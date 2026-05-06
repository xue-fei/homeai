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

    List<float> buffer = new List<float>();
    string tempPath;

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
    }

    void PointerDown(BaseEventData data)
    {
        Debug.LogWarning("按下");
        buffer.Clear();
        microphone.Begin();
        if (ws != null && ws.ReadyState == WebSocketState.Open)
        {
            ws.SendAsync("{\"code\":1,\"message\":\"开始语音\"}");
            Debug.Log("开始语音");
        }
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
        Util.SaveClip(1, 16000, buffer.ToArray(), tempFile);
#endif
    }

    void OnData(float[] input)
    {
        buffer.AddRange(input);

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

    private void OnMessage(object sender, MessageEventArgs e)
    {
        if (e.IsBinary)
        {
            //string str = Encoding.UTF8.GetString(e.RawData);
            //Debug.Log("WS received message: " + str); 
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
    }

    private void OnApplicationQuit()
    {
        if (ws != null && ws.ReadyState != WebSocketState.Closed)
        {
            ws.CloseAsync();
        }
    }
}