/*
 * HomeAI ESP32-S3 客户端 —— 纯 PCM 直传版
 *
 * 协议（全链路 16000Hz / 16bit / 单声道 / 小端序裸 PCM，无任何编解码、无长度前缀）：
 *   上行 (ESP32 -> Server) : WebSocket Binary，裸 PCM 字节流
 *   下行 (Server -> ESP32) : WebSocket Binary，裸 PCM 字节流（服务端按 20ms/帧 节拍发送）
 *   控制                    : WebSocket Text  {"code":n,"message":"..."}
 *                             -1=已连接 0=心跳 1=开始说话 2=结束说话
 *
 * 稳定性设计（防止爆内存 / 播放噪音）：
 *   1. 播放缓冲为「静态数组」环形队列，编译期确定大小，运行时零动态分配 —— 堆不可能被音频撑爆
 *   2. 队列满时直接丢弃新数据（有界背压），而不是无限堆积
 *   3. 播放采用「预缓冲 + DMA 背压」状态机：先攒够 PREBUFFER_FRAMES 再开声，
 *      期间不向 I2S 写任何东西；欠载时自动退回预缓冲态，靠 tx_desc_auto_clear
 *      让硬件输出静音，避免手工插零帧造成的爆音/断续噪声
 *   4. 主循环全程非阻塞（i2s_read / i2s_write 超时均为 0），保证 webSocket.loop()
 *      被高频调用，TCP 接收窗口不会积压 -> lwIP 不会吃光内存
 *   5. 开始录音时立即清空播放队列并冲掉 DMA 残留，杜绝上一轮尾音混入
 *   6. 周期打印堆水位，便于观察是否仍有泄漏
 */

#include <WiFi.h>
#include <WebSocketsClient.h>
#include <driver/i2s.h>
#include <esp_system.h>

// 已移除 libopus，栈需求大幅下降；留 16KB 供 WiFi/lwIP 回调使用
SET_LOOP_TASK_STACK_SIZE(16 * 1024);

// ================== Wi-Fi / WebSocket 配置 ==================
const char* ssid = "206";
const char* password = "unityioslinux1";

const char* websocketServer = "192.168.2.177";
const int websocketPort = 9999;
const char* websocketPath = "/";

// ================== I2S 引脚 ==================
// MAX98357A（输出）
#define I2S_OUT_PORT I2S_NUM_1
#define I2S_OUT_BCLK 15
#define I2S_OUT_LRC 16
#define I2S_OUT_DOUT 7

// INMP441（输入）
#define I2S_IN_PORT I2S_NUM_0
#define I2S_IN_BCLK 4
#define I2S_IN_LRC 5
#define I2S_IN_DIN 6

// ================== 音频格式（全链路唯一一套参数）==================
#define SAMPLE_RATE 16000
#define FRAME_SAMPLES 320                        // 20ms @ 16kHz
#define FRAME_BYTES (FRAME_SAMPLES * 2)          // 640 字节

// ================== 播放抖动缓冲（静态内存，绝不动态分配）==================
#define JITTER_FRAMES 24                         // 24 * 20ms = 480ms 上限（约 15KB 静态 RAM）
#define PREBUFFER_FRAMES 6                       // 攒够 120ms 才开声
static int16_t jitterBuf[JITTER_FRAMES][FRAME_SAMPLES];
static int jitterWrite = 0;
static int jitterRead = 0;
static int jitterCount = 0;
static bool playing = false;                     // false = 预缓冲中，true = 正在放音
static int frameOffset = 0;                      // 当前帧已写入 I2S 的字节数（断点续写）
static uint32_t dropFrames = 0;                  // 因缓冲满而丢弃的帧数（诊断用）

// 下行 PCM 字节拼帧缓冲（WebSocket 帧边界不保证与 640 字节对齐）
static uint8_t rxAssemble[FRAME_BYTES];
static int rxAssembleLen = 0;

// ================== 上行录音缓冲（静态）==================
#define MIC_READ_BYTES 640                       // 每次尝试读 20ms
#define TX_FRAME_BYTES 1280                      // 攒到 40ms 再发，减少 WS 帧开销
static uint8_t micRaw[MIC_READ_BYTES];
static uint8_t txBuf[TX_FRAME_BYTES];
static int txBufLen = 0;

// ================== 按钮 ==================
#define PIN_BUTTON 47
static bool pressed = false;
static bool lastReading = false;
static unsigned long lastDebounceTime = 0;
static const unsigned long debounceDelay = 50;

// ================== 心跳 / 诊断计时 ==================
WebSocketsClient webSocket;
static unsigned long lastHeartbeat = 0;
static const unsigned long heartbeatInterval = 1000;
static unsigned long lastHeapReport = 0;
static const unsigned long heapReportInterval = 5000;

// ================================================================
// 抖动缓冲操作
// 说明：webSocketEvent 由 webSocket.loop() 在 loop 任务内同步调用，
// 与消费端处于同一任务、顺序执行，不存在真正的并发，普通 int 即可。
// ================================================================
static inline bool jitterPush(const int16_t* frame) {
  if (jitterCount >= JITTER_FRAMES) {
    dropFrames++;
    return false;                                // 有界丢弃，堆内存不会增长
  }
  memcpy(jitterBuf[jitterWrite], frame, FRAME_BYTES);
  jitterWrite = (jitterWrite + 1) % JITTER_FRAMES;
  jitterCount++;
  return true;
}

static inline void jitterReset() {
  jitterWrite = 0;
  jitterRead = 0;
  jitterCount = 0;
  playing = false;
  frameOffset = 0;
  rxAssembleLen = 0;
}

void setup() {
  Serial.begin(115200);
  delay(200);
  Serial.println();
  Serial.println("[HomeAI] 纯 PCM 模式启动（16000Hz/16bit/mono，无编解码）");

  // ---------- Wi-Fi ----------
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);                          // 关掉省电，避免音频链路周期性卡顿
  WiFi.begin(ssid, password);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println();
  Serial.print("[WiFi] 已连接 IP=");
  Serial.println(WiFi.localIP());

  // ---------- I2S 输入（麦克风）----------
  i2s_config_t i2s_config_in = {
    .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_RX),
    .sample_rate = SAMPLE_RATE,
    .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
    .communication_format = i2s_comm_format_t(I2S_COMM_FORMAT_STAND_I2S),
    .intr_alloc_flags = ESP_INTR_FLAG_LEVEL1,
    .dma_buf_count = 4,
    .dma_buf_len = FRAME_SAMPLES,                // 4 * 20ms = 80ms
    .use_apll = false,
    .tx_desc_auto_clear = false,
    .fixed_mclk = 0
  };
  i2s_pin_config_t pin_config_in = {
    .bck_io_num = I2S_IN_BCLK,
    .ws_io_num = I2S_IN_LRC,
    .data_out_num = I2S_PIN_NO_CHANGE,
    .data_in_num = I2S_IN_DIN
  };

  // ---------- I2S 输出（功放）----------
  // tx_desc_auto_clear = true 是关键：欠载时硬件自动填 0，
  // 不会重复播放上一帧残留数据（那正是"噪音/嗡嗡声"的主要来源）
  i2s_config_t i2s_config_out = {
    .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_TX),
    .sample_rate = SAMPLE_RATE,
    .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_RIGHT,
    .communication_format = (i2s_comm_format_t)(I2S_COMM_FORMAT_STAND_I2S),
    .intr_alloc_flags = ESP_INTR_FLAG_LEVEL1,
    .dma_buf_count = 8,
    .dma_buf_len = FRAME_SAMPLES,                // 8 * 20ms = 160ms 硬件缓冲
    .use_apll = true,
    .tx_desc_auto_clear = true,
    .fixed_mclk = 256 * SAMPLE_RATE
  };
  i2s_pin_config_t pin_config_out = {
    .bck_io_num = I2S_OUT_BCLK,
    .ws_io_num = I2S_OUT_LRC,
    .data_out_num = I2S_OUT_DOUT,
    .data_in_num = I2S_PIN_NO_CHANGE
  };

  i2s_driver_install(I2S_IN_PORT, &i2s_config_in, 0, NULL);
  i2s_set_pin(I2S_IN_PORT, &pin_config_in);
  i2s_driver_install(I2S_OUT_PORT, &i2s_config_out, 0, NULL);
  i2s_set_pin(I2S_OUT_PORT, &pin_config_out);
  i2s_zero_dma_buffer(I2S_OUT_PORT);

  Serial.printf("[I2S] 就绪  抖动缓冲=%d帧(%dms)  预缓冲=%d帧(%dms)  静态占用=%u字节\n",
                JITTER_FRAMES, JITTER_FRAMES * 20,
                PREBUFFER_FRAMES, PREBUFFER_FRAMES * 20,
                (unsigned)sizeof(jitterBuf));

  // ---------- WebSocket ----------
  webSocket.begin(websocketServer, websocketPort, websocketPath);
  webSocket.onEvent(webSocketEvent);
  webSocket.setReconnectInterval(3000);
  webSocket.enableHeartbeat(15000, 3000, 2);     // 底层 ping/pong 保活

  pinMode(PIN_BUTTON, INPUT_PULLUP);
  lastReading = (digitalRead(PIN_BUTTON) == LOW);
}

void loop() {
  webSocket.loop();

  unsigned long now = millis();

  // ---------- 心跳 ----------
  if (now - lastHeartbeat >= heartbeatInterval) {
    lastHeartbeat = now;
    if (!pressed && webSocket.isConnected()) {
      webSocket.sendTXT("{\"code\":0,\"message\":\"心跳消息\"}");
    }
  }

  // ---------- 按钮去抖 ----------
  bool reading = (digitalRead(PIN_BUTTON) == LOW);
  if (reading != lastReading) {
    lastReading = reading;
    lastDebounceTime = now;
  } else if (reading != pressed && (now - lastDebounceTime) > debounceDelay) {
    pressed = reading;
    if (pressed) {
      onStartTalking();
    } else {
      onStopTalking();
    }
  }

  // ---------- 上行：录音 -> 裸 PCM ----------
  if (pressed) {
    pumpMicrophone();
  }

  // ---------- 下行：抖动缓冲 -> I2S（DMA 背压驱动，非阻塞）----------
  pumpPlayback();

  // ---------- 堆水位诊断 ----------
  if (now - lastHeapReport >= heapReportInterval) {
    lastHeapReport = now;
    Serial.printf("[诊断] freeHeap=%u minHeap=%u 缓冲帧=%d 丢帧=%lu 状态=%s\n",
                  (unsigned)ESP.getFreeHeap(),
                  (unsigned)ESP.getMinFreeHeap(),
                  jitterCount, (unsigned long)dropFrames,
                  playing ? "放音" : "预缓冲");
  }
}

// ================================================================
// 状态切换
// ================================================================
void onStartTalking() {
  Serial.println("[录音] 开始");
  // 立刻停声并冲掉硬件残留，防止上一轮尾音被当成噪音听到
  jitterReset();
  i2s_zero_dma_buffer(I2S_OUT_PORT);
  txBufLen = 0;
  // 丢掉麦克风 DMA 里按下按钮之前的陈旧采样
  i2s_zero_dma_buffer(I2S_IN_PORT);
  if (webSocket.isConnected()) {
    webSocket.sendTXT("{\"code\":1,\"message\":\"开始语音\"}");
  }
}

void onStopTalking() {
  Serial.println("[录音] 结束");
  // 把攒着不足一帧的尾巴补发出去，避免最后一个字被截断
  flushTxBuffer();
  if (webSocket.isConnected()) {
    webSocket.sendTXT("{\"code\":2,\"message\":\"结束语音\"}");
  }
}

// ================================================================
// 上行：读麦克风并发送裸 PCM
// ================================================================
void pumpMicrophone() {
  if (!webSocket.isConnected()) return;

  size_t bytesRead = 0;
  // 超时 0：DMA 里有多少拿多少，绝不阻塞主循环
  if (i2s_read(I2S_IN_PORT, micRaw, MIC_READ_BYTES, &bytesRead, 0) != ESP_OK) return;
  if (bytesRead == 0) return;

  size_t consumed = 0;
  while (consumed < bytesRead) {
    int space = TX_FRAME_BYTES - txBufLen;
    int n = (int)(bytesRead - consumed);
    if (n > space) n = space;
    memcpy(txBuf + txBufLen, micRaw + consumed, n);
    txBufLen += n;
    consumed += n;
    if (txBufLen >= TX_FRAME_BYTES) {
      webSocket.sendBIN(txBuf, TX_FRAME_BYTES);
      txBufLen = 0;
    }
  }
}

void flushTxBuffer() {
  if (txBufLen >= 2 && webSocket.isConnected()) {
    webSocket.sendBIN(txBuf, txBufLen & ~1);     // 保证偶数字节（完整采样）
  }
  txBufLen = 0;
}

// ================================================================
// 下行：抖动缓冲 -> I2S
// 依靠 i2s_write(timeout=0) 的返回值做背压：DMA 塞不下就下一轮再来，
// 这样播放节拍完全由硬件时钟决定，不存在 millis 漂移导致的断续噪声。
// ================================================================
void pumpPlayback() {
  if (pressed) return;                           // 录音期间不放音

  // 预缓冲：攒够再开声，避免一开口就欠载爆音
  if (!playing) {
    if (jitterCount < PREBUFFER_FRAMES) return;
    playing = true;
  }

  // 每轮最多推 4 帧，避免单次循环占用过久饿死 webSocket.loop()
  for (int i = 0; i < 4; i++) {
    if (jitterCount == 0) {
      playing = false;                           // 欠载 -> 退回预缓冲，硬件自动输出静音
      frameOffset = 0;
      return;
    }

    uint8_t* src = (uint8_t*)jitterBuf[jitterRead] + frameOffset;
    int toWrite = FRAME_BYTES - frameOffset;
    size_t written = 0;
    esp_err_t err = i2s_write(I2S_OUT_PORT, src, toWrite, &written, 0);
    if (err != ESP_OK || written == 0) {
      return;                                    // DMA 已满，本轮结束（正常背压）
    }

    if ((int)written < toWrite) {
      // 部分写入：只记录已消费的偏移，下轮从断点续写。
      // 不做数据搬移也不补零，样本流严格连续，杜绝咔哒声。
      frameOffset += (int)written;
      return;
    }

    // 整帧写完，前进到下一帧
    frameOffset = 0;
    jitterRead = (jitterRead + 1) % JITTER_FRAMES;
    jitterCount--;
  }
}

// ================================================================
// 下行 PCM 拼帧：WebSocket 帧长度不保证是 640 的整数倍
// ================================================================
void onPcmReceived(const uint8_t* data, size_t length) {
  size_t consumed = 0;
  while (consumed < length) {
    int space = FRAME_BYTES - rxAssembleLen;
    int n = (int)(length - consumed);
    if (n > space) n = space;
    memcpy(rxAssemble + rxAssembleLen, data + consumed, n);
    rxAssembleLen += n;
    consumed += n;
    if (rxAssembleLen >= FRAME_BYTES) {
      jitterPush((const int16_t*)rxAssemble);
      rxAssembleLen = 0;
    }
  }
}

// ================================================================
// WebSocket 事件
// ================================================================
void webSocketEvent(WStype_t type, uint8_t* payload, size_t length) {
  switch (type) {
    case WStype_DISCONNECTED:
      Serial.println("[WS] 断开");
      jitterReset();
      i2s_zero_dma_buffer(I2S_OUT_PORT);
      txBufLen = 0;
      break;

    case WStype_CONNECTED:
      Serial.println("[WS] 已连接服务器");
      jitterReset();
      dropFrames = 0;
      webSocket.sendTXT("{\"code\":-1,\"message\":\"esp32s3已连接\"}");
      break;

    case WStype_TEXT:
      Serial.printf("[WS] 文本: %.*s\n", (int)length, (const char*)payload);
      break;

    case WStype_BIN:
      if (!pressed && length > 0) {
        onPcmReceived(payload, length);
      }
      break;

    // 大帧被库拆分时的兜底（当前服务端按 640 字节发送，正常不会触发）
    case WStype_FRAGMENT_BIN_START:
    case WStype_FRAGMENT:
    case WStype_FRAGMENT_FIN:
      if (!pressed && length > 0) {
        onPcmReceived(payload, length);
      }
      break;

    case WStype_ERROR:
      Serial.println("[WS] 通信错误");
      break;

    default:
      break;
  }
}
