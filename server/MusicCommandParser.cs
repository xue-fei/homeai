namespace Server
{
    public enum MusicCommand
    {
        None,
        Play,
        Pause,
        Resume,
        Stop,
        Next,
        Previous,
        VolumeUp,
        VolumeDown,
        List
    }

    public class MusicIntent
    {
        public MusicCommand Command = MusicCommand.None;
        /// <summary>「播放<曲名>」里解析出的曲名，可能为空</summary>
        public string TrackName = string.Empty;
    }

    /// <summary>
    /// 音乐语音指令解析（纯规则匹配，不动用 LLM）
    ///
    /// 放在 ASR 之后、LLM 之前拦截。理由很实际：
    /// 「下一首」这类指令交给 1.5B 模型走一圈既慢又不稳，
    /// 而且模型会自然地开始"聊天"而不是执行动作。规则匹配几微秒搞定。
    ///
    /// ASR 输出已经过标点模型处理，所以匹配前先剥掉标点。
    /// </summary>
    public static class MusicCommandParser
    {
        private static readonly char[] Punct = "，。！？、；：,.!?;:\"'“”‘’ \t".ToCharArray();

        // 停止类要先判，否则「停止播放音乐」会被「播放」抢先匹配
        private static readonly string[] StopWords =
            { "停止播放", "关闭音乐", "关掉音乐", "别放了", "不听了", "停止音乐", "关音乐", "停下音乐" };

        private static readonly string[] PauseWords =
            { "暂停", "先停一下", "停一下" };

        private static readonly string[] ResumeWords =
            { "继续播放", "继续放", "接着放", "继续音乐", "恢复播放", "继续听" };

        private static readonly string[] NextWords =
            { "下一首", "下一曲", "换一首", "换首歌", "切歌", "换歌" };

        private static readonly string[] PrevWords =
            { "上一首", "上一曲", "前一首", "上首歌", "前首歌", "上一首歌", "前一支", "上一支", "后退一首" };


        private static readonly string[] VolumeUpWords =
            { "声音大一点", "大声一点", "音量大一点", "调大音量", "大点声", "声音大点" };

        private static readonly string[] VolumeDownWords =
            { "声音小一点", "小声一点", "音量小一点", "调小音量", "小点声", "声音小点" };

        private static readonly string[] ListWords =
            { "有什么歌", "有哪些歌", "歌单", "有什么音乐", "有哪些音乐", "播放列表" };

        // 「播放XXX」类前缀，按长度降序匹配以便优先吃掉更具体的说法
        private static readonly string[] PlayPrefixes =
            { "播放音乐", "放音乐", "播放歌曲", "来首歌", "来点音乐", "听音乐", "听歌", "播放", "放一首", "放首", "我想听", "想听" };

        public static MusicIntent Parse(string rawText)
        {
            var intent = new MusicIntent();
            if (string.IsNullOrWhiteSpace(rawText)) return intent;

            string t = Normalize(rawText);
            if (t.Length == 0) return intent;

            if (MatchAny(t, StopWords)) { intent.Command = MusicCommand.Stop; return intent; }
            if (MatchAny(t, ListWords)) { intent.Command = MusicCommand.List; return intent; }
            if (MatchAny(t, ResumeWords)) { intent.Command = MusicCommand.Resume; return intent; }
            if (MatchAny(t, NextWords)) { intent.Command = MusicCommand.Next; return intent; }
            if (MatchAny(t, PrevWords)) { intent.Command = MusicCommand.Previous; return intent; }
            if (MatchAny(t, VolumeUpWords)) { intent.Command = MusicCommand.VolumeUp; return intent; }
            if (MatchAny(t, VolumeDownWords)) { intent.Command = MusicCommand.VolumeDown; return intent; }
            if (MatchAny(t, PauseWords)) { intent.Command = MusicCommand.Pause; return intent; }

            foreach (var prefix in PlayPrefixes)
            {
                int idx = t.IndexOf(prefix, StringComparison.Ordinal);
                if (idx < 0) continue;

                // 只接受出现在句首附近的指令，避免「他说了播放什么什么」这类误触
                if (idx > 4) continue;

                string rest = t.Substring(idx + prefix.Length).Trim();
                rest = TrimTrailingNoise(rest);

                intent.Command = MusicCommand.Play;
                // 「播放音乐」后面没跟具体曲名 -> 播当前/第一首
                intent.TrackName = rest;
                return intent;
            }

            return intent;
        }

        private static string Normalize(string s)
        {
            var chars = s.Where(c => Array.IndexOf(Punct, c) < 0).ToArray();
            return new string(chars);
        }

        private static bool MatchAny(string text, string[] words)
        {
            foreach (var w in words)
            {
                if (text.Contains(w, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string TrimTrailingNoise(string s)
        {
            // 「播放青花瓷这首歌」-> 「青花瓷」
            string[] tails = { "这首歌", "这首", "那首歌", "那首", "歌曲", "音乐", "吧", "呢", "啊", "的歌", "歌" };
            bool changed = true;
            while (changed && s.Length > 0)
            {
                changed = false;
                foreach (var tail in tails)
                {
                    if (s.Length > tail.Length && s.EndsWith(tail, StringComparison.Ordinal))
                    {
                        s = s.Substring(0, s.Length - tail.Length);
                        changed = true;
                        break;
                    }
                }
            }
            return s.Trim();
        }
    }
}
