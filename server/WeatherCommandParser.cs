namespace Server
{
    /// <summary>
    /// 天气查询意图
    /// </summary>
    public class WeatherIntent
    {
        public bool IsWeatherQuery { get; set; }
        /// <summary>提取出的城市名，为空则用默认位置</summary>
        public string City { get; set; } = string.Empty;
        /// <summary>true = 问明天/未来几天，false = 问今天</summary>
        public bool IsForecast { get; set; }
        /// <summary>问未来第几天（1=明天，2=后天）</summary>
        public int ForecastDay { get; set; } = 1;
    }

    /// <summary>
    /// 天气语音指令解析（纯规则匹配，不动用 LLM）
    ///
    /// 放在 ASR 之后、LLM 之前拦截。理由和音乐指令一样：
    /// 「今天天气怎么样」绕一圈 1.5B 模型既慢又不稳，直接规则匹配 + API 调用几毫秒搞定。
    /// </summary>
    public static class WeatherCommandParser
    {
        // 触发词：必须包含其中之一才算天气查询
        // 注意：「天气预报员」这种不算，所以后面要排除
        private static readonly string[] WeatherTriggers =
            { "天气", "气温", "温度", "下雨", "刮风", "下雪", "几度", "多少度", "穿什么", "带伞" };

        // 时态词（按长度降序，优先匹配长的）
        private static readonly string[] FutureWords =
            { "大后天", "后天", "明天", "未来", "接下来", "这周", "周末" };

        // 时间词黑名单：这些词出现在「天气」前面时，不能当城市名提取
        private static readonly string[] TimeBlacklist =
            { "今天", "明天", "后天", "大后天", "昨天", "前天", "现在", "当前", "目前",
              "早上", "上午", "中午", "下午", "晚上", "夜里", "凌晨",
              "未来", "接下来", "这周", "周末", "下周", "下个月", "最近",
              "几天", "这几天", "这些天" };

        // 常见城市列表（用于从句子中提取城市名）。
        // 心知天气 API 支持中文城市名直接查询，所以这里列中文。
        // 不在列表里的城市也能查——直接扔给 API，API 会返回错误，我们再降级到默认城市。
        private static readonly string[] KnownCities =
        {
            "北京", "上海", "广州", "深圳", "杭州", "南京", "成都", "重庆", "武汉",
            "西安", "苏州", "天津", "长沙", "郑州", "青岛", "大连", "厦门", "福州",
            "昆明", "丽江", "大理", "桂林", "三亚", "拉萨", "乌鲁木齐", "哈尔滨",
            "沈阳", "济南", "合肥", "南昌", "贵阳", "南宁", "兰州", "太原",
            "石家庄", "无锡", "宁波", "佛山", "东莞", "珠海", "汕头", "海口",
            "三亚", "秦皇岛", "九寨沟", "张家界", "凤凰", "阳朔", "乌镇", "西塘",
            "平遥", "敦煌", "洛阳", "开封", "扬州", "绍兴", "景德镇", "婺源",
            "上海", "香港", "澳门", "台北", "东京", "首尔", "新加坡", "曼谷",
            "伦敦", "巴黎", "纽约", "悉尼", "莫斯科", "柏林", "罗马", "马德里"
        };

        public static WeatherIntent Parse(string text)
        {
            var intent = new WeatherIntent();
            if (string.IsNullOrWhiteSpace(text)) return intent;

            // 0. 排除明显不是天气查询的句子（「天气预报员说...」）
            if (text.Contains("天气预报员") || text.Contains("天气预报说") || text.Contains("预报员"))
                return intent;

            // 1. 判断是否为天气查询
            bool isWeather = false;
            foreach (var w in WeatherTriggers)
            {
                if (text.Contains(w, StringComparison.Ordinal))
                {
                    isWeather = true;
                    break;
                }
            }
            if (!isWeather) return intent;

            intent.IsWeatherQuery = true;

            // 2. 判断时态（按长度降序匹配，避免「后天」先于「大后天」）
            foreach (var w in FutureWords)
            {
                if (text.Contains(w, StringComparison.Ordinal))
                {
                    intent.IsForecast = true;
                    if (text.Contains("大后天")) intent.ForecastDay = 3;
                    else if (text.Contains("后天")) intent.ForecastDay = 2;
                    else intent.ForecastDay = 1;
                    break;
                }
            }

            // 3. 提取城市名
            string city = ExtractCity(text);
            if (!string.IsNullOrEmpty(city))
            {
                intent.City = city;
            }

            return intent;
        }

        private static string ExtractCity(string text)
        {
            // 策略 1：匹配已知城市列表（优先长匹配，避免「北京」匹配到「京」）
            string best = string.Empty;
            foreach (var city in KnownCities)
            {
                if (text.Contains(city, StringComparison.Ordinal) && city.Length > best.Length)
                {
                    best = city;
                }
            }
            if (!string.IsNullOrEmpty(best)) return best;

            // 策略 2：正则提取「XX天气」「XX的气温」等模式
            int idx = text.IndexOf("天气", StringComparison.Ordinal);
            if (idx > 0)
            {
                // 取「天气」前面 1-4 个字作为候选城市
                int start = Math.Max(0, idx - 4);
                string candidate = text.Substring(start, idx - start);
                // 去掉句首的「的」「在」「去」「到」等介词
                candidate = candidate.TrimStart('的', '在', '去', '到', '从', '和', '跟', '与');
                // 去掉时间词（今天、明天等不能当城市名）
                foreach (var tw in TimeBlacklist)
                {
                    candidate = candidate.Replace(tw, "");
                }
                candidate = candidate.Trim();
                if (candidate.Length >= 2) return candidate;
            }

            return string.Empty;
        }
    }
}
