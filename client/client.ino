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

// 全链路统一采样率：24000 Hz（TTS 模型原生采样率）
#define SAMPLE_RATE 24000
#define SAMPLE_BITS 16
#define BUFFER_SIZE 1536

// Opus 配置
#define OPUS_SAMPLE_RATE 24000
#define OPUS_FRAME_SIZE 480  // 20ms @ 24kHz = 480 samples
#define OPUS_MAX_PACKET 1275 // Opus 推荐最大包大小
#define OPUS_BITRATE 24000   // 24kbps - 适合语音

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

// Opus 解码缓冲区
byte opusDecodeBuffer[OPUS_MAX_PACKET + 2]; // +2 for length prefix
int opusDecodeBufferIndex = 0;
int opusDecodePacketLength = 0;
bool opusDecodeReadingLength = true;

// 解码PCM输出缓冲区 - 移到全局避免栈溢出
short pcmOutput[OPUS_FRAME_SIZE * 6];

// 发送缓冲区 - 移到全局避免栈溢出
uint8_t frameBuffer[2 + OPUS_MAX_PACKET];

// 心跳相关
unsigned long previousMillis = 0;
const long interval = 1000;

void setup() {
  Serial.begin(115200);

  // 初始化 Opus 编码器（必须与 TTS 采样率一致 = 24000 Hz）
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

  // 初始化 Opus 解码器（必须与 TTS 采样率一致 = 24000 Hz）
  opusDecoder = opus_decoder_create(OPUS_SAMPLE_RATE, 1, &err);
  if (err != OPUS_OK) {
    Serial.printf("Opus 解码器创建失败: %d\n", err);
  } else {
    Serial.println("Opus 解码器已创建");
  }

  // 自测：编码一帧正弦波再解码，验证客户端编解码 roundtrip
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

  // Initialize I2S for audio input
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

  // Initialize I2S for audio output
  i2s_config_t i2s_config_out = {
    .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_TX),
    .sample_rate = SAMPLE_RATE,
    .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
    .communication_format = (i2s_comm_format_t)(I2S_COMM_FORMAT_STAND_MSB),
    .intr_alloc_flags = ESP_INTR_FLAG_LEVEL1,
    .dma_buf_count = 4,
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

  // 连接WebSocket服务器
  webSocket.begin(websocketServer, websocketPort, websocketPath);
  webSocket.onEvent(webSocketEvent);
  
  // 设置超时时间，避免无限阻塞
  webSocket.setReconnectInterval(5000);
  
  //开关按钮为输入开启上拉电阻
  pinMode(PIN_BUTTON, INPUT_PULLUP);
}

void loop() {
  webSocket.loop();  // 必须调用以处理WebSocket事件
  
  unsigned long currentMillis = millis();
  
  // 心跳发送
  if (currentMillis - previousMillis >= interval) {
    previousMillis = currentMillis;
    // 只在非语音状态下发送心跳
    if (!pressed) {
      webSocket.sendTXT("{\"code\":0,\"message\":\"心跳消息\"}");
    }
  }
  
  // 按钮读取（带去抖）
  bool reading = (digitalRead(PIN_BUTTON) == LOW);
  if (reading != pressed) {
    if (currentMillis - lastDebounceTime > debounceDelay) {
      lastDebounceTime = currentMillis;
      pressed = reading;
      
      if (pressed) {
        // 开始语音
        webSocket.sendTXT("{\"code\":1,\"message\":\"开始语音\"}");
      } else {
        // 结束语音
        webSocket.sendTXT("{\"code\":2,\"message\":\"结束语音\"}");
      }
    }
  }
  
  if (pressed) {
    static uint8_t buffer[BUFFER_SIZE];
    size_t bytesRead;
    
    // 从I2S读取音频数据 - 使用有限超时
    esp_err_t err = i2s_read(I2S_NUM_0, buffer, BUFFER_SIZE, &bytesRead, pdMS_TO_TICKS(10));
    (void)err; // 读取失败时忽略，下一轮重试
    
    // 将 PCM 数据编码为 Opus 并发送
    if (opusEncoder != NULL && bytesRead > 0) {
      encodeAndSendOpus(buffer, bytesRead);
    }
  }
  
  yield(); // 让出 CPU，避免饿死 WiFi/系统任务触发看门狗
}

/// 将 PCM 缓冲区编码为 Opus 包并发送
void encodeAndSendOpus(uint8_t* pcmData, size_t length) {
  size_t bytesProcessed = 0;
  
  while (bytesProcessed < length) {
    // 将字节数据填充到 short 缓冲区
    size_t bytesToCopy = min((size_t)(OPUS_FRAME_SIZE - opusEncodeBufferIndex) * 2, length - bytesProcessed);
    memcpy(((uint8_t*)opusEncodeInput) + opusEncodeBufferIndex * 2, pcmData + bytesProcessed, bytesToCopy);
    opusEncodeBufferIndex += bytesToCopy / 2;
    bytesProcessed += bytesToCopy;
    
    // 当缓冲区满一帧时进行编码
    if (opusEncodeBufferIndex >= OPUS_FRAME_SIZE) {
      int encodedBytes = opus_encode(opusEncoder, opusEncodeInput, OPUS_FRAME_SIZE, opusEncodeOutput, OPUS_MAX_PACKET);
      
      if (encodedBytes > 0) {
        // 合并长度前缀（2字节，小端序）+ Opus 数据
        frameBuffer[0] = (uint8_t)(encodedBytes & 0xFF);
        frameBuffer[1] = (uint8_t)(encodedBytes >> 8 & 0xFF);
        memcpy(frameBuffer + 2, opusEncodeOutput, encodedBytes);
        webSocket.sendBIN(frameBuffer, 2 + encodedBytes);
      }
      
      opusEncodeBufferIndex = 0;
    }
  }
}

/// 从 WebSocket 接收 Opus 数据并解码播放
/// 服务端直接发送原始 Opus 包（不带额外长度前缀），WebSocket 帧本身已含长度信息
void decodeAndPlayOpus(uint8_t* data, size_t length) {
  if (data == NULL || length == 0) return;
  
  if (opusDecoder != NULL) {
    // 诊断：打印前8字节
    if (length > 0) {
      Serial.printf("[解码] len=%d 前8字节:", (int)length);
      for (int i = 0; i < min((int)length, 8); i++) {
        Serial.printf(" %02X", data[i]);
      }
      Serial.println();
    }
    int decodedSamples = opus_decode(opusDecoder, data, length, pcmOutput, OPUS_FRAME_SIZE * 6, 0);
    Serial.printf("[解码] opus_decode 返回 %d 采样\n", decodedSamples);
    
    if (decodedSamples > 0) {
      size_t bytes_written;
      i2s_write(I2S_OUT_PORT, pcmOutput, decodedSamples * 2, &bytes_written, pdMS_TO_TICKS(10));
    }
  }
}

// 以下旧的 length-prefix 解析代码已不再使用，保留注释以便参考
/*
void decodeAndPlayOpus(uint8_t* data, size_t length) {
  size_t bytesProcessed = 0;
  while (bytesProcessed < length) {
    if (opusDecodeReadingLength) {
      size_t bytesToCopy = min((size_t)(2 - opusDecodeBufferIndex), length - bytesProcessed);
      memcpy(opusDecodeBuffer + opusDecodeBufferIndex, data + bytesProcessed, bytesToCopy);
      opusDecodeBufferIndex += bytesToCopy;
      bytesProcessed += bytesToCopy;
      if (opusDecodeBufferIndex >= 2) {
        opusDecodePacketLength = opusDecodeBuffer[0] | (opusDecodeBuffer[1] << 8);
        if (opusDecodePacketLength <= 0 || opusDecodePacketLength > OPUS_MAX_PACKET) {
          opusDecodePacketLength = 0;
          opusDecodeReadingLength = true;
          opusDecodeBufferIndex = 0;
          continue;
        }
        opusDecodeReadingLength = false;
        opusDecodeBufferIndex = 0;
      }
    } else {
      size_t bytesToCopy = min((size_t)(opusDecodePacketLength - opusDecodeBufferIndex), length - bytesProcessed);
      memcpy(opusDecodeBuffer + opusDecodeBufferIndex, data + bytesProcessed, bytesToCopy);
      opusDecodeBufferIndex += bytesToCopy;
      bytesProcessed += bytesToCopy;
      if (opusDecodeBufferIndex >= opusDecodePacketLength) {
        if (opusDecoder != NULL) {
          int decodedSamples = opus_decode(opusDecoder, opusDecodeBuffer, opusDecodePacketLength, pcmOutput, OPUS_FRAME_SIZE, 0);
          if (decodedSamples > 0) {
            size_t bytes_written;
            i2s_write(I2S_OUT_PORT, pcmOutput, decodedSamples * 2, &bytes_written, pdMS_TO_TICKS(10));
          }
        }
        opusDecodeReadingLength = true;
        opusDecodeBufferIndex = 0;
        opusDecodePacketLength = 0;
      }
    }
  }
}
*/

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
      if (!pressed) {
        if (length > 0) {
          if (opusDecoder != NULL) {
            decodeAndPlayOpus(payload, length);
          } else {
            size_t bytes_written;
            i2s_write(I2S_OUT_PORT, payload, length, &bytes_written, pdMS_TO_TICKS(10));
          }
        }
      }
      // 录音状态：忽略服务器发来的音频，不做任何 I2S 操作
      break;

    case WStype_ERROR:
      Serial.println("WebSocket通信错误");
      break;
  }
}
