using System;
using System.Collections.Generic;
using System.IO;
using uMicrophoneWebGL;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityWebSocket;

/// <summary>
/// HomeAI Unity 客户端 —— 纯 PCM 直传版
///
/// 协议（与 ESP32 / Server 完全一致，无任何编解码、无长度前缀、无比特率概念）：
///   上行 : Binary = 16000Hz / 16bit / mono 小端裸 PCM
///   下行 : Binary = 同上，服务端按 640 字节（20ms）一帧节拍发送
///   控制 : Text   = {"code":n,"message":"..."}
///
/// 播放：统一走固定长度环形缓冲，但输出层分两条路径
///   - WebGL      : 分块 AudioClip + PlayScheduled 无缝排队
///                  （WebGL 的音频由 WebAudio 实现，不支持 OnAudioFilterRead）
///   - 其他平台   : OnAudioFilterRead 直接灌样本，延迟最低
///
/// 两条路径都做到：不落盘 WAV、AudioClip 池化复用、运行期零增长内存。
/// </summary>
public class UniTest : MonoBehaviour
{
    IWebSocket ws;
    private float nextExecuteTime = 0f;
    MicrophoneWebGL microphone;
    public Button button;

    // ================== 音频格式（全链路唯一一套）==================
    private const int SAMPLE_RATE = 16000;
    private const int FRAME_SAMPLES = 320;                     // 20ms
    private const int FRAME_BYTES = FRAME_SAMPLES * 2;         // 640 字节

    // ================== 播放：固定长度环形缓冲 ==================
    private const int RING_CAPACITY = SAMPLE_RATE * 8;         // 8 秒，满则丢最旧
    private const int PREBUFFER_SAMPLES = SAMPLE_RATE / 5;     // 200ms 预缓冲后开声

    private readonly float[] ring = new float[RING_CAPACITY];
    private int ringWrite = 0;
    private int ringRead = 0;
    private int ringCount = 0;
    private readonly object ringLock = new object();
    private bool streaming = false;

    private bool isRecording = false;

    // 下行拼帧：WebSocket 帧不保证是 640 字节整数倍
    private readonly List<byte> rxAssemble = new List<byte>(FRAME_BYTES * 4);

    // ================== 上行 ==================
    private const int TX_CHUNK_BYTES = FRAME_BYTES * 2;        // 40ms 一发
    private readonly List<byte> txBuffer = new List<byte>(TX_CHUNK_BYTES * 2);

#if UNITY_EDITOR
    private readonly List<float> recorderBuffer = new List<float>();
    private string tempPath;
#endif

    // ================== 播放输出层 ==================
#if UNITY_WEBGL && !UNITY_EDITOR
    // WebGL：分块调度播放
    private const int CHUNK_SAMPLES = SAMPLE_RATE / 5;         // 200ms 一块
    private const int POOL_SIZE = 4;                           // 4 块 = 800ms 排队深度
    private const double SCHEDULE_AHEAD = 0.4;                 // 提前排队上限（秒）

    private AudioSource[] sourcePool;
    private AudioClip[] clipPool;
    private readonly float[] chunkScratch = new float[CHUNK_SAMPLES];
    private int poolIndex = 0;
    private double nextDspTime = 0d;
#else
    // 其他平台：OnAudioFilterRead 流式灌入
    private AudioSource audioSource;
    private float lastSample = 0f;                             // 欠载淡出用
#endif

    void Start()
    {
        microphone = GetComponent<MicrophoneWebGL>();
        microphone.isAutoStart = false;
        microphone.dataEvent.AddListener(OnData);

        ws = new WebSocket("ws://172.32.151.240:9999");
        ws.OnOpen += OnOpen;
        ws.OnMessage += OnMessage;
        ws.OnError += OnError;
        ws.OnClose += OnClose;
        ws.ConnectAsync();

        BindButtonEvents();

#if UNITY_EDITOR
        tempPath = Application.dataPath + "/Temp";
        if (!Directory.Exists(tempPath))
        {
            Directory.CreateDirectory(tempPath);
        }
#endif

        SetupPlayback();

        Debug.Log($"[HomeAI] 纯 PCM 模式  {SAMPLE_RATE}Hz/16bit/mono  帧={FRAME_BYTES}字节  " +
                  $"环形缓冲={RING_CAPACITY / (float)SAMPLE_RATE:F1}s  预缓冲={PREBUFFER_SAMPLES * 1000 / SAMPLE_RATE}ms");
    }

    private void BindButtonEvents()
    {
        EventTrigger trigger = button.gameObject.GetComponent<EventTrigger>();
        if (trigger == null)
        {
            // 原实现给同一对象 AddComponent 两次 EventTrigger，第二个会被 Unity 忽略；
            // 这里复用同一个 EventTrigger 挂两个 entry，保证按下和松开都能触发
            trigger = button.gameObject.AddComponent<EventTrigger>();
        }

        var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
        down.callback.AddListener(new UnityAction<BaseEventData>(PointerDown));
        trigger.triggers.Add(down);

        var up = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
        up.callback.AddListener(new UnityAction<BaseEventData>(PointerUp));
        trigger.triggers.Add(up);
    }

    void Update()
    {
        // 心跳（每秒一次）
        if (Time.time >= nextExecuteTime)
        {
            nextExecuteTime = Time.time + 1f;
            if (ws != null && ws.ReadyState == WebSocketState.Open)
            {
                ws.SendAsync("{\"code\":0,\"message\":\"心跳消息\"}");
            }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        PumpScheduledPlayback();
#endif
    }

    // ================================================================
    // 播放层初始化
    // ================================================================
    private void SetupPlayback()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        sourcePool = new AudioSource[POOL_SIZE];
        clipPool = new AudioClip[POOL_SIZE];
        for (int i = 0; i < POOL_SIZE; i++)
        {
            var go = new GameObject("PcmChunk_" + i);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            sourcePool[i] = src;

            // 池化 clip：长度固定，靠 SetData 覆盖内容，永不重复创建
            clipPool[i] = AudioClip.Create("PcmChunk_" + i, CHUNK_SAMPLES, 1, SAMPLE_RATE, false);
            src.clip = clipPool[i];
        }
#else
        audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        // 静音载体，只为让 OnAudioFilterRead 持续被音频线程调用
        AudioClip carrier = AudioClip.Create("PcmStreamCarrier", SAMPLE_RATE, 1, SAMPLE_RATE, false);
        carrier.SetData(new float[SAMPLE_RATE], 0);
        audioSource.clip = carrier;
        audioSource.loop = true;
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        audioSource.Play();
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    /// <summary>
    /// WebGL 播放泵：从环形缓冲取整块，写进池化 clip，按 dspTime 无缝排队。
    /// 每块首尾各做 2ms 淡入淡出，消除块边界的咔哒声。
    /// </summary>
    private void PumpScheduledPlayback()
    {
        if (isRecording) return;

        double now = AudioSettings.dspTime;
        if (nextDspTime < now) nextDspTime = now + 0.05;   // 落后了就重新对基准

        while (nextDspTime - now < SCHEDULE_AHEAD)
        {
            lock (ringLock)
            {
                if (!streaming)
                {
                    if (ringCount < PREBUFFER_SAMPLES) return;
                    streaming = true;
                }
                if (ringCount < CHUNK_SAMPLES)
                {
                    // 数据不够一整块：退回预缓冲，等攒够再继续（避免半块拼接产生断续噪声）
                    streaming = false;
                    return;
                }

                for (int i = 0; i < CHUNK_SAMPLES; i++)
                {
                    chunkScratch[i] = ring[ringRead];
                    ringRead = (ringRead + 1) % RING_CAPACITY;
                    ringCount--;
                }
            }

            // 边界淡入淡出（32 样本 ≈ 2ms）
            const int fade = 32;
            for (int i = 0; i < fade; i++)
            {
                float g = i / (float)fade;
                chunkScratch[i] *= g;
                chunkScratch[CHUNK_SAMPLES - 1 - i] *= g;
            }

            var src = sourcePool[poolIndex];
            clipPool[poolIndex].SetData(chunkScratch, 0);
            src.Stop();
            src.PlayScheduled(nextDspTime);

            poolIndex = (poolIndex + 1) % POOL_SIZE;
            nextDspTime += CHUNK_SAMPLES / (double)SAMPLE_RATE;
        }
    }
#else
    /// <summary>
    /// 音频线程回调：把环形缓冲的数据灌进输出。
    /// 运行在音频线程，禁止调用 Unity API、禁止分配内存。
    /// </summary>
    void OnAudioFilterRead(float[] data, int channels)
    {
        int frames = data.Length / channels;

        lock (ringLock)
        {
            if (isRecording)
            {
                streaming = false;
            }
            else if (!streaming && ringCount >= PREBUFFER_SAMPLES)
            {
                streaming = true;
            }

            if (!streaming)
            {
                Array.Clear(data, 0, data.Length);
                lastSample = 0f;
                return;
            }

            for (int f = 0; f < frames; f++)
            {
                float v;
                if (ringCount > 0)
                {
                    v = ring[ringRead];
                    ringRead = (ringRead + 1) % RING_CAPACITY;
                    ringCount--;
                    lastSample = v;
                }
                else
                {
                    // 欠载：指数衰减淡出，避免硬切造成咔哒/爆音
                    lastSample *= 0.85f;
                    if (lastSample < 1e-4f && lastSample > -1e-4f) lastSample = 0f;
                    v = lastSample;
                    streaming = false;   // 退回预缓冲，等数据攒够再继续
                }

                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    data[baseIdx + c] = v;
                }
            }
        }
    }
#endif

    // ================================================================
    // 录音控制
    // ================================================================
    void PointerDown(BaseEventData data)
    {
        Debug.Log("[录音] 按下按钮");
        isRecording = true;
        txBuffer.Clear();
#if UNITY_EDITOR
        recorderBuffer.Clear();
#endif
        microphone.Begin();

        // 立即停声并清空所有下行缓冲，杜绝上一轮尾音混入
        ResetPlayback();

        if (ws != null && ws.ReadyState == WebSocketState.Open)
        {
            ws.SendAsync("{\"code\":1,\"message\":\"开始语音\"}");
        }
    }

    void PointerUp(BaseEventData data)
    {
        Debug.Log("[录音] 松开按钮");
        isRecording = false;
        microphone.End();

        FlushTxBuffer();   // 补发不足一片的尾巴，避免最后一个字被截断

        if (ws != null && ws.ReadyState == WebSocketState.Open)
        {
            ws.SendAsync("{\"code\":2,\"message\":\"结束语音\"}");
        }

#if UNITY_EDITOR
        if (recorderBuffer.Count > 0)
        {
            try
            {
                string tempFile = tempPath + "/" + DateTime.Now.ToFileTime() + "_record.wav";
                Util.SaveClip(1, SAMPLE_RATE, recorderBuffer.ToArray(), tempFile);
                Debug.Log($"[录音] 已保存：{tempFile}");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[录音] 保存失败（不影响链路）: " + e.Message);
            }
        }
#endif
    }

    // ================================================================
    // 上行：麦克风 float → 16bit PCM → 直发
    // ================================================================
    void OnData(float[] input)
    {
        if (input == null || input.Length == 0) return;

#if UNITY_EDITOR
        recorderBuffer.AddRange(input);
#endif

        if (ws == null || ws.ReadyState != WebSocketState.Open) return;

        byte[] pcmBytes = new byte[input.Length * 2];
        for (int i = 0; i < input.Length; i++)
        {
            float sample = input[i];
            if (sample > 1f) sample = 1f;
            else if (sample < -1f) sample = -1f;
            short s = (short)(sample * 32767f);
            pcmBytes[i * 2] = (byte)(s & 0xFF);
            pcmBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        txBuffer.AddRange(pcmBytes);
        while (txBuffer.Count >= TX_CHUNK_BYTES)
        {
            byte[] chunk = new byte[TX_CHUNK_BYTES];
            txBuffer.CopyTo(0, chunk, 0, TX_CHUNK_BYTES);
            txBuffer.RemoveRange(0, TX_CHUNK_BYTES);
            ws.SendAsync(chunk);
        }
    }

    private void FlushTxBuffer()
    {
        if (ws == null || ws.ReadyState != WebSocketState.Open)
        {
            txBuffer.Clear();
            return;
        }

        int len = txBuffer.Count & ~1;   // 偶数字节 = 完整采样
        if (len > 0)
        {
            byte[] chunk = new byte[len];
            txBuffer.CopyTo(0, chunk, 0, len);
            ws.SendAsync(chunk);
        }
        txBuffer.Clear();
    }

    // ================================================================
    // 下行：裸 PCM → 环形缓冲
    // ================================================================
    private void OnPcmReceived(byte[] data)
    {
        rxAssemble.AddRange(data);

        while (rxAssemble.Count >= FRAME_BYTES)
        {
            lock (ringLock)
            {
                for (int i = 0; i < FRAME_SAMPLES; i++)
                {
                    short pcm = (short)((rxAssemble[i * 2 + 1] << 8) | rxAssemble[i * 2]);
                    RingPushUnsafe(pcm / 32768f);
                }
            }
            rxAssemble.RemoveRange(0, FRAME_BYTES);
        }
    }

    /// <summary>调用方必须已持有 ringLock</summary>
    private void RingPushUnsafe(float sample)
    {
        if (ringCount >= RING_CAPACITY)
        {
            // 缓冲满：丢最旧一个样本，容量恒定，内存不增长
            ringRead = (ringRead + 1) % RING_CAPACITY;
            ringCount--;
        }
        ring[ringWrite] = sample;
        ringWrite = (ringWrite + 1) % RING_CAPACITY;
        ringCount++;
    }

    private void ResetPlayback()
    {
        lock (ringLock)
        {
            ringWrite = 0;
            ringRead = 0;
            ringCount = 0;
            streaming = false;
        }
        rxAssemble.Clear();

#if UNITY_WEBGL && !UNITY_EDITOR
        if (sourcePool != null)
        {
            for (int i = 0; i < sourcePool.Length; i++)
            {
                if (sourcePool[i] != null) sourcePool[i].Stop();
            }
        }
        poolIndex = 0;
        nextDspTime = 0d;
#else
        lastSample = 0f;
#endif
    }

    // ================================================================
    // WebSocket 事件
    // ================================================================
    private void OnOpen(object sender, OpenEventArgs e)
    {
        Debug.Log("[WebSocket] 已连接");
        ResetPlayback();
        ws.SendAsync("{\"code\":-1,\"message\":\"unity已连接\"}");
    }

    private void OnMessage(object sender, MessageEventArgs e)
    {
        if (e.IsBinary)
        {
            byte[] pcm = e.RawData;
            if (pcm == null || pcm.Length == 0) return;
            if (isRecording) return;              // 录音期间丢弃下行，防止自己听自己
            OnPcmReceived(pcm);
        }
        else if (e.IsText)
        {
            Debug.Log("[WebSocket] 收到文本消息: " + e.Data);
        }
    }

    private void OnError(object sender, UnityWebSocket.ErrorEventArgs e)
    {
        Debug.LogError("[WebSocket] 错误: " + e.Message);
    }

    private void OnClose(object sender, CloseEventArgs e)
    {
        Debug.Log($"[WebSocket] 已断开，状态码: {e.StatusCode}，原因: {e.Reason}");
        ResetPlayback();
    }

    private void OnApplicationQuit()
    {
        if (ws != null && ws.ReadyState != WebSocketState.Closed)
        {
            ws.CloseAsync();
        }
    }

    private void OnDestroy()
    {
        if (ws != null)
        {
            ws.OnOpen -= OnOpen;
            ws.OnMessage -= OnMessage;
            ws.OnError -= OnError;
            ws.OnClose -= OnClose;
            if (ws.ReadyState != WebSocketState.Closed)
            {
                ws.CloseAsync();
            }
            ws = null;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        if (clipPool != null)
        {
            for (int i = 0; i < clipPool.Length; i++)
            {
                if (sourcePool != null && sourcePool[i] != null) sourcePool[i].Stop();
                if (clipPool[i] != null) Destroy(clipPool[i]);
            }
        }
#else
        if (audioSource != null)
        {
            audioSource.Stop();
            if (audioSource.clip != null)
            {
                Destroy(audioSource.clip);
                audioSource.clip = null;
            }
        }
#endif
    }
}
