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

    // 轻量配置读取: shot-service.json 同目录; 支持 "a.b.c" 点嵌套; 无文件/缺字段返回 def
    static string Cfg(string key, string def)
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot-service.json");
            if (!File.Exists(path)) return def;
            string txt = File.ReadAllText(path);
            string[] parts = key.Split('.');
            string cur = txt;
            foreach (var p in parts)
            {
                string k = "\"" + p + "\":";
                int i = cur.IndexOf(k, StringComparison.Ordinal);
                if (i < 0) return def;
                i += k.Length;
                while (i < cur.Length && (cur[i] == ' ' || cur[i] == '\t')) i++;
                int j = i;
                if (cur[j] == '"')
                {
                    j++;
                    StringBuilder sb = new StringBuilder();
                    while (j < cur.Length)
                    {
                        char c = cur[j++];
                        if (c == '\\') { if (j < cur.Length) sb.Append(UnescapeJson(cur[j++])); }
                        else if (c == '"') break;
                        else sb.Append(c);
                    }
                    cur = sb.ToString();
                }
                else { while (j < cur.Length && cur[j] != ',' && cur[j] != '}' && cur[j] != '\n') j++; cur = cur.Substring(i, j - i).Trim().Trim('"'); }
            }
            return cur;
        }
        catch (Exception ex) { Log("cfg err: " + ex.Message); return def; }
    }
}
