
namespace server
{
    [Serializable]
    public class BaseMsg
    {
        /// <summary>
        /// -1 asr未准备完成 0 实时返回的文字 1 开始发送语音数据 2停止发送语音数据
        /// </summary>
        public int code;
        public string msg;

        public BaseMsg(int code, string msg)
        {
            this.code = code;
            this.msg = msg;
        }
    }
}
