using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// M3: 设置/登录入口 (让用户自助填 OCR/翻译配置, 存 shot-service.json 热生效)
// 与 shot-service.cs 同属 ShotService 类(partial), 共享 Cfg/Log/TrayIcon
partial class ShotService
{
    // 配置文件路径: 读写统一走这里, 设置窗显示给用户 — 杜绝"写进哪个文件都不知道"
    static string ConfigPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot-service.json");
    }

    // 已知配置键 (扁平 a.b.c); 保存时整体写回双层 json, 合并不丢其它字段
    static Dictionary<string, string> LoadCfgDict()
    {
        var d = new Dictionary<string, string>();
        d["ocr.provider"] = Cfg("ocr.provider", "qwen3vl");
        d["ocr.endpoint"] = Cfg("ocr.endpoint", "http://127.0.0.1:11434/api/generate");
        d["translate.provider"] = Cfg("translate.provider", "local");
        d["translate.endpoint"] = Cfg("translate.endpoint", "http://127.0.0.1:11434/api/generate");
        d["translate.model"] = Cfg("translate.model", "qwen3-vl:4b-instruct");
        d["translate.apiKey"] = Cfg("translate.apiKey", "");
        d["translate.to"] = Cfg("translate.to", "zh");
        d["translate.baiduAppId"] = Cfg("translate.baiduAppId", "");
        d["translate.baiduKey"] = Cfg("translate.baiduKey", "");
        return d;
    }

    // 写回 shot-service.json (双层结构; Cfg() 每次读文件, 保存即热生效)
    // 注意: 百度翻译标准 API 只需 appid+key 两个参数, 无第三个 apiKey 字段
    static void SaveCfgDict(Dictionary<string, string> d)
    {
        string path = ConfigPath();
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"ocr\": { \"provider\": " + J(d["ocr.provider"]) + ", \"endpoint\": " + J(d["ocr.endpoint"]) + " },\n");
        sb.Append("  \"translate\": {\n");
        sb.Append("    \"provider\": " + J(d["translate.provider"]) + ",\n");
        sb.Append("    \"endpoint\": " + J(d["translate.endpoint"]) + ",\n");
        sb.Append("    \"model\": " + J(d["translate.model"]) + ",\n");
        sb.Append("    \"apiKey\": " + J(d["translate.apiKey"]) + ",\n");
        sb.Append("    \"to\": " + J(d["translate.to"]) + ",\n");
        sb.Append("    \"baiduAppId\": " + J(d["translate.baiduAppId"]) + ",\n");
        sb.Append("    \"baiduKey\": " + J(d["translate.baiduKey"]) + "\n");
        sb.Append("  }\n");
        sb.Append("}\n");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8); // 失败会抛, 由调用方弹错+记日志
        Log("config saved: " + path + " (provider=" + d["translate.provider"] + ")");
    }

    // 最小 json 字符串转义 (值简单, 不引第三方序列化库)
    static string J(string s)
    {
        if (s == null) s = "";
        var b = new StringBuilder();
        b.Append('"');
        foreach (char c in s)
        {
            if (c == '"') b.Append("\\\"");
            else if (c == '\\') b.Append("\\\\");
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') b.Append("\\r");
            else if (c == '\t') b.Append("\\t");
            else b.Append(c);
        }
        b.Append('"');
        return b.ToString();
    }

    // 设置/配置窗 (托盘菜单"设置..."触发; UI 线程 STA, ShowDialog 安全)
    // 翻译引擎下拉动态切换: local -> 本地 LLM 配置; baidu -> 百度 appid/密钥
    // 含: 测试按钮(按当前面板值实测连通性) / 密钥掩码+显示切换 / 配置文件路径可见+一键打开
    static void ShowSettingsForm()
    {
        string cfgPath = ConfigPath();
        var d = LoadCfgDict();
        Form f = new Form();
        f.Text = "Win Desktop Helper 设置";
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.Width = 500; f.Height = 500;
        f.MaximizeBox = false; f.MinimizeBox = false;

        int y = 16;
        Label l1 = new Label(); l1.Text = "翻译引擎:"; l1.Left = 16; l1.Top = y; l1.Width = 90; f.Controls.Add(l1);
        ComboBox prov = new ComboBox(); prov.Left = 112; prov.Top = y; prov.Width = 320; prov.DropDownStyle = ComboBoxStyle.DropDownList;
        prov.Items.AddRange(new object[] { "local (本机 LLM 零花费)", "baidu (百度翻译)" });
        prov.SelectedIndex = (d["translate.provider"] == "baidu") ? 1 : 0;
        f.Controls.Add(prov);
        y += 36;

        Label l2 = new Label(); l2.Text = "目标语言:"; l2.Left = 16; l2.Top = y; l2.Width = 90; f.Controls.Add(l2);
        TextBox to = new TextBox(); to.Left = 112; to.Top = y; to.Width = 120; to.Text = d["translate.to"]; f.Controls.Add(to);
        y += 40;

        // ---- 本地 LLM 面板 ----
        Panel pLocal = new Panel(); pLocal.Left = 16; pLocal.Top = y; pLocal.Width = 450; pLocal.Height = 150; pLocal.BorderStyle = BorderStyle.FixedSingle; f.Controls.Add(pLocal);
        Label lL = new Label(); lL.Text = "【本地 LLM】"; lL.Left = 8; lL.Top = 6; lL.Width = 120; lL.Font = new Font(lL.Font, FontStyle.Bold); pLocal.Controls.Add(lL);
        Label lEp = new Label(); lEp.Text = "地址(endpoint):"; lEp.Left = 8; lEp.Top = 32; lEp.Width = 110; pLocal.Controls.Add(lEp);
        TextBox ep = new TextBox(); ep.Left = 124; ep.Top = 29; ep.Width = 310; ep.Text = d["translate.endpoint"]; pLocal.Controls.Add(ep);
        Label lMd = new Label(); lMd.Text = "模型名:"; lMd.Left = 8; lMd.Top = 68; lMd.Width = 110; pLocal.Controls.Add(lMd);
        TextBox model = new TextBox(); model.Left = 124; model.Top = 65; model.Width = 310; model.Text = d["translate.model"]; pLocal.Controls.Add(model);
        Label lAk = new Label(); lAk.Text = "API Key(选填):"; lAk.Left = 8; lAk.Top = 104; lAk.Width = 110; pLocal.Controls.Add(lAk);
        TextBox lak = new TextBox(); lak.Left = 124; lak.Top = 101; lak.Width = 310; lak.PasswordChar = '*'; lak.Text = d["translate.apiKey"]; pLocal.Controls.Add(lak); // 掩码: 不明文显示

        // ---- 百度面板 ----
        Panel pBaidu = new Panel(); pBaidu.Left = 16; pBaidu.Top = y; pBaidu.Width = 450; pBaidu.Height = 150; pBaidu.BorderStyle = BorderStyle.FixedSingle; f.Controls.Add(pBaidu);
        Label lB = new Label(); lB.Text = "【百度翻译】"; lB.Left = 8; lB.Top = 6; lB.Width = 120; lB.Font = new Font(lB.Font, FontStyle.Bold); pBaidu.Controls.Add(lB);
        Label l3 = new Label(); l3.Text = "APP ID:"; l3.Left = 8; l3.Top = 40; l3.Width = 90; pBaidu.Controls.Add(l3);
        TextBox appid = new TextBox(); appid.Left = 104; appid.Top = 37; appid.Width = 330; appid.Text = d["translate.baiduAppId"]; pBaidu.Controls.Add(appid);
        Label l4 = new Label(); l4.Text = "密钥 Key:"; l4.Left = 8; l4.Top = 82; l4.Width = 90; pBaidu.Controls.Add(l4);
        TextBox key = new TextBox(); key.Left = 104; key.Top = 79; key.Width = 330; key.PasswordChar = '*'; key.Text = d["translate.baiduKey"]; pBaidu.Controls.Add(key); // 已填也只显 ***
        Label lBtip = new Label(); lBtip.Text = "（只需 APP ID + 密钥，标准 API 无需第三个参数）"; lBtip.Left = 104; lBtip.Top = 112; lBtip.Width = 330; lBtip.ForeColor = Color.Gray; pBaidu.Controls.Add(lBtip);

        // 动态切换
        prov.SelectedIndexChanged += (s, e) =>
        {
            bool baidu = prov.SelectedIndex == 1;
            pBaidu.Visible = baidu;
            pLocal.Visible = !baidu;
        };
        { bool baidu = prov.SelectedIndex == 1; pBaidu.Visible = baidu; pLocal.Visible = !baidu; }

        y += 162;
        // 测试按钮: 按当前面板填的值实测一次翻译, 不依赖是否已保存 (P1-4)
        Button test = new Button(); test.Text = "测试"; test.Left = 112; test.Top = y; test.Width = 80; f.Controls.Add(test);
        Button save = new Button(); save.Text = "保存"; save.Left = 304; save.Top = y; save.Width = 80; save.DialogResult = DialogResult.OK; f.Controls.Add(save);
        Button cancel = new Button(); cancel.Text = "取消"; cancel.Left = 392; cancel.Top = y; cancel.Width = 80; cancel.DialogResult = DialogResult.Cancel; f.Controls.Add(cancel);
        f.AcceptButton = save; f.CancelButton = cancel;

        // 显示/隐藏密钥 (默认掩码, 需要核对时勾选明文)
        CheckBox chkShow = new CheckBox(); chkShow.Text = "显示密钥"; chkShow.Left = 220; chkShow.Top = y + 4; chkShow.Width = 90; chkShow.AutoSize = false; f.Controls.Add(chkShow);
        chkShow.CheckedChanged += (s, e) => { key.PasswordChar = chkShow.Checked ? '\0' : '*'; lak.PasswordChar = chkShow.Checked ? '\0' : '*'; };

        test.Click += (s, e) =>
        {
            bool isBaidu = prov.SelectedIndex == 1;
            string tAppid = appid.Text.Trim(), tKey = key.Text.Trim();
            string tEp = ep.Text.Trim(), tModel = model.Text.Trim(), tAk = lak.Text.Trim();
            string tTo = to.Text.Trim();
            if (isBaidu && (tAppid.Length == 0 || tKey.Length == 0))
            {
                MessageBox.Show(f, "请先填 APP ID 和密钥再测试", "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            test.Enabled = false; test.Text = "测试中...";
            Task.Run(() =>
            {
                string okMsg = null, errMsg = null;
                try
                {
                    ITranslateProvider tp = isBaidu
                        ? (ITranslateProvider)new BaiduTranslateProvider(tAppid, tKey)
                        : (ITranslateProvider)new LocalLlmTranslateProvider(tEp, tModel, tAk);
                    string r = tp.TranslateAsync("Hello, world", tTo).GetAwaiter().GetResult();
                    okMsg = string.IsNullOrEmpty(r) ? "(返回为空)" : r;
                }
                catch (Exception ex) { errMsg = ex.Message; }
                try
                {
                    f.BeginInvoke(new MethodInvoker(() =>
                    {
                        test.Enabled = true; test.Text = "测试";
                        if (errMsg != null)
                            MessageBox.Show(f, "❌ 测试失败:\n" + errMsg, "翻译测试 (" + (isBaidu ? "baidu" : "local") + ")", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        else
                            MessageBox.Show(f, "✅ 测试成功, 译文: " + okMsg, "翻译测试 (" + (isBaidu ? "baidu" : "local") + ")", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch { } // 窗口已关, 丢弃结果
            });
        };

        // 底部: 配置文件实际路径 + 一键打开 (读写到哪、内容是什么, 用户直接可见 — 排障闭环)
        Label pathLabel = new Label();
        pathLabel.Text = "配置文件: " + cfgPath;
        pathLabel.Left = 16; pathLabel.Top = y + 40; pathLabel.Width = 350;
        pathLabel.ForeColor = Color.Gray; pathLabel.Font = new Font(pathLabel.Font.FontFamily, 8f);
 pathLabel.AutoEllipsis = true; f.Controls.Add(pathLabel);
        Button openCfg = new Button(); openCfg.Text = "打开配置"; openCfg.Left = 392; openCfg.Top = y + 34; openCfg.Width = 80; openCfg.Height = 24; f.Controls.Add(openCfg);
        openCfg.Click += (s, e) =>
        {
            try
            {
                if (!File.Exists(cfgPath)) File.WriteAllText(cfgPath, "{\n}\n", Encoding.UTF8);
                System.Diagnostics.Process.Start("notepad.exe", "\"" + cfgPath + "\"");
            }
            catch (Exception ex) { MessageBox.Show(f, "打开失败: " + ex.Message, "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };

        if (f.ShowDialog() == DialogResult.OK)
        {
            d["translate.provider"] = (prov.SelectedIndex == 1) ? "baidu" : "local";
            d["translate.to"] = to.Text.Trim();
            d["translate.endpoint"] = ep.Text.Trim();
            d["translate.model"] = model.Text.Trim();
            d["translate.apiKey"] = lak.Text.Trim();
            d["translate.baiduAppId"] = appid.Text.Trim();
            d["translate.baiduKey"] = key.Text.Trim();
            try
            {
                SaveCfgDict(d);
                TrayNotify("设置已保存",
                    "provider=" + d["translate.provider"] + ", 写入 " + cfgPath + "，下次 OCR/翻译自动生效");
            }
            catch (Exception ex)
            {
                Log("config SAVE FAILED: " + ex.Message);
                MessageBox.Show("保存失败:\n" + ex.Message + "\n\n目标文件: " + cfgPath, "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
