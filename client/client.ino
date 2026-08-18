#include <WiFi.h>
#include <WebSocketsClient.h>
#include <driver/i2s.h>
#include <opus.h>
#include <esp_system.h>

// 增大 loopTask 栈空间（libopus 的 SILK 编码器是重栈操作，官方示例用 32KB 独立任务栈，
// loop 里还叠加 WiFi/lwIP 调用，故给到 48KB 更稳妥）
SET_LOOP_TASK_STACK_SIZE(48 * 1024); // 48KB

// Wi-Fi配置
const char* ssid = "206";
const char* password = "unityioslinux1";

// WebSocket服务器配置
const char* websocketServer = "192.168.2.177";
const int websocketPort = 9999;
const char* websocketPath = "/";

// I2S config for MAX98357A
#define I2S_OUT_PORT I2S_NUM_1
#define I2S_OUT_BCLK 15
#define I2S_OUT_LRC 16
#define I2S_OUT_DOUT 7

// INMP441 config
#define I2S_IN_PORT I2S_NUM_0
#define I2S_IN_BCLK 4
#define I2S_IN_LRC 5
#define I2S_IN_DIN 6

// 全链路统一采样率：16000 Hz
#define SAMPLE_RATE 16000
#define SAMPLE_BITS 16
#define BUFFER_SIZE 1536

// Opus 配置
#define OPUS_SAMPLE_RATE 16000
#define OPUS_FRAME_SIZE 320  // 20ms @ 16kHz = 320 samples
#define OPUS_MAX_PACKET 1275 // Opus 推荐最大包大小
#define OPUS_BITRATE 16000   // 16kbps - 适合语音 @ 16kHz

// WebSocket客户端
WiFiClient wifiClient;
WebSocketsClient webSocket;

// 开关按钮
#define PIN_BUTTON 47
bool pressed = false;
unsigned long lastDebounceTime = 0;
const unsigned long debounceDelay = 50;

// Opus 编码器/解码器
OpusEncoder* opusEncoder = NULL;
OpusDecoder* opusDecoder = NULL;

// Opus 编码缓冲区
short opusEncodeInput[OPUS_FRAME_SIZE];
byte opusEncodeOutput[OPUS_MAX_PACKET];
int opusEncodeBufferIndex = 0;

// 发送缓冲区
uint8_t frameBuffer[2 + OPUS_MAX_PACKET];

// 心跳相关
unsigned long previousMillis = 0;
const long interval = 1000;

// ========== 环形缓冲区（多任务安全）==========
#define PCM_QUEUE_SIZE 32           // 缓存 32 帧（约 640ms），吸收网络与解码抖动
#define PCM_FRAME_BYTES (OPUS_FRAME_SIZE * sizeof(short))

// 每个帧存储 PCM 数据（short 数组）
short pcmBuffer[PCM_QUEUE_SIZE][OPUS_FRAME_SIZE];
int pcmWriteIndex = 0;   // 生产者写入位置
int pcmReadIndex = 0;    // 消费者读取位置
int pcmCount = 0;        // 当前缓冲区中的帧数

// 临界区自旋锁（跨任务保护环形缓冲区：生产者=解码回调，消费者=播放任务）
portMUX_TYPE pcmMux = portMUX_INITIALIZER_UNLOCKED;

// 播放任务句柄与欠载（underrun）统计
static TaskHandle_t playTaskHandle = NULL;
volatile uint32_t underrunCount = 0;

// ========== 环形缓冲区操作函数（非阻塞，单生产者单消费者）==========
bool pushPcmFrame(short* frame) {
  portENTER_CRITICAL(&pcmMux);
  if (pcmCount >= PCM_QUEUE_SIZE) {
    portEXIT_CRITICAL(&pcmMux);
    return false; // 缓冲区满，丢弃
  }
  memcpy(pcmBuffer[pcmWriteIndex], frame, PCM_FRAME_BYTES);
  pcmWriteIndex = (pcmWriteIndex + 1) % PCM_QUEUE_SIZE;
  pcmCount++;
  portEXIT_CRITICAL(&pcmMux);
  return true;
}

bool popPcmFrame(short* outFrame) {
  portENTER_CRITICAL(&pcmMux);
  if (pcmCount == 0) {
    portEXIT_CRITICAL(&pcmMux);
    return false; // 缓冲区空
  }
  memcpy(outFrame, pcmBuffer[pcmReadIndex], PCM_FRAME_BYTES);
  pcmReadIndex = (pcmReadIndex + 1) % PCM_QUEUE_SIZE;
  pcmCount--;
  portEXIT_CRITICAL(&pcmMux);
  return true;
}
// ===============================================

/// 播放任务：独立于 loop 运行，用阻塞式 i2s_write 以 I2S 硬件时钟节奏驱动播放，
/// 从根本上消除软件定时器（millis 轮询）带来的漂移与卡顿。
void playTask(void* arg) {
  short pcmFrame[OPUS_FRAME_SIZE];
  size_t bytes_written = 0;
  while (true) {
    bool hasData = popPcmFrame(pcmFrame);
    if (!hasData) {
      // 欠载：写一帧静音，保持 I2S DMA 不空，避免爆音/跳跃
      underrunCount++;
      memset(pcmFrame, 0, sizeof(pcmFrame));
    }
    // 阻塞写：DMA 满时在此等待，播放速率完全由 I2S 硬件时钟决定
    i2s_write(I2S_OUT_PORT, pcmFrame, sizeof(pcmFrame), &bytes_written, portMAX_DELAY);
  }
}

void setup() {
  Serial.begin(115200);

  // 初始化 Opus 编码器
  int err;
  opusEncoder = opus_encoder_create(OPUS_SAMPLE_RATE, 1, OPUS_APPLICATION_VOIP, &err);
  if (err != OPUS_OK) {
    Serial.printf("Opus 编码器创建失败: %d\n", err);
  } else {
    opus_encoder_ctl(opusEncoder, OPUS_SET_BITRATE(OPUS_BITRATE));
    opus_encoder_ctl(opusEncoder, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));
    opus_encoder_ctl(opusEncoder, OPUS_SET_COMPLEXITY(0));
    Serial.printf("Opus 编码器已创建 (采样率: %d Hz)\n", OPUS_SAMPLE_RATE);
  }

  // 初始化 Opus 解码器
  opusDecoder = opus_decoder_create(OPUS_SAMPLE_RATE, 1, &err);
  if (err != OPUS_OK) {
    Serial.printf("Opus 解码器创建失败: %d\n", err);
  } else {
    Serial.println("Opus 解码器已创建");
  }

  // 自测
  if (opusEncoder != NULL && opusDecoder != NULL) {
    short testPcm[OPUS_FRAME_SIZE];
    for (int i = 0; i < OPUS_FRAME_SIZE; i++)
      testPcm[i] = (short)(sin(2.0 * PI * 440.0 * i / SAMPLE_RATE) * 12000.0);
    int testEnc = opus_encode(opusEncoder, testPcm, OPUS_FRAME_SIZE, opusEncodeOutput, OPUS_MAX_PACKET);
    Serial.printf("[自测] 编码 %d 字节, TOC=0x%02X\n", testEnc, testEnc > 0 ? opusEncodeOutput[0] : 0);
    if (testEnc > 0) {
      short testOut[OPUS_FRAME_SIZE];
      int testDec = opus_decode(opusDecoder, opusEncodeOutput, testEnc, testOut, OPUS_FRAME_SIZE, 0);
      Serial.printf("[自测] 解码 %d 采样 %s\n", testDec, testDec > 0 ? "OK" : "失败");
    }
  }

  // 连接Wi-Fi
  WiFi.begin(ssid, password);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println("WiFi connected");

  // I2S input
  i2s_config_t i2s_config_in = {
    .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_RX),
    .sample_rate = SAMPLE_RATE,
    .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
    .communication_format = i2s_comm_format_t(I2S_COMM_FORMAT_STAND_I2S),
    .intr_alloc_flags = ESP_INTR_FLAG_LEVEL1,
    .dma_buf_count = 4,
    .dma_buf_len = 1024,
  };
  i2s_pin_config_t pin_config_in = {
    .bck_io_num = I2S_IN_BCLK,
    .ws_io_num = I2S_IN_LRC,
    .data_out_num = -1,
    .data_in_num = I2S_IN_DIN
  };

  // I2S output
  i2s_config_t i2s_config_out = {
    .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_TX),
    .sample_rate = SAMPLE_RATE,
    .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_RIGHT,
    .communication_format = (i2s_comm_format_t)(I2S_COMM_FORMAT_STAND_I2S),
    .intr_alloc_flags = ESP_INTR_FLAG_LEVEL1,
    .dma_buf_count = 8,               // 加大 DMA 缓冲（8×1024B ≈ 256ms），吸收抖动
    .dma_buf_len = 1024,
    .use_apll = true,
    .tx_desc_auto_clear = true,
    .fixed_mclk = 256 * SAMPLE_RATE
  };
  i2s_pin_config_t pin_config_out = {
    .bck_io_num = I2S_OUT_BCLK,
    .ws_io_num = I2S_OUT_LRC,
    .data_out_num = I2S_OUT_DOUT,
    .data_in_num = -1
  };

  i2s_driver_install(I2S_IN_PORT, &i2s_config_in, 0, NULL);
  i2s_set_pin(I2S_IN_PORT, &pin_config_in);
  i2s_driver_install(I2S_OUT_PORT, &i2s_config_out, 0, NULL);
  i2s_set_pin(I2S_OUT_PORT, &pin_config_out);

  // 创建播放任务（固定在 core 0，与 loop 所在 core 1 分离，避免互相阻塞）
  xTaskCreatePinnedToCore(playTask, "i2s_play", 8192, NULL, 5, &playTaskHandle, 0);

  // 连接WebSocket
  webSocket.begin(websocketServer, websocketPort, websocketPath);
  webSocket.onEvent(webSocketEvent);
  webSocket.setReconnectInterval(5000);

  pinMode(PIN_BUTTON, INPUT_PULLUP);
}

void loop() {
  webSocket.loop();  // WebSocket 事件处理

  unsigned long currentMillis = millis();

  // 心跳
  if (currentMillis - previousMillis >= interval) {
    previousMillis = currentMillis;
    if (!pressed) {
      webSocket.sendTXT("{\"code\":0,\"message\":\"心跳消息\"}");
    }
  }

  // 按钮去抖
  bool reading = (digitalRead(PIN_BUTTON) == LOW);
  if (reading != pressed) {
    if (currentMillis - lastDebounceTime > debounceDelay) {
      lastDebounceTime = currentMillis;
      pressed = reading;
      if (pressed) {
        webSocket.sendTXT("{\"code\":1,\"message\":\"开始语音\"}");
      } else {
        webSocket.sendTXT("{\"code\":2,\"message\":\"结束语音\"}");
      }
    }
  }

  // 录音编码发送
  if (pressed) {
    static uint8_t buffer[BUFFER_SIZE];
    size_t bytesRead;
    esp_err_t err = i2s_read(I2S_NUM_0, buffer, BUFFER_SIZE, &bytesRead, pdMS_TO_TICKS(10));
    (void)err;
    if (opusEncoder != NULL && bytesRead > 0) {
      encodeAndSendOpus(buffer, bytesRead);
    }
  }

  // 调试统计：每 5 秒打印一次队列水位与欠载次数（验证修复后可删除）
  static unsigned long lastStatTime = 0;
  if (currentMillis - lastStatTime >= 5000) {
    lastStatTime = currentMillis;
    Serial.printf("[播放] 队列水位=%d/%d 欠载=%u\n", pcmCount, PCM_QUEUE_SIZE, underrunCount);
  }

  yield(); // 避免看门狗
}

/// 编码并发送 Opus
void encodeAndSendOpus(uint8_t* pcmData, size_t length) {
  size_t bytesProcessed = 0;
  while (bytesProcessed < length) {
    size_t bytesToCopy = min((size_t)(OPUS_FRAME_SIZE - opusEncodeBufferIndex) * 2, length - bytesProcessed);
    memcpy(((uint8_t*)opusEncodeInput) + opusEncodeBufferIndex * 2, pcmData + bytesProcessed, bytesToCopy);
    opusEncodeBufferIndex += bytesToCopy / 2;
    bytesProcessed += bytesToCopy;
    if (opusEncodeBufferIndex >= OPUS_FRAME_SIZE) {
      int encodedBytes = opus_encode(opusEncoder, opusEncodeInput, OPUS_FRAME_SIZE, opusEncodeOutput, OPUS_MAX_PACKET);
      if (encodedBytes > 0) {
        frameBuffer[0] = (uint8_t)(encodedBytes & 0xFF);
        frameBuffer[1] = (uint8_t)(encodedBytes >> 8 & 0xFF);
        memcpy(frameBuffer + 2, opusEncodeOutput, encodedBytes);
        webSocket.sendBIN(frameBuffer, 2 + encodedBytes);
      }
      opusEncodeBufferIndex = 0;
    }
  }
}

/// 解码并压入环形缓冲区（不再直接写 I2S）
void decodeAndPlayOpus(uint8_t* data, size_t length) {
  if (data == NULL || length == 0 || opusDecoder == NULL) return;

  short pcmFrame[OPUS_FRAME_SIZE];
  int decodedSamples = opus_decode(opusDecoder, data, length, pcmFrame, OPUS_FRAME_SIZE, 0);
  if (decodedSamples > 0) {
    if (!pushPcmFrame(pcmFrame)) {
      // 缓冲区满，丢弃（可打印统计）
      // Serial.println("[警告] PCM 队列满，丢弃一帧");
    }
  }
}

// WebSocket 事件回调
void webSocketEvent(WStype_t type, uint8_t* payload, size_t length) {
  switch (type) {
    case WStype_DISCONNECTED:
      Serial.println("WebSocket断开连接");
      break;
    case WStype_CONNECTED:
      Serial.printf("已连接到服务器: %s\n", payload);
      webSocket.sendTXT("{\"code\":-1,\"message\":\"esp32s3已连接\"}");
      Serial.println(WiFi.localIP());
      break;
    case WStype_TEXT:
      Serial.printf("收到文本消息: %s\n", payload);
      break;
    case WStype_BIN:
      if (!pressed) {  // 非录音状态才播放
        if (length > 0 && opusDecoder != NULL) {
          decodeAndPlayOpus(payload, length);
        }
      }
      break;
    case WStype_ERROR:
      Serial.println("WebSocket通信错误");
      break;
  }
}