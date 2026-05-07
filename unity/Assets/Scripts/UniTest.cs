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
    private const int clipSampleLength = 24000 * 5;           // Clip 长度：5秒 (16kHz采样率)
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

        // ---------- 初始化音频播放器 ----------
        // 添加 AudioSource 组件（如果不存在）
        audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        // 创建动态音频剪辑，用于流式播放
        dynamicClip = AudioClip.Create("StreamAudio", clipSampleLength, 1, 24000, true, OnAudioRead);
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
                //Debug.Log("发送心跳消息");
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

        // 将 short[] 转为 byte[] 并发送
        byte[] byteBuffer = new byte[pcmData.Length * 2];
        Buffer.BlockCopy(pcmData, 0, byteBuffer, 0, byteBuffer.Length);

        if (ws != null && ws.ReadyState == WebSocketState.Open)
        {
            ws.SendAsync(byteBuffer);
        }
    }

    private void OnOpen(object sender, OpenEventArgs e)
    {
        Debug.Log("WS connected!");
    }

    float[] receive;
    private void OnMessage(object sender, MessageEventArgs e)
    {
        if (e.IsBinary)
        {
            // 收到二进制数据（PCM 音频流）
            byte[] audioBytes = e.RawData;
            if (audioBytes == null || audioBytes.Length == 0)
            {
                return;
            }
            receive = Util.ConvertPCMBytesToFloat(audioBytes);
            receiveBuffer.AddRange(receive);
            lock (queueLock)
            {
                // 将字节数据加入队列
                foreach (byte b in audioBytes)
                {
                    audioDataQueue.Enqueue(b);
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
        // data 是需要填充的音频缓冲区，长度为 2 * 采样数（因为双声道？但我们创建的是单声道，所以 data.Length 等于帧数）
        // data 是 float 数组，范围 -1..1
        int sampleCount = data.Length;   // 单声道：sampleCount 就是需要填充的采样点数

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
    }
}