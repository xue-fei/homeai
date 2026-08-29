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
            Console.WriteLine("音乐调试指令：play [曲名] / pause / resume / stop / next / prev / vol 0.6 / list / scan");

            // 控制台指令线程：不接设备也能测音乐链路，省得每次都拿板子试
            var cmdThread = new Thread(() => ConsoleLoop(server, exitEvent))
            {
                IsBackground = true,
                Name = "Console"
            };
            cmdThread.Start();

            exitEvent.Wait();

            Console.WriteLine("正在停止服务...");
            server.Shutdown();
            Console.WriteLine("已退出");
        }

        static void ConsoleLoop(Server server, ManualResetEventSlim exitEvent)
        {
            while (!exitEvent.IsSet)
            {
                string line;
                try { line = Console.ReadLine(); }
                catch { return; }

                if (line == null) return;                 // 无 stdin（服务化运行）时直接退出该线程
                line = line.Trim();
                if (line.Length == 0) continue;

                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                string cmd = parts[0].ToLowerInvariant();
                string arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                var music = server.Music;

                switch (cmd)
                {
                    case "play":
                        if (!music.Play(arg)) Console.WriteLine("播放失败");
                        break;
                    case "resume":
                        if (!music.Play()) Console.WriteLine("没有可播放内容");
                        break;
                    case "pause":
                        music.Pause();
                        break;
                    case "stop":
                        music.Stop();
                        break;
                    case "next":
                        music.Next();
                        break;
                    case "prev":
                    case "previous":
                        music.Previous();
                        break;
                    case "vol":
                        if (float.TryParse(arg, out float v)) music.SetVolume(v);
                        else Console.WriteLine($"当前音量 {music.GetVolume():0.00}");
                        break;
                    case "loop":
                        music.SetLoopAll(arg != "off" && arg != "0");
                        break;
                    case "scan":
                        music.RefreshPlaylist();
                        break;
                    case "list":
                        var names = music.GetTrackNames();
                        if (names.Count == 0)
                        {
                            Console.WriteLine("music 目录下没有 wav 文件");
                        }
                        else
                        {
                            for (int i = 0; i < names.Count; i++)
                            {
                                Console.WriteLine($"  {i}. {names[i]}");
                            }
                        }
                        break;
                    case "state":
                        Console.WriteLine($"{music.State}  {music.CurrentName}  vol={music.GetVolume():0.00}");
                        break;
                    case "quit":
                    case "exit":
                        exitEvent.Set();
                        return;
                    default:
                        Console.WriteLine("未知指令。可用：play/pause/resume/stop/next/prev/vol/loop/list/scan/state/quit");
                        break;
                }
            }
        }
    }
}
