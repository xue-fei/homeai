namespace Server
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Server server = new Server();

            // Ctrl+C / 进程退出时优雅停机，确保线程、Timer、ONNX 会话都被释放
            var exitEvent = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exitEvent.Set();
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => exitEvent.Set();

            Console.WriteLine("服务已启动，按 Ctrl+C 退出");
            exitEvent.Wait();

            Console.WriteLine("正在停止服务...");
            server.Shutdown();
            Console.WriteLine("已退出");
        }
    }
}
