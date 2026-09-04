using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

// M2: 翻译 provider (一键翻译识别文本)
// 与 shot-service.cs 同属 ShotService 类(partial), 共享 Log/Cfg/ExtractField/EscapeJson
partial class ShotService
{
    interface ITranslateProvider
    {
        Task<string> TranslateAsync(string text, string to);
    }

    // 默认零 key: 本地 LLM (本机 Ollama qwen3-vl 当翻译用, 零花费离线)
    class LocalLlmTranslateProvider : ITranslateProvider
    {
        readonly string ep, model, apiKey;
        public LocalLlmTranslateProvider(string e, string m, string k) { ep = e; model = m; apiKey = k; }
        public async Task<string> TranslateAsync(string text, string to)
        {
            string lang = (to == "en") ? "English" : "Chinese";
            string prompt = "Translate the following text into " + lang + ". Output ONLY the translation, no explanation, no quotes.\n\n" + text;
            string json = "{\"model\":" + EscapeJson(model) + ",\"prompt\":" + EscapeJson(prompt) + ",\"stream\":false}";
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8; // 同上: Ollama 响应无 charset, 默认 Latin-1 解码中文乱码
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                if (!string.IsNullOrEmpty(apiKey)) wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
                string resp = await wc.UploadStringTaskAsync(ep, json);
                return ExtractField(resp, "response").Trim();
            }
        }
    }

    // 百度通用翻译 (需 appid/key, 从 shot-service.json 读; 未配置则跳过, 回退 local)
    class BaiduTranslateProvider : ITranslateProvider
    {
        readonly string appId, key;
        public BaiduTranslateProvider(string id, string k) { appId = id; key = k; }
        public async Task<string> TranslateAsync(string text, string to)
        {
            string salt = DateTime.Now.Ticks.ToString();
            string sign = Md5(appId + text + salt + key);
            string q = Uri.EscapeDataString(text);
            string url = "https://fanyi-api.baidu.com/api/trans/vip/translate?q=" + q +
                         "&from=auto&to=" + to + "&appid=" + appId + "&salt=" + salt + "&sign=" + sign;
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                string resp = await wc.DownloadStringTaskAsync(url);
                // 百度返回 {"trans_result":[{"src":"...","dst":"..."}]}
                return ExtractField(resp, "dst").Trim();
            }
        }
    }

    // 翻译 provider 工厂: 配了百度 appid/key 用百度, 否则本地 LLM (零 key)
    static ITranslateProvider TranslateProvider()
    {
        string p = Cfg("translate.provider", "local");
        if (p == "baidu")
        {
            string id = Cfg("translate.baiduAppId", "");
            string key = Cfg("translate.baiduKey", "");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key))
                return new BaiduTranslateProvider(id, key);
            Log("translate: baidu selected but appid/key missing -> fallback local LLM");
        }
        string ep = Cfg("translate.endpoint", "http://127.0.0.1:11434/api/generate");
        string model = Cfg("translate.model", "qwen3-vl:4b-instruct");
        string ak = Cfg("translate.apiKey", "");
        return new LocalLlmTranslateProvider(ep, model, ak);
    }

    static string Md5(string s)
    {
        using (var md5 = MD5.Create())
        {
            byte[] b = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
