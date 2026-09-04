using System;
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
            string prompt = "OCR all text in this image. Output ONLY the recognized text, keep original line breaks and layout. If no text is present, output empty.";
            string json = "{\"model\":\"qwen3-vl:4b-instruct\",\"prompt\":" + EscapeJson(prompt) + ",\"images\":[\"" + b64 + "\"],\"stream\":false}";
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8; // ⚠️ Ollama 返回的 application/json 不带 charset, WebClient 默认按 Latin-1 解码 → 中文全乱码 (实测踩坑)
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                string resp = await wc.UploadStringTaskAsync(endpoint, json);
                return ExtractField(resp, "response").Trim();
            }
        }
    }

    // OCR provider 工厂: 当前只有 qwen3vl; 接口预留, 以后可加 tesseract/云端
    static IOcrProvider OcrProvider()
    {
        string ep = Cfg("ocr.endpoint", "http://127.0.0.1:11434/api/generate");
        return new QwenVlOcrProvider(ep);
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
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '\\') { if (i < json.Length) sb.Append(UnescapeJson(json[i++])); }
                else if (c == '"') break;
                else sb.Append(c);
            }
            return sb.ToString();
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
