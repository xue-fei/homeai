
namespace server
{
    [Serializable]
    public class BaseMsg
    {
        /// <summary>
        /// -1 asr未准备完成 0 实时返回的文字 1 语句结束
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
