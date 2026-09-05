namespace Server
{
    /// <summary>
    /// 心知天气 API 客户端
    ///
    /// API 文档：https://docs.seniverse.com/api/weather/now.html
    /// 免费版限制：天气现象文字、代码、气温 3 项数据，每秒 1 次请求
    /// </summary>
    public class WeatherService
    {
        private readonly string apiKey;
        private readonly string baseUrl;
        private readonly HttpClient http;
        private readonly string defaultCity;

        public WeatherService(string apiKey, string defaultCity = "北京")
        {
            this.apiKey = apiKey;
            this.defaultCity = defaultCity;
            baseUrl = "https://api.seniverse.com/v3";
            http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// 查询实况天气
        /// </summary>
        public async Task<WeatherResult> GetCurrentAsync(string city = null)
        {
            city = string.IsNullOrWhiteSpace(city) ? defaultCity : city.Trim();
            string url = $"{baseUrl}/weather/now.json?key={apiKey}&location={Uri.EscapeDataString(city)}&language=zh-Hans&unit=c";

            try
            {
                var resp = await http.GetStringAsync(url);
                return ParseCurrent(resp, city);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[天气] 查询失败: {e.Message}");
                return WeatherResult.Failed(city);
            }
        }

        /// <summary>
        /// 查询未来几天预报
        /// </summary>
        public async Task<WeatherResult> GetForecastAsync(string city = null, int days = 3)
        {
            city = string.IsNullOrWhiteSpace(city) ? defaultCity : city.Trim();
            days = Math.Clamp(days, 1, 3);
            string url = $"{baseUrl}/weather/daily.json?key={apiKey}&location={Uri.EscapeDataString(city)}&language=zh-Hans&unit=c&start=0&days={days}";

            try
            {
                var resp = await http.GetStringAsync(url);
                return ParseDaily(resp, city, days);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[天气] 预报查询失败: {e.Message}");
                return WeatherResult.Failed(city);
            }
        }

        private WeatherResult ParseCurrent(string json, string city)
        {
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var results = obj["results"] as Newtonsoft.Json.Linq.JArray;
                if (results == null || results.Count == 0)
                    return WeatherResult.Failed(city);

                var r = results[0];
                var now = r["now"];
                if (now == null) return WeatherResult.Failed(city);

                return new WeatherResult
                {
                    Success = true,
                    City = r["location"]?["name"]?.ToString() ?? city,
                    Text = now["text"]?.ToString() ?? "未知",
                    Temperature = now["temperature"]?.ToString() ?? "--",
                    FeelsLike = now["feels_like"]?.ToString(),
                    Humidity = now["humidity"]?.ToString(),
                    WindDirection = now["wind_direction"]?.ToString(),
                    WindScale = now["wind_scale"]?.ToString(),
                    IsForecast = false
                };
            }
            catch (Exception e)
            {
                Console.WriteLine($"[天气] 解析失败: {e.Message}");
                return WeatherResult.Failed(city);
            }
        }

        private WeatherResult ParseDaily(string json, string city, int days)
        {
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var results = obj["results"] as Newtonsoft.Json.Linq.JArray;
                if (results == null || results.Count == 0)
                    return WeatherResult.Failed(city);

                var r = results[0];
                var daily = r["daily"] as Newtonsoft.Json.Linq.JArray;
                if (daily == null || daily.Count == 0)
                    return WeatherResult.Failed(city);

                var today = daily[0];
                var result = new WeatherResult
                {
                    Success = true,
                    City = r["location"]?["name"]?.ToString() ?? city,
                    Text = $"{today["text_day"]?.ToString() ?? "未知"}，夜间{today["text_night"]?.ToString() ?? "未知"}",
                    Temperature = $"{today["low"]?.ToString() ?? "--"}~{today["high"]?.ToString() ?? "--"}",
                    IsForecast = true,
                    Date = today["date"]?.ToString()
                };

                // 如果查多天，拼成一段话
                if (daily.Count > 1)
                {
                    var parts = new List<string> { result.Text };
                    for (int i = 1; i < Math.Min(daily.Count, days); i++)
                    {
                        var d = daily[i];
                        string date = d["date"]?.ToString() ?? $"第{i + 1}天";
                        string dayText = $"{date} {d["text_day"]?.ToString() ?? "未知"}，{d["low"]?.ToString() ?? "--"}~{d["high"]?.ToString() ?? "--"}度";
                        parts.Add(dayText);
                    }
                    result.Text = string.Join("，", parts);
                }

                return result;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[天气] 预报解析失败: {e.Message}");
                return WeatherResult.Failed(city);
            }
        }
    }

    public class WeatherResult
    {
        public bool Success { get; set; }
        public string City { get; set; }
        public string Text { get; set; }
        public string Temperature { get; set; }
        public string FeelsLike { get; set; }
        public string Humidity { get; set; }
        public string WindDirection { get; set; }
        public string WindScale { get; set; }
        public bool IsForecast { get; set; }
        public string Date { get; set; }

        public static WeatherResult Failed(string city) => new WeatherResult
        {
            Success = false,
            City = city,
            Text = "查询失败",
            Temperature = "--"
        };

        /// <summary>
        /// 生成适合 TTS 朗读的自然语言描述
        /// </summary>
        public string ToSpeech()
        {
            if (!Success) return $"抱歉，{City}的天气没查到。";

            if (IsForecast)
                return $"{City}天气：{Text}。";

            string speech = $"{City}现在{Text}，{Temperature}度";
            if (!string.IsNullOrEmpty(FeelsLike) && FeelsLike != Temperature)
                speech += $"，体感{FeelsLike}度";
            if (!string.IsNullOrEmpty(Humidity))
                speech += $"，湿度{Humidity}%";
            if (!string.IsNullOrEmpty(WindDirection) && !string.IsNullOrEmpty(WindScale))
                speech += $"，{WindDirection}风{WindScale}级";
            speech += "。";
            return speech;
        }
    }
}
