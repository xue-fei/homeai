using Server.Tts;

namespace Server
{
    /// <summary>
    /// LLM 后端统一接口。目前有两个实现：
    ///   - Llm         : 本地 Ollama（OllamaSharp）
    ///   - LlmLongCat  : LongCat 云端 API（OpenAI 兼容格式）
    ///
    /// 只暴露 Asr/Server 真正用到的方法。历史记录（GetHistory/SetHistory）因类型
    /// 依赖各自 SDK（OllamaSharp.Models.Chat.Message vs 自定义消息），不进接口，
    /// 由各实现自行管理。当前代码路径里这两个方法也没有外部调用者。
    /// </summary>
    public interface ILlm
    {
        /// <summary>TT 语音合成器，由 Server 注入</summary>
        TtsMatchaIcefall tts { get; set; }

        /// <summary>异步发起一次对话请求，识别出的文本边生成边送 TTS</summary>
        void RequestAsync(string prompt);

        /// <summary>打断 LLM 流式生成 + TTS 播放</summary>
        void Interrupt();

        /// <summary>清空对话历史（保留系统提示）</summary>
        void ClearHistory();

        /// <summary>释放资源</summary>
        void Stop();
    }
}
