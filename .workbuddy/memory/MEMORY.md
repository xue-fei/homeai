# homeai 项目长期备忘

## 音频链路约定（全链路唯一一套）
- 16000Hz / 16bit / 单声道 / 小端裸 PCM，无编解码、无长度前缀
- 一帧 = 20ms = 320 samples = 640 字节
- 下行每包聚合 3 帧 = 60ms（规避 Nagle 造成的不规律突发）
- server: `ws://192.168.2.177:9999`（Fleck）；client: ESP32-S3 + WebSocketsClient

## 控制协议（Text JSON `{"code":n,"msg":"..."}`）
| code | 方向 | 含义 |
|---|---|---|
| -1 | 双向 | 连接建立 |
| 0 | C→S | 心跳（1s 一次，超时 10s） |
| 1 | C→S | 开始说话（打断 LLM+TTS，音乐让位不停） |
| 2 | C→S | 结束说话（触发 ASR） |
| 3 | 双向 | 音乐控制 / 状态回执 `State|曲名|音量` |
| 99 | S→C | 错误回执 |

## 下行音频架构（关键约束）
`PcmStreamer` 是**唯一** PCM 出口，TTS 与 MusicPlayer 互斥共享。
绝不允许再开第二个发送线程往同一 WebSocket 推音频——字节交织即噪音。
抢占靠 generation 令牌：`Acquire` 拿代号，`Push(frame, gen)` 代号过期即失败。

优先级：语音 > 音乐。语音 Acquire 抢占 → 音乐转 Ducked → 语音结束
（OnIdle 或看门狗轮询 IsIdle）→ 音乐从被打断处接回。

## 发送节拍（PcmStreamer.SendLoop 最终版，勿再改回）
- 起播水位 `StartWatermarkFrames=1`：有数据立刻发，防爆音预缓冲职责在 ESP32 侧。
- 限速 `SendAheadLimitFrames=32`：发送超前量 = **framesDue - due**（已发帧数 − 实时
  应发帧数），超前超过 32 帧(640ms) 就暂停等实时追上。
- 教训：这个判据曾两次写错——先是「攒够 6 帧才发」卡死供给（欠载暴涨），
  再是「due - framesDue > 32」方向反了（恒假，限速失效，丢帧 400）。
  正确语义：**超前量 = 已做 − 应做**，且发送速率必须严格贴合 50 帧/秒实时。

## 反压原则（勿改成丢帧）
`PcmStreamer.Push` 队列满时**阻塞**生产者，不丢帧。
Matcha steps-3 推理约 20~40 倍实时，丢帧 = 成段扔掉已合成语音 = 听感跳字。
但 MusicPlayer 需额外自限预读 1 秒（`MusicBufferFrames`），因为音乐流无限长。

## ESP32 侧水位
抖动缓冲 64 帧(1280ms)，冷启动预缓冲 10 帧(200ms)，
中途欠载后 `RESUME_FRAMES=3`(60ms) 快速续播（DMA 里仍有存货，高水位干等反而放空）。
I2S TX DMA 12 帧(240ms)，`tx_desc_auto_clear=true`。
串口诊断：`[诊断] 缓冲帧=x/64 丢帧= 欠载=` —— 欠载持续增长说明供给侧不足。

## 音乐播放（MusicPlayer）
- 循环模式三态 `LoopMode`：Sequential(顺序) / LoopAll(列表循环，默认) / LoopOne(单曲循环)。
  `SetLoopMode` / `GetLoopMode` / `CycleLoopMode`；`SetLoopAll(bool)` 是兼容旧接口。
- `OnTrackFinished` 按 loopMode 决定播完一首后的行为：LoopOne 重播当前、LoopAll 切下一首
  （到末尾回 0）、Sequential 播完最后停。
- 语音指令（MusicCommandParser，新增 `Loop` 命令）：
  「单曲循环/循环这一首」→ LoopOne；「列表循环/循环播放/循环」→ LoopAll；
  「顺序播放/不循环/关闭循环」→ Sequential。注意循环类要先于 Play 前缀判断，
  否则「循环播放」会被「播放」抢先匹配。
- 控制台：`loop [one|all|off]`、`cycle`（循环切换）。

## 目录
- `server/music/` — 背景音乐 wav（必须 16000Hz 单声道 16bit，格式不符启动报错）
- 模型目录需拷到 exe 同级：matcha-icefall-zh-baker / sherpa-onnx-conformer-zh-* /
  sherpa-onnx-punct-* / sherpa-onnx-kws-* / gtcrn_simple.onnx / silero_vad.onnx

## 构建注意
完整 `dotnet build` 会因 server.exe 正在运行而 MSB3027 文件锁失败。
只验证代码用 `dotnet build -t:Compile`。
