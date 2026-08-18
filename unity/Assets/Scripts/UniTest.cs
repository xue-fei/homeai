using System;
using System.Collections.Generic;
using System.IO;
using uMicrophoneWebGL;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityWebSocket;

public class UniTest : MonoBehaviour
{
    IWebSocket ws;
    private float nextExecuteTime = 0f;
    MicrophoneWebGL microphone;
    public Button button;

    List<float> recorderBuffer = new List<float>();
    List<float> receiveBuffer = new List<float>();
    string tempPath;

    // ---------- 流式音频播放相关 ----------
    private AudioSource audioSource;
    private AudioClip dynamicClip;
    private Queue<byte> audioDataQueue = new Queue<byte>();   // 存放接收到的 PCM 字节
    private object queueLock = new object();                  // 队列锁（线程安全）
    private bool isPlaying = false;                           // 是否正在播放
    private const int clipSampleLength = 24000 * 5;           // Clip 长度：5秒
    // -----------------------------------

    // ---------- Opus 编解码相关 ----------
    private OpusCodec opusEncoder;
    private OpusCodec opusDecoder;
    private int opusFrameSize = 320; // 20ms @ 16kHz
    private List<byte> opusPcmBuffer = new List<byte>(); // PCM 帧缓冲区

    // Opus 解码状态
    private List<byte> opusDecodeBuffer = new List<byte>();
    private bool opusDecodeReadingLength = true;
    private int opusDecodePacketLength = 0;
    // -----------------------------------

    // Start is called before the first frame update
    void Start()
    {
        microphone = GetComponent<MicrophoneWebGL>();
        microphone.isAutoStart = false;
        microphone.dataEvent.AddListener(OnData);

        ws = new WebSocket("ws://192.168.2.177:9999");
        ws.OnOpen += OnOpen;
        ws.OnMessage += OnMessage;
        ws.OnError += OnError;
        ws.OnClose += OnClose;
        ws.ConnectAsync();

        UnityAction<BaseEventData> down = new UnityAction<BaseEventData>(PointerDown);
        EventTrigger.Entry eDown = new EventTrigger.Entry();
        eDown.eventID = EventTriggerType.PointerDown;
        eDown.callback.AddListener(down);
        EventTrigger etDown = button.gameObject.AddComponent<EventTrigger>();
        etDown.triggers.Add(eDown);

        UnityAction<BaseEventData> up = new UnityAction<BaseEventData>(PointerUp);
        EventTrigger.Entry eUp = new EventTrigger.Entry();
        eUp.eventID = EventTriggerType.PointerUp;
        eUp.callback.AddListener(up);
        EventTrigger etUp = button.gameObject.AddComponent<EventTrigger>();
        etUp.triggers.Add(eUp);

#if UNITY_EDITOR
        tempPath = Application.dataPath + "/Temp";
        if (!Directory.Exists(tempPath))
        {
            Directory.CreateDirectory(tempPath);
        }
#endif

        // ---------- 初始化 Opus 编解码器 ----------
        try
        {
            opusEncoder = new OpusCodec(16000, 1, 16000);
            opusDecoder = new OpusCodec(16000, 1, 16000);
            Debug.Log("[Opus] 编解码器已创建");
        }
        catch (Exception e)
        {
            Debug.LogError("[Opus] 编解码器创建失败: " + e.Message);
        }
        // ----------------------------------------

        // ---------- 初始化音频播放器 ----------
        // 添加 AudioSource 组件（如果不存在）
        audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        // 创建动态音频剪辑，用于流式播放
        dynamicClip = AudioClip.Create("StreamAudio", clipSampleLength, 1, 16000, true, OnAudioRead);
        audioSource.clip = dynamicClip;
        audioSource.loop = true;
        // 循环播放以便持续补充数据          
        // --------------------------------- 
    }

    // Update is called once per frame
    void Update()
    {
        if (Time.time >= nextExecuteTime)
        {
            nextExecuteTime = Time.time + 1f;
            if (ws != null && ws.ReadyState == WebSocketState.Open)
            {
                ws.SendAsync("{\"code\":0,\"message\":\"心跳消息\"}");
                Debug.Log("发送心跳消息");
            }
        }
        // 如果已经在播放但队列长时间为空（约1秒），可以自动暂停（可选）
        if (isPlaying && audioDataQueue.Count == 0 && audioSource.isPlaying)
        {
            // 队列空，暂停播放（避免无声播放）
            audioSource.Pause();
        }
        else if (!isPlaying && audioDataQueue.Count > 0 && !audioSource.isPlaying)
        {
            // 有数据且未播放，开始播放
            audioSource.Play();
            isPlaying = true;
        }
    }

    void PointerDown(BaseEventData data)
    {
        Debug.LogWarning("按下");
        recorderBuffer.Clear();
        microphone.Begin();
        if (ws != null && ws.ReadyState == WebSocketState.Open)
        {
            ws.SendAsync("{\"code\":1,\"message\":\"开始语音\"}");
            Debug.Log("开始语音");
        }
        // 新的一轮语音开始前，清空音频播放队列并停止播放
        lock (queueLock)
        {
            audioDataQueue.Clear();
        }
        if (audioSource.isPlaying)
        {
            audioSource.Stop();
        }
        isPlaying = false;
        if (receiveBuffer.Count > 0)
        {
            Util.SaveClip(1, 24000, receiveBuffer.ToArray(),
                Application.dataPath + "/" + DateTime.Now.ToFileTime() + ".wav");
        }
        receiveBuffer.Clear();
    }

    void PointerUp(BaseEventData data)
    {
        Debug.LogWarning("抬起");
        microphone.End();
        if (ws != null && ws.ReadyState == WebSocketState.Open)
        {
            ws.SendAsync("{\"code\":2,\"message\":\"结束语音\"}");
            Debug.Log("结束语音");
        }
#if UNITY_EDITOR
        string tempFile = tempPath + "/" + DateTime.Now.ToFileTime() + ".wav";
        Util.SaveClip(1, 16000, recorderBuffer.ToArray(), tempFile);
#endif
    }

    void OnData(float[] input)
    {
        recorderBuffer.AddRange(input);

        // 将 float[] 转换为 short[] (16-bit PCM)
        short[] pcmData = new short[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            float sample = input[i];
            // 钳位到 [-1, 1] 范围，然后转换为 short
            sample = Mathf.Clamp(sample, -1f, 1f);
            pcmData[i] = (short)(sample * 32767f);
        }

        // 将 short[] 转为 byte[]
        byte[] pcmBytes = new byte[pcmData.Length * 2];
        Buffer.BlockCopy(pcmData, 0, pcmBytes, 0, pcmBytes.Length);

        if (ws != null && ws.ReadyState == WebSocketState.Open)
        {
            if (opusEncoder != null)
            {
                // 使用 Opus 编码发送
                EncodeAndSendOpus(pcmBytes);
            }
            else
            {
                // 兼容模式：直接发送 PCM
                ws.SendAsync(pcmBytes);
            }
        }
    }

    /// <summary>
    /// 将 PCM 数据缓冲并编码为 Opus 包发送
    /// </summary>
    private void EncodeAndSendOpus(byte[] pcmBytes)
    {
        opusPcmBuffer.AddRange(pcmBytes);

        int frameBytes = opusFrameSize * 2; // 16-bit = 2 bytes per sample
        while (opusPcmBuffer.Count >= frameBytes)
        {
            byte[] frame = opusPcmBuffer.GetRange(0, frameBytes).ToArray();
            opusPcmBuffer.RemoveRange(0, frameBytes);

            try
            {
                byte[] opusPacket = opusEncoder.Encode(frame);
                if (opusPacket != null)
                {
                    // 合并长度前缀（2字节，小端序）+ Opus 数据，作为单个 WebSocket 帧发送
                    byte[] frameBuffer = new byte[2 + opusPacket.Length];
                    frameBuffer[0] = (byte)(opusPacket.Length & 0xFF);
                    frameBuffer[1] = (byte)(opusPacket.Length >> 8 & 0xFF);
                    Buffer.BlockCopy(opusPacket, 0, frameBuffer, 2, opusPacket.Length);
                    ws.SendAsync(frameBuffer);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Opus] 编码失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 从接收到的数据中解析 Opus 包并解码为 PCM
    /// </summary>
    private void DecodeAndQueueOpus(byte[] data)
    {
        opusDecodeBuffer.AddRange(data);

        while (opusDecodeBuffer.Count > 0)
        {
            if (opusDecodeReadingLength)
            {
                if (opusDecodeBuffer.Count < 2)
                    break;

                opusDecodePacketLength = opusDecodeBuffer[0] | (opusDecodeBuffer[1] << 8);
                opusDecodeBuffer.RemoveRange(0, 2);
                opusDecodeReadingLength = false;
            }
            else
            {
                if (opusDecodeBuffer.Count < opusDecodePacketLength)
                    break;

                byte[] opusPacket = opusDecodeBuffer.GetRange(0, opusDecodePacketLength).ToArray();
                opusDecodeBuffer.RemoveRange(0, opusDecodePacketLength);
                opusDecodeReadingLength = true;

                try
                {
                    byte[] pcmBytes = opusDecoder.DecodeToBytes(opusPacket);
                    if (pcmBytes != null)
                    {
                        // 将解码后的 PCM 数据加入播放队列
                        lock (queueLock)
                        {
                            foreach (byte b in pcmBytes)
                            {
                                audioDataQueue.Enqueue(b);
                            }
                        }

                        // 同时保存到 receiveBuffer（用于保存文件）
                        float[] floatData = Util.ConvertPCMBytesToFloat(pcmBytes);
                        if (floatData != null)
                        {
                            receiveBuffer.AddRange(floatData);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[Opus] 解码失败: " + e.Message);
                }
            }
        }
    }

    private void OnOpen(object sender, OpenEventArgs e)
    {
        Debug.Log("WS connected!");
        ws.SendAsync("{\"code\":-1,\"message\":\"unity已连接\"}");
    }

    float[] receive;
    private void OnMessage(object sender, MessageEventArgs e)
    {
        if (e.IsBinary)
        {
            // 收到二进制数据（Opus 编码音频）
            byte[] audioBytes = e.RawData;
            if (audioBytes == null || audioBytes.Length == 0)
            {
                return;
            }

            if (opusDecoder != null)
            {
                // 使用 Opus 解码
                DecodeAndQueueOpus(audioBytes);
            }
            else
            {
                // 兼容模式：直接处理 PCM
                receive = Util.ConvertPCMBytesToFloat(audioBytes);
                receiveBuffer.AddRange(receive);
                lock (queueLock)
                {
                    foreach (byte b in audioBytes)
                    {
                        audioDataQueue.Enqueue(b);
                    }
                }
            }
        }
        else if (e.IsText)
        {
            Debug.Log("WS received message: " + e.Data);
        }
    }

    private void OnError(object sender, UnityWebSocket.ErrorEventArgs e)
    {
        Debug.Log("WS error: " + e.Message);
    }

    private void OnClose(object sender, CloseEventArgs e)
    {
        Debug.Log(string.Format("Closed: StatusCode: {0}, Reason: {1}", e.StatusCode, e.Reason));
        // 关闭播放
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
        isPlaying = false;
    }

    // ---------- 音频回调函数 ----------
    // 当 Unity 音频引擎需要更多数据时调用
    private void OnAudioRead(float[] data)
    {
        // data 是需要填充的音频缓冲区
        int sampleCount = data.Length;

        // 每帧需要读取 sampleCount 个 float 样本
        for (int i = 0; i < sampleCount; i++)
        {
            if (TryDequeueShort(out short pcmValue))
            {
                // 将 16-bit PCM 转换为 float （-32768..32767 => -1..1）
                data[i] = pcmValue / 32768f;
            }
            else
            {
                // 队列中无数据，填充静音（避免杂音）
                data[i] = 0f;
            }
        }
    }

    // 从字节队列中取出一个完整的 16-bit 样本（小端序），返回 true 表示成功
    private bool TryDequeueShort(out short result)
    {
        result = 0;
        lock (queueLock)
        {
            if (audioDataQueue.Count < 2)
            {
                return false;
            }
            byte low = audioDataQueue.Dequeue();
            byte high = audioDataQueue.Dequeue();
            result = (short)((high << 8) | low);
            return true;
        }
    }

    private void OnApplicationQuit()
    {
        if (ws != null && ws.ReadyState != WebSocketState.Closed)
        {
            ws.CloseAsync();
        }
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
    }

    private void OnDestroy()
    {
        // 释放资源
        if (dynamicClip != null)
        {
            Destroy(dynamicClip);
        }
        if (opusEncoder != null)
        {
            opusEncoder.Dispose();
        }
        if (opusDecoder != null)
        {
            opusDecoder.Dispose();
        }
    }
}