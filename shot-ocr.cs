using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

// M2: OCR provider (区域截图识别后复制文本)
// 与 shot-service.cs 同属 ShotService 类(partial), 共享 Log/Cfg/BitmapToBase64/ExtractField 等
partial class ShotService
{
    interface IOcrProvider
    {
        Task<string> RecognizeAsync(Bitmap bmp);
    }

    // 默认实现: 本地 qwen3-vl (本机 Ollama :11434 常驻, 零花费零 key, 中文混排/表格最聪明)
    class QwenVlOcrProvider : IOcrProvider
    {
        readonly string endpoint;
        public QwenVlOcrProvider(string ep) { endpoint = ep; }
        public async Task<string> RecognizeAsync(Bitmap bmp)
        {
            string b64 = BitmapToBase64(bmp);
            string prompt = "OCR all text in this image. Output ONLY the recognized text, keep original line breaks and layout. If the text contains Chinese, output Simplified Chinese (简体中文), never Traditional (禁止繁体). If no text is present, output empty.";
            // keep_alive 60m: Ollama 默认 5 分钟卸载模型, 划词间隔一长就重新加载 4B 模型(核显 5s+), 划词慢的大头;
            // num_predict 300: 划词是短文本, 防模型幻觉输出长文拖时间
            string json = "{\"model\":\"qwen3-vl:4b-instruct\",\"prompt\":" + EscapeJson(prompt) + ",\"images\":[\"" + b64 + "\"],\"stream\":false,\"options\":{\"num_predict\":300},\"keep_alive\":\"60m\"}";
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8; // ⚠️ Ollama 返回的 application/json 不带 charset, WebClient 默认按 Latin-1 解码 → 中文全乱码 (实测踩坑)
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                string resp = await wc.UploadStringTaskAsync(endpoint, json);
                return ExtractField(resp, "response").Trim();
            }
        }
    }

    // 远程 OCR 的单个「key + 端点」组合
    // 必须成对: 不同 key 绑不同站点(key1/key2=国际站 apihub, key3=中国站 .cn), 混用会 401
    class OcrKeyEndpoint
    {
        public readonly string Key, Endpoint;
        public OcrKeyEndpoint(string k, string e) { Key = k; Endpoint = e; }
    }

    // WebClient 本身没有超时属性, 派生一个给请求设 Timeout
    // 为什么要超时: 国际站 apihub 处理大图会卡满 60s 才返回空, 不设超时会白等
    class TimeoutWebClient : WebClient
    {
        readonly int timeoutMs;
        public TimeoutWebClient(int ms) { timeoutMs = ms; }
        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest r = base.GetWebRequest(address);
            if (r != null) r.Timeout = timeoutMs;
            return r;
        }
    }

    // 远程 OpenAI 兼容视觉 provider (agnes 官方 / litellm 网关 / 任何 OpenAI 兼容 chat 服务)
    // 用途: 本机没装 Ollama 的机器(如 shlc/gs) 走远程; 也适合客户不想本地跑模型的场景
    //
    // 2026-09-17 增强 (全部由实测驱动, 见 SKILL):
    //   ① 多 key「按优先级 + failover」而非轮流 —— key 与站点绑定且两站差 26 倍:
    //      实测 240x90 小图 .cn=0.8s / apihub key2=6.1s / apihub key1=20.9s;
    //      且 key1 打 .cn 直接 401 ⇒ 乱换必挂。配置里谁在前谁先用, 失败才换下一个。
    //   ② per-key 请求超时: apihub 大图卡满 60s 才返回空, 不设超时白等
    //   ③ 送图压缩: 原实现送 PNG 无损, 全屏图 base64 后极易破 1MB → 被拒/超时
    class OpenAiVisionOcrProvider : IOcrProvider
    {
        readonly string model;
        readonly List<OcrKeyEndpoint> eps;
        // 需换 key 重试的 HTTP 状态: 限流/额度/鉴权临时失败 (与 N5105 同口径)
        static readonly int[] RetryStatus = { 429, 402, 403, 500, 502, 503, 504 };
        const int PerKeyTimeoutMs = 45000;   // 单 key 请求上限(超时即换下一个); 留足量, .cn 大图最坏见过 33s
        const int MaxSide = 1400;            // 送图最长边
        const long MaxBytes = 500 * 1024;

        public OpenAiVisionOcrProvider(string m, List<OcrKeyEndpoint> list) { model = m; eps = list; }

        public async Task<string> RecognizeAsync(Bitmap bmp)
        {
            string b64 = BitmapToBase64Jpeg(bmp, MaxSide, MaxBytes);
            string prompt = "OCR all text in this image. Output ONLY the recognized text, keep original line breaks and layout. If the text contains Chinese, output Simplified Chinese (简体中文), never Traditional (禁止繁体). If no text is present, output empty.";
            string json = "{\"model\":" + EscapeJson(model) +
                ",\"messages\":[{\"role\":\"user\",\"content\":[" +
                "{\"type\":\"text\",\"text\":" + EscapeJson(prompt) + "}," +
                "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64," + b64 + "\"}}" +
                "]}],\"max_tokens\":4096,\"temperature\":0,\"stream\":false}";

            int n = eps.Count;
            Exception last = null;
            for (int i = 0; i < n; i++)             // 顺序 = 优先级: 配置里第 1 个先用
            {
                var ep = eps[i];
                try
                {
                    using (var wc = new TimeoutWebClient(PerKeyTimeoutMs))
                    {
                        wc.Encoding = Encoding.UTF8; // OpenAI 兼容响应可能不带 charset, 不显式设会中文乱码
                        wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                        wc.Headers[HttpRequestHeader.Accept] = "application/json";
                        if (!string.IsNullOrEmpty(ep.Key)) wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + ep.Key;
                        // 注意: HttpWebRequest.Timeout 对*异步*请求无效(MS 文档明确), 必须自己包一层 WhenAny 才真的能超时
                        Task<string> dl = wc.UploadStringTaskAsync(ep.Endpoint, json);
                        Task done = await Task.WhenAny(dl, Task.Delay(PerKeyTimeoutMs));
                        if (done != (Task)dl)
                        {
                            try { wc.CancelAsync(); } catch { }
                            throw new WebException("per-key timeout " + PerKeyTimeoutMs + "ms", WebExceptionStatus.Timeout);
                        }
                        string resp = await dl;
                        string content = OpenAiContent(resp);
                        // 拿不到 content 且响应里有 error -> 把服务端错误原样抛出来, 便于用户看日志定位
                        if (content.Length == 0 && resp.IndexOf("\"error\"", StringComparison.Ordinal) >= 0)
                            throw new Exception("remote ocr error: " + resp.Substring(0, Math.Min(300, resp.Length)));
                        if (content.Length == 0 && i < n - 1)
                        {
                            // 空返回也可能是这条通道不行(实测 apihub 大图 200+空) -> 还能换就换
                            Log("ocr: key#" + (i + 1) + " empty reply, failover next key");
                            continue;
                        }
                        if (i > 0) Log("ocr: key#" + (i + 1) + " ok (failover, 前 " + i + " 个失败)");
                        return content.Trim();
                    }
                }
                catch (WebException wex)
                {
                    last = wex;
                    int code = 0;
                    var hr = wex.Response as HttpWebResponse;
                    if (hr != null) code = (int)hr.StatusCode;
                    // code==0 多为超时/连接失败, 同样可重试
                    bool retryable = (code == 0) || Array.IndexOf(RetryStatus, code) >= 0;
                    if (retryable && i < n - 1)
                    {
                        Log("ocr: key#" + (i + 1) + " -> " + (code == 0 ? wex.Status.ToString() : ("HTTP " + code)) + ", failover next key");
                        continue;
                    }
                    throw;
                }
            }
            if (last != null) throw last;
            return "";   // 所有 key 都返回空
        }
    }

    // OCR provider 工厂: qwen3vl=本机 Ollama(零花费); openai=远程 OpenAI 兼容(agnes 官方等)
    // 多 key 配置: ocr.apiKeys = "key@endpoint|key@endpoint|..." (endpoint 可省, 省略则用 ocr.endpoint)
    static IOcrProvider OcrProvider()
    {
        string p = Cfg("ocr.provider", "qwen3vl");
        if (p == "openai")
        {
            string rep = Cfg("ocr.endpoint", "");
            string rmodel = Cfg("ocr.model", "agnes-3.0-flash");
            string rak = Cfg("ocr.apiKey", "");
            string multi = Cfg("ocr.apiKeys", "");
            var list = new List<OcrKeyEndpoint>();
            if (!string.IsNullOrEmpty(multi))
            {
                foreach (string item in multi.Split('|'))
                {
                    string t = item.Trim();
                    if (t.Length == 0) continue;
                    int at = t.LastIndexOf('@');
                    string k = at >= 0 ? t.Substring(0, at).Trim() : t;
                    string e = at >= 0 ? t.Substring(at + 1).Trim() : rep;
                    if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(e))
                        list.Add(new OcrKeyEndpoint(k, e));
                }
            }
            if (list.Count == 0 && !string.IsNullOrEmpty(rep) && !string.IsNullOrEmpty(rak))
                list.Add(new OcrKeyEndpoint(rak, rep));   // 兼容旧的单 key 配置
            if (list.Count > 0)
            {
                Log("ocr: openai -> " + list.Count + " key(s) rotation");
                return new OpenAiVisionOcrProvider(rmodel, list);
            }
            Log("ocr: openai selected but no usable key -> fallback qwen3vl");
            return new QwenVlOcrProvider("http://127.0.0.1:11434/api/generate");
        }
        return new QwenVlOcrProvider(Cfg("ocr.endpoint", "http://127.0.0.1:11434/api/generate"));
    }

    // ---- 通用工具 (供 OCR/翻译共用, 放在此文件) ----

    static string BitmapToBase64(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
    }

    // 远端 OCR 专用: 等比缩放到 maxSide 以内 + JPEG 编码, 并自动降质直到 <= maxBytes
    // 为什么要压: agnes 收 PNG 大图会长时间无响应甚至拒绝(实测长图必 60s 超时, 小图 3.6s 秒回);
    //            PNG 无损 + base64 膨胀 33% 后体积极易破 1MB
    // maxSide<=0 表示不缩放; maxBytes<=0 表示只按质量 88 编一次不降质
    static string BitmapToBase64Jpeg(Bitmap bmp, int maxSide, long maxBytes)
    {
        Bitmap work = bmp;
        try
        {
            int w = bmp.Width, h = bmp.Height;
            int longSide = Math.Max(w, h);
            if (maxSide > 0 && longSide > maxSide)
            {
                double s = (double)maxSide / longSide;
                int nw = Math.Max(1, (int)Math.Round(w * s));
                int nh = Math.Max(1, (int)Math.Round(h * s));
                var nb = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(nb))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(bmp, 0, 0, nw, nh);
                }
                work = nb;
            }
            // 找 JPEG 编码器(拿不到就退回默认编码)
            ImageCodecInfo jpg = null;
            foreach (var ci in ImageCodecInfo.GetImageEncoders())
                if (ci.FormatID == ImageFormat.Jpeg.Guid) { jpg = ci; break; }

            long q = 88;
            byte[] outBytes = null;
            while (true)
            {
                using (var ms = new MemoryStream())
                {
                    if (jpg != null)
                    {
                        using (var ps = new EncoderParameters(1))
                        {
                            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                            work.Save(ms, jpg, ps);
                        }
                    }
                    else work.Save(ms, ImageFormat.Jpeg);
                    outBytes = ms.ToArray();
                }
                if (maxBytes <= 0 || outBytes.LongLength <= maxBytes || q <= 55) break;
                q -= 11;
            }
            return Convert.ToBase64String(outBytes);
        }
        finally
        {
            if (!ReferenceEquals(work, bmp)) work.Dispose();
        }
    }

    static string EscapeJson(string s)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // 解析 OpenAI 兼容响应 choices[0].message.content (字符串形式), 供 OCR/翻译/划词问AI 共用
    static string OpenAiContent(string resp)
    {
        int mv = FindJsonKey(resp, "message", 0);
        if (mv < 0) return "";
        string msg = JsonValueAt(resp, mv);
        int cv = FindJsonKey(msg, "content", 0);
        if (cv < 0) return "";
        return UnquoteJson(JsonValueAt(msg, cv));
    }

    // 轻量 JSON 字符串字段提取 (避免引第三方 JSON 库; 处理 \" 转义)
    static string ExtractField(string json, string field)
    {
        string key = "\"" + field + "\":";
        int i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return "";
        i += key.Length;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
        if (i >= json.Length) return "";
        if (json[i] == '"')
        {
            // 扫描到闭合引号 (跳过转义序列), 取原始串再整体解码
            int end = i + 1;
            while (end < json.Length)
            {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            return UnescapeJsonString(json.Substring(i + 1, end - i - 1));
        }
        // 非字符串(数字/bool/null)
        int j = i;
        while (j < json.Length && json[j] != ',' && json[j] != '}' && json[j] != '\n') j++;
        return json.Substring(i, j - i).Trim();
    }

    static char UnescapeJson(char c)
    {
        switch (c) { case 'n': return '\n'; case 't': return '\t'; case 'r': return '\r'; case '"': return '"'; case '\\': return '\\'; default: return c; }
    }

    // JSON 字符串解码: \n \t \" \\ 及 4位十六进制转义(中文)。该转义必须整体拦截, 否则变成反斜杠+u 乱码 (实测踩坑)
    static string UnescapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch != '\\') { sb.Append(ch); continue; }
            if (i + 1 >= s.Length) { sb.Append(ch); break; }
            char n = s[i + 1];
            if (n == 'u' && i + 5 < s.Length)
            {
                string hex = s.Substring(i + 2, 4);
                int code;
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code))
                {
                    sb.Append((char)code);
                    i += 5;
                    continue;
                }
            }
            i++;
            sb.Append(UnescapeJson(n));
        }
        return sb.ToString();
    }

    // 在 json 中定位 "key"(闭合引号) 后的第一个冒号, 返回冒号之后的位置; 找不到返回 -1。
    // 比旧版(硬编码 "\"key\":") 健壮: 容忍冒号前空白/换行, 且不会误匹配含 key 字样的更长键名
    static int FindJsonKey(string json, string key, int from)
    {
        string pat = "\"" + key + "\"";
        int i = from;
        while ((i = json.IndexOf(pat, i, StringComparison.Ordinal)) >= 0)
        {
            int j = i + pat.Length;
            while (j < json.Length && (json[j] == ' ' || json[j] == '\t' || json[j] == '\r' || json[j] == '\n')) j++;
            if (j < json.Length && json[j] == ':') return j + 1;
            i += pat.Length;
        }
        return -1;
    }

    // 取 "key": 之后值的完整文本。对象/数组 → 配对花括号的完整子串 (含边界, 供下一层继续解析);
    // 字符串 → 含引号; 标量 → 到逗号/右括号/行尾。
    // ⚠️ 旧版把对象值切到第一个逗号/换行 → 多行格式下嵌套节只切出 "{" → 后续键全部找不到
    //    (P0-3 "设置保存失效/读不回"的真根因; 曾被用户的单行手写 json 掩盖)
    static string JsonValueAt(string json, int valStart)
    {
        int i = valStart;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
        if (i >= json.Length) return "";
        if (json[i] == '{' || json[i] == '[')
        {
            char open = json[i], close = open == '{' ? '}' : ']';
            int depth = 0; bool inStr = false;
            for (int j = i; j < json.Length; j++)
            {
                char c = json[j];
                if (inStr) { if (c == '\\') j++; else if (c == '"') inStr = false; }
                else if (c == '"') inStr = true;
                else if (c == open) depth++;
                else if (c == close) { depth--; if (depth == 0) return json.Substring(i, j - i + 1); }
            }
            return json.Substring(i); // 未闭合, 尽力而为
        }
        if (json[i] == '"')
        {
            for (int j = i + 1; j < json.Length; j++)
            {
                if (json[j] == '\\') { j++; continue; }
                if (json[j] == '"') return json.Substring(i, j - i + 1);
            }
            return json.Substring(i);
        }
        int k = i;
        while (k < json.Length && json[k] != ',' && json[k] != '}' && json[k] != ']' && json[k] != '\n' && json[k] != '\r') k++;
        return json.Substring(i, k - i).Trim();
    }

    static string UnquoteJson(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[s.Length - 1] != '"') return s;
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 1; i < s.Length - 1; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length - 1) sb.Append(UnescapeJson(s[++i]));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // 轻量配置读取: shot-service.json 同目录; 支持 "a.b.c" 点嵌套(逐层取对象子串); 无文件/缺字段返回 def
    static string Cfg(string key, string def)
    {
        try
        {
            string path = ConfigPath();
            if (!File.Exists(path)) return def;
            string cur = File.ReadAllText(path);
            string[] parts = key.Split('.');
            for (int pi = 0; pi < parts.Length; pi++)
            {
                int val = FindJsonKey(cur, parts[pi], 0);
                if (val < 0) return def;
                cur = JsonValueAt(cur, val); // 末层=值全文(字符串含引号); 中间层=对象子串
            }
            return UnquoteJson(cur);
        }
        catch (Exception ex) { Log("cfg err: " + ex.Message); return def; }
    }
}
