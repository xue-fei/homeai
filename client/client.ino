#include <WiFi.h>
#include <WebSocketsClient.h>
#include <driver/i2s.h>
#include <opus.h>

// Wi-Fi配置
const char* ssid = "ssid";
const char* password = "password";

// WebSocket服务器配置
const char* websocketServer = "192.168.0.164";
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

// 录音参数配置
#define SAMPLE_RATE 16000
#define SAMPLE_BITS 16
#define BUFFER_SIZE 1024

// Opus 配置
#define OPUS_FRAME_SIZE 320  // 20ms @ 16kHz = 320 samples
#define OPUS_MAX_PACKET 1275 // Opus 推荐最大包大小
#define OPUS_BITRATE 24000   // 24kbps - 适合语音

// WebSocket客户端
WiFiClient wifiClient;
WebSocketsClient webSocket;
// 开关按钮
#define PIN_BUTTON 47
bool pressed = false;

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

void setup() {
  Serial.begin(115200);

  // 初始化 Opus 编码器
  int err;
  opusEncoder = opus_encoder_create(SAMPLE_RATE, 1, OPUS_APPLICATION_VOIP, &err);
  if (err != OPUS_OK) {
    Serial.printf("Opus 编码器创建失败: %d\n", err);
  } else {
    opus_encoder_ctl(opusEncoder, OPUS_SET_BITRATE(OPUS_BITRATE));
    opus_encoder_ctl(opusEncoder, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));
    opus_encoder_ctl(opusEncoder, OPUS_SET_COMPLEXITY(10));
    Serial.println("Opus 编码器已创建");
  }

  // 初始化 Opus 解码器
  opusDecoder = opus_decoder_create(SAMPLE_RATE, 1, &err);
  if (err != OPUS_OK) {
    Serial.printf("Opus 解码器创建失败: %d\n", err);
  } else {
    Serial.println("Opus 解码器已创建");
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
    .dma_buf_count = 16,
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
    .sample_rate = SAMPLE_RATE,  // 改为与输入相同的采样率
    .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_RIGHT,
    .communication_format = (i2s_comm_format_t)(I2S_COMM_FORMAT_STAND_I2S),
    .intr_alloc_flags = ESP_INTR_FLAG_LEVEL1,
    .dma_buf_count = 8,
    .dma_buf_len = 1024,
    .use_apll = false,
    .tx_desc_auto_clear = true,
    .fixed_mclk = 0
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
  //开关按钮为输入开启上拉电阻
  pinMode(PIN_BUTTON, INPUT_PULLUP);
}

unsigned long previousMillis = 0; // 上次发送消息的时间
const long interval = 1000; // 间隔时间（毫秒）

void loop() {
  webSocket.loop();  // 必须调用以处理WebSocket事件
  unsigned long currentMillis = millis(); // 获取当前时间（毫秒）
  if (currentMillis - previousMillis >= interval) {
    if (webSocket.sendTXT("{\"code\":0,\"message\":\"心跳消息\"}")) {
      previousMillis = currentMillis; // 更新上次发送时间
    }
  }
  
  if (digitalRead(PIN_BUTTON) == LOW) {
    if (!pressed) {
      pressed = true;
      i2s_zero_dma_buffer(I2S_OUT_PORT);
      if (webSocket.sendTXT("{\"code\":1,\"message\":\"开始语音\"}")) {
      }
    }
    uint8_t buffer[BUFFER_SIZE];
    size_t bytesRead;
    // 从I2S读取音频数据
    i2s_read(I2S_NUM_0, buffer, BUFFER_SIZE, &bytesRead, portMAX_DELAY);
    
    // 将 PCM 数据编码为 Opus 并发送
    if (opusEncoder != NULL && bytesRead > 0) {
      encodeAndSendOpus(buffer, bytesRead);
    } else if (webSocket.sendBIN(buffer, bytesRead)) {
      // 兼容模式：直接发送原始 PCM（如果 Opus 未初始化）
    }
  } else {
    if (pressed) {
      pressed = false;
      if (webSocket.sendTXT("{\"code\":2,\"message\":\"结束语音\"}")) {
      }
    }
  }
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
        // 合并长度前缀（2字节，小端序）+ Opus 数据，作为单个 WebSocket 帧发送
        uint8_t frameBuffer[2 + OPUS_MAX_PACKET];
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
void decodeAndPlayOpus(uint8_t* data, size_t length) {
  size_t bytesProcessed = 0;
  
  while (bytesProcessed < length) {
    if (opusDecodeReadingLength) {
      // 读取长度前缀（2字节）
      size_t bytesToCopy = min((size_t)(2 - opusDecodeBufferIndex), length - bytesProcessed);
      memcpy(opusDecodeBuffer + opusDecodeBufferIndex, data + bytesProcessed, bytesToCopy);
      opusDecodeBufferIndex += bytesToCopy;
      bytesProcessed += bytesToCopy;
      
      if (opusDecodeBufferIndex >= 2) {
        opusDecodePacketLength = opusDecodeBuffer[0] | (opusDecodeBuffer[1] << 8);
        opusDecodeReadingLength = false;
        opusDecodeBufferIndex = 0;
      }
    } else {
      // 读取 Opus 数据包
      size_t bytesToCopy = min((size_t)(opusDecodePacketLength - opusDecodeBufferIndex), length - bytesProcessed);
      memcpy(opusDecodeBuffer + opusDecodeBufferIndex, data + bytesProcessed, bytesToCopy);
      opusDecodeBufferIndex += bytesToCopy;
      bytesProcessed += bytesToCopy;
      
      if (opusDecodeBufferIndex >= opusDecodePacketLength) {
        // 解码 Opus 数据
        if (opusDecoder != NULL) {
          short pcmOutput[OPUS_FRAME_SIZE * 6];
          int decodedSamples = opus_decode(opusDecoder, opusDecodeBuffer, opusDecodePacketLength, pcmOutput, OPUS_FRAME_SIZE, 0);
          
          if (decodedSamples > 0) {
            size_t bytes_written;
            i2s_write(I2S_OUT_PORT, pcmOutput, decodedSamples * 2, &bytes_written, portMAX_DELAY);
          }
        }
        
        // 重置状态，准备接收下一个包
        opusDecodeReadingLength = true;
        opusDecodeBufferIndex = 0;
        opusDecodePacketLength = 0;
      }
    }
  }
}

void webSocketEvent(WStype_t type, uint8_t* payload, size_t length) {
  switch (type) {
    case WStype_DISCONNECTED:
      Serial.println("WebSocket断开连接");
      break;

    case WStype_CONNECTED:
      Serial.printf("已连接到服务器: %s\n", payload);
      // 连接成功后发送测试消息（可选）
      webSocket.sendTXT("{\"code\":-1,\"message\":\"esp32s3已连接\"}");
      // 打印本地IP地址
      Serial.println(WiFi.localIP());
      break;

    case WStype_TEXT:
      // 处理文本数据
      Serial.printf("收到文本消息: %s\n", payload);
      break;

    case WStype_BIN:
      // 处理二进制数据（Opus 编码音频）
      if (!pressed) {
        if (length > 0) {
          if (opusDecoder != NULL) {
            decodeAndPlayOpus(payload, length);
          } else {
            // 兼容模式：直接播放 PCM
            size_t bytes_written;
            i2s_write(I2S_OUT_PORT, payload, length, &bytes_written, portMAX_DELAY);
          }
        }
      } else {
        if (length > 0) {
          i2s_zero_dma_buffer(I2S_OUT_PORT);
        }
      }
      break;

    case WStype_ERROR:
      Serial.println("WebSocket通信错误");
      break;
  }
}
