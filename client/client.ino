#include <WiFi.h>
#include <WebSocketsClient.h>
#include <driver/i2s.h>

// Wi-Fi配置
const char* ssid = "ssid";
const char* password = "password";

// WebSocket服务器配置
const char* websocketServer = "172.32.151.240";
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

// WebSocket客户端
WiFiClient wifiClient;
WebSocketsClient webSocket;
// 开关按钮
#define PIN_BUTTON 47  
bool pressed = false;

void setup() {
  Serial.begin(115200);

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
    .bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT,  // 注意：INMP441 输出 32 位数据
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
    .sample_rate = 44100,
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

void loop() {
  webSocket.loop();  // 必须调用以处理WebSocket事件
  if (digitalRead(PIN_BUTTON) == LOW) {
    pressed = true;
    uint8_t buffer[BUFFER_SIZE];
    size_t bytesRead;
    // 从I2S读取音频数据
    i2s_read(I2S_NUM_0, buffer, BUFFER_SIZE, &bytesRead, portMAX_DELAY);
    // 通过WebSocket发送音频数据
    if (webSocket.sendBIN(buffer, bytesRead)) {
      //Serial.printf("Sent %d bytes of audio data\n", bytesRead);
    } else {
      //Serial.println("Failed to send audio data");
    }
  } else {
    if (pressed) {
      pressed = false;
      if (webSocket.sendTXT("{\"code\":1,\"message\":\"结束语音\"}")) {
      } else {
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
      // 处理二进制数据
      //Serial.printf("收到二进制数据，长度: %d\n", length);
      size_t bytes_written;
      i2s_write(I2S_OUT_PORT, payload, length, &bytes_written, portMAX_DELAY);
      //delay(1);
      //i2s_zero_dma_buffer(I2S_OUT_PORT);
      break;

    case WStype_ERROR:
      Serial.println("WebSocket通信错误");
      break;
  }
}