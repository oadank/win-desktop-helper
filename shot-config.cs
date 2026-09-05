using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// 设置中心 (多分类配置页): 翻译 / OCR / 截图 / 剪贴板 / 任务栏音量
// 持久化 shot-service.json 多节; 音量/剪贴板条数保存即生效, 热键/目录重启生效
partial class ShotService
{
    // 配置文件路径: 读写统一走这里
    static string ConfigPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot-service.json");
    }

    // 翻译/OCR 配置读取 (原有键)
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

    // 多节写回 (translate/ocr/capture/clipboard/volume)
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
        sb.Append("  },\n");
        sb.Append("  \"capture\": {\n");
        sb.Append("    \"dir\": " + J(d["capture.dir"]) + ",\n");
        sb.Append("    \"hotkeyRegion\": " + J(d["capture.hotkeyRegion"]) + ",\n");
        sb.Append("    \"hotkeyFull\": " + J(d["capture.hotkeyFull"]) + ",\n");
        sb.Append("    \"hotkeyPin\": " + J(d["capture.hotkeyPin"]) + "\n");
        sb.Append("  },\n");
        sb.Append("  \"clipboard\": { \"enabled\": " + J(d["clipboard.enabled"]) + ", \"max\": " + J(d["clipboard.max"]) + " },\n");
        sb.Append("  \"volume\": { \"enabled\": " + J(d["volume.enabled"]) + ", \"step\": " + J(d["volume.step"]) + ", \"reverse\": " + J(d["volume.reverse"]) + " }\n");
        sb.Append("}\n");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Log("config saved: " + path + " (provider=" + d["translate.provider"] + ")");
    }

    // 最小 json 字符串转义
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

    // ==================== 设置中心 (左分类列表 + 右页) ====================
    static void ShowSettingsForm()
    {
        string cfgPath = ConfigPath();
        var d = LoadCfgDict();
        // 其它节读入
        d["capture.dir"] = Cfg("capture.dir", "");
        d["capture.hotkeyRegion"] = Cfg("capture.hotkeyRegion", "");
        d["capture.hotkeyFull"] = Cfg("capture.hotkeyFull", "");
        d["capture.hotkeyPin"] = Cfg("capture.hotkeyPin", "");
        d["clipboard.enabled"] = Cfg("clipboard.enabled", "1");
        d["clipboard.max"] = Cfg("clipboard.max", "50");
        d["volume.enabled"] = Cfg("volume.enabled", "1");
        d["volume.step"] = Cfg("volume.step", "2");
        d["volume.reverse"] = Cfg("volume.reverse", "0");

        string loadedBaiduKey = d["translate.baiduKey"];
        string loadedApiKey = d["translate.apiKey"];

        Color cBg = Color.FromArgb(35, 36, 40), cPanel = Color.FromArgb(28, 29, 33), cField = Color.FromArgb(22, 23, 27),
              cText = Color.FromArgb(225, 228, 232), cDim = Color.FromArgb(130, 136, 146),
              cBtn = Color.FromArgb(52, 54, 62), cAccent = Color.FromArgb(64, 108, 190);

        Form f = new Form();
        f.Text = "设置";
        f.FormBorderStyle = FormBorderStyle.None;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.Size = new Size(620, 494);
        f.TopMost = true;
        f.BackColor = cBg;
        f.KeyPreview = true;

        DarkUI.MakeTitleBar(f, "  设置");

        // ---- 左侧分类列表 ----
        ListBox cats = new ListBox();
        cats.Left = 12; cats.Top = 46; cats.Width = 118; cats.Height = 400;
        cats.BorderStyle = BorderStyle.FixedSingle;
        cats.BackColor = cPanel; cats.ForeColor = cText;
        cats.Font = new Font("Microsoft YaHei UI", 9.5f);
        cats.Items.AddRange(new object[] { "翻译", "OCR", "截图", "剪贴板", "任务栏音量" });
        f.Controls.Add(cats);

        // ---- 右侧页面容器 (486 宽) ----
        int PX = 142, PY = 46, PW = 462, PH = 400;
        Func<string, Panel> mkPage = (name) =>
        {
            Panel p = new Panel(); p.Left = PX; p.Top = PY; p.Size = new Size(PW, PH);
            p.BackColor = cBg; p.Visible = false; f.Controls.Add(p); return p;
        };
        Panel pgTr = mkPage("tr"), pgOcr = mkPage("ocr"), pgCap = mkPage("cap"), pgClip = mkPage("clip"), pgVol = mkPage("vol");

        // 深色控件工厂 (页面内)
        Func<Panel, string, int, int, Label> mkL = (p, text, x, y) =>
        {
            Label l = new Label(); l.Text = text; l.Left = x; l.Top = y; l.AutoSize = true;
            l.ForeColor = cText; l.Font = new Font("Microsoft YaHei UI", 9.5f); p.Controls.Add(l); return l;
        };
        Func<Panel, string, int, int, Label> mkDim = (p, text, x, y) =>
        {
            Label l = new Label(); l.Text = text; l.Left = x; l.Top = y; l.AutoSize = true;
            l.ForeColor = cDim; l.Font = new Font("Microsoft YaHei UI", 8.5f); p.Controls.Add(l); return l;
        };
        Func<Panel, TextBox> mkT = (p) =>
        {
            TextBox t = new TextBox(); t.BackColor = cField; t.ForeColor = cText; t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = new Font("Microsoft YaHei UI", 9.5f); p.Controls.Add(t); return t;
        };
        Func<Panel, CheckBox> mkC = (p) =>
        {
            CheckBox c = new CheckBox(); c.ForeColor = cText; c.Font = new Font("Microsoft YaHei UI", 9.5f); p.Controls.Add(c); return c;
        };

        // ==================== 页 1: 翻译 ====================
        int LX = 14, FX = 132, FW = 310;
        mkL(pgTr, "翻译引擎:", LX, 14);
        ComboBox prov = new ComboBox(); prov.Left = FX; prov.Top = 11; prov.Width = FW; prov.DropDownStyle = ComboBoxStyle.DropDownList;
        prov.FlatStyle = FlatStyle.Flat; prov.BackColor = cField; prov.ForeColor = cText;
        prov.Font = new Font("Microsoft YaHei UI", 9.5f);
        prov.Items.AddRange(new object[] { "local   本机 LLM · 零花费", "baidu   百度翻译 · 需 APP ID + 密钥" });
        prov.DrawMode = DrawMode.OwnerDrawFixed; prov.ItemHeight = 22;
        prov.DrawItem += (s, e) =>
        {
            e.DrawBackground();
            bool sel2 = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush b = new SolidBrush(sel2 ? cAccent : cField)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, prov.Items[e.Index].ToString(), prov.Font, e.Bounds, cText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        prov.SelectedIndex = (d["translate.provider"] == "baidu") ? 1 : 0;
        pgTr.Controls.Add(prov);

        mkL(pgTr, "目标语言:", LX, 52);
        TextBox to = mkT(pgTr); to.Left = FX; to.Top = 49; to.Width = 120; to.Text = d["translate.to"];
        mkDim(pgTr, "(点翻译的自动检测不受此项影响: 中文自动译英/英文自动译中)", LX, 76);

        Panel pLocal = new Panel(); pLocal.Left = LX; pLocal.Top = 100; pLocal.Size = new Size(PW - LX * 2, 152);
        pLocal.BackColor = cPanel; pgTr.Controls.Add(pLocal);
        Label llt = new Label(); llt.Text = "本地 LLM (Ollama)"; llt.Left = 12; llt.Top = 8; llt.AutoSize = true;
        llt.ForeColor = cText; llt.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold); pLocal.Controls.Add(llt);
        mkL(pLocal, "地址 endpoint:", 12, 38);
        TextBox ep = mkT(pLocal); ep.Left = 130; ep.Top = 35; ep.Width = 296; ep.Text = d["translate.endpoint"];
        mkL(pLocal, "模型名:", 12, 70);
        TextBox model = mkT(pLocal); model.Left = 130; model.Top = 67; model.Width = 296; model.Text = d["translate.model"];
        mkL(pLocal, "API Key (选填):", 12, 102);
        TextBox lak = mkT(pLocal); lak.Left = 130; lak.Top = 99; lak.Width = 200; lak.PasswordChar = '*'; lak.Text = d["translate.apiKey"];
        CheckBox chkShowL = mkC(pLocal); chkShowL.Text = "显示"; chkShowL.Left = 340; chkShowL.Top = 101; chkShowL.AutoSize = true;
        chkShowL.ForeColor = cDim; chkShowL.Font = new Font("Microsoft YaHei UI", 9f);

        Panel pBaidu = new Panel(); pBaidu.Left = LX; pBaidu.Top = 100; pBaidu.Size = new Size(PW - LX * 2, 152);
        pBaidu.BackColor = cPanel; pgTr.Controls.Add(pBaidu);
        Label lbt = new Label(); lbt.Text = "百度翻译"; lbt.Left = 12; lbt.Top = 8; lbt.AutoSize = true;
        lbt.ForeColor = cText; lbt.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold); pBaidu.Controls.Add(lbt);
        mkL(pBaidu, "APP ID:", 12, 42);
        TextBox appid = mkT(pBaidu); appid.Left = 130; appid.Top = 39; appid.Width = 296; appid.Text = d["translate.baiduAppId"];
        mkL(pBaidu, "密钥 Key:", 12, 74);
        TextBox key = mkT(pBaidu); key.Left = 130; key.Top = 71; key.Width = 200; key.PasswordChar = '*'; key.Text = d["translate.baiduKey"];
        CheckBox chkShow = mkC(pBaidu); chkShow.Text = "显示"; chkShow.Left = 340; chkShow.Top = 73; chkShow.AutoSize = true;
        chkShow.ForeColor = cDim; chkShow.Font = new Font("Microsoft YaHei UI", 9f);
        mkDim(pBaidu, "留空保存 = 不修改已存密钥", 130, 108);
        chkShow.CheckedChanged += (s, e) => { char pc = chkShow.Checked ? '\0' : '*'; key.PasswordChar = pc; lak.PasswordChar = pc; };
        chkShowL.CheckedChanged += (s, e) => { char pc = chkShowL.Checked ? '\0' : '*'; key.PasswordChar = pc; lak.PasswordChar = pc; chkShow.Checked = chkShowL.Checked; };

        prov.SelectedIndexChanged += (s, e) => { bool bd = prov.SelectedIndex == 1; pBaidu.Visible = bd; pLocal.Visible = !bd; };
        { bool bd = prov.SelectedIndex == 1; pBaidu.Visible = bd; pLocal.Visible = !bd; }

        // 测试区 (内置英文示例, 结果写界面)
        Label demoLabel = new Label(); demoLabel.Text = "示例: The quick brown fox jumps over the lazy dog.";
        demoLabel.Left = LX; demoLabel.Top = 262; demoLabel.AutoSize = true;
        demoLabel.ForeColor = cDim; demoLabel.Font = new Font("Consolas", 9f); pgTr.Controls.Add(demoLabel);
        Button test = new Button(); test.Text = "测试"; test.FlatStyle = FlatStyle.Flat; test.FlatAppearance.BorderSize = 0;
        test.BackColor = cBtn; test.ForeColor = cText; test.Font = new Font("Microsoft YaHei UI", 9.5f);
        test.Cursor = Cursors.Hand; test.SetBounds(PW - LX - 100, 256, 96, 28); pgTr.Controls.Add(test); test.BringToFront();
        TextBox testResult = new TextBox(); testResult.Multiline = true; testResult.ReadOnly = true;
        testResult.Left = LX; testResult.Top = 284; testResult.Width = PW - LX * 2; testResult.Height = 52;
        testResult.BackColor = cField; testResult.ForeColor = cText; testResult.BorderStyle = BorderStyle.FixedSingle;
        testResult.Font = new Font("Microsoft YaHei UI", 9f);
        testResult.Text = "（点「测试」后, 上面这句英文翻译成中文的结果显示在这里）";
        pgTr.Controls.Add(testResult);


        // ==================== 页 2: OCR ====================
        mkL(pgOcr, "OCR 引擎:", LX, 14);
        ComboBox ocrProv = new ComboBox(); ocrProv.Left = FX; ocrProv.Top = 11; ocrProv.Width = FW; ocrProv.DropDownStyle = ComboBoxStyle.DropDownList;
        ocrProv.FlatStyle = FlatStyle.Flat; ocrProv.BackColor = cField; ocrProv.ForeColor = cText;
        ocrProv.Font = new Font("Microsoft YaHei UI", 9.5f);
        ocrProv.Items.AddRange(new object[] { "qwen3vl   本机 Ollama (零花费)" });
        try { ocrProv.SelectedIndex = (d["ocr.provider"] == "qwen3vl") ? 0 : 0; } catch { }
        pgOcr.Controls.Add(ocrProv);
        mkL(pgOcr, "endpoint:", LX, 52);
        TextBox ocrEp = mkT(pgOcr); ocrEp.Left = FX; ocrEp.Top = 49; ocrEp.Width = FW; ocrEp.Text = d["ocr.endpoint"];
        mkDim(pgOcr, "本机 Ollama 需先拉取模型: ollama pull qwen3-vl:4b-instruct", LX, 80);

        // ==================== 页 3: 截图 ====================
        mkL(pgCap, "保存目录:", LX, 14);
        TextBox capDir = mkT(pgCap); capDir.Left = FX; capDir.Top = 11; capDir.Width = FW; capDir.Text = d["capture.dir"];
        mkDim(pgCap, "留空 = 图片库\\Screenshots (每用户各自目录)。支持环境变量如 %USERPROFILE%\\Pictures\\Shots", LX, 40);
        mkDim(pgCap, "当前生效: " + ShotDir, LX, 60);
        mkL(pgCap, "区域截图热键:", LX, 92);
        TextBox hkRegion = mkT(pgCap); hkRegion.Left = FX; hkRegion.Top = 89; hkRegion.Width = 200; hkRegion.Text = d["capture.hotkeyRegion"];
        mkDim(pgCap, "留空 = 默认降级链", FX + 208, 92);
        mkL(pgCap, "全屏截图热键:", LX, 126);
        TextBox hkFull = mkT(pgCap); hkFull.Left = FX; hkFull.Top = 123; hkFull.Width = 200; hkFull.Text = d["capture.hotkeyFull"];
        mkDim(pgCap, "留空 = 默认降级链", FX + 208, 126);
        mkL(pgCap, "贴图热键:", LX, 160);
        TextBox hkPin = mkT(pgCap); hkPin.Left = FX; hkPin.Top = 157; hkPin.Width = 200; hkPin.Text = d["capture.hotkeyPin"];
        mkDim(pgCap, "留空 = 默认降级链", FX + 208, 160);
        mkDim(pgCap, "热键格式: Ctrl+Alt+S / Win+Shift+A / F9 等, 修改保存后重启生效", LX, 190);
        mkDim(pgCap, "截图后: 双击选区=复制 · 中键点选区=贴图 · 右键=重新框选 · Esc=退出", LX, 212);

        // ==================== 页 4: 剪贴板 ====================
        CheckBox clipEn = mkC(pgClip); clipEn.Text = "启用剪贴板历史 (自动记录复制的文本)"; clipEn.Left = LX; clipEn.Top = 14; clipEn.AutoSize = true;
        clipEn.Checked = d["clipboard.enabled"] == "1";
        mkL(pgClip, "最大条数:", LX, 50);
        TextBox clipMaxT = mkT(pgClip); clipMaxT.Left = FX; clipMaxT.Top = 47; clipMaxT.Width = 80; clipMaxT.Text = d["clipboard.max"];
        mkDim(pgClip, "5 - 500 条, 超出自动淘汰最旧", FX + 90, 50);
        mkDim(pgClip, "热键 " + (clipHotkeyName == "" ? "Ctrl+Alt+V" : clipHotkeyName) + " 弹历史 · 保存后条数立即生效", LX, 84);

        // ==================== 页 5: 任务栏音量 ====================
        CheckBox volEn = mkC(pgVol); volEn.Text = "启用任务栏滚轮调音量 (滚轮=音量, 中键=静音)"; volEn.Left = LX; volEn.Top = 14; volEn.AutoSize = true;
        volEn.Checked = d["volume.enabled"] == "1";
        mkL(pgVol, "每次步进 (%):", LX, 50);
        TextBox volStepT = mkT(pgVol); volStepT.Left = FX; volStepT.Top = 47; volStepT.Width = 80; volStepT.Text = d["volume.step"];
        mkDim(pgVol, "1 - 20, 默认 2", FX + 90, 50);
        CheckBox volRev = mkC(pgVol); volRev.Text = "反向 (滚轮向上 = 减小音量)"; volRev.Left = LX; volRev.Top = 84; volRev.AutoSize = true;
        volRev.Checked = d["volume.reverse"] == "1";
        mkDim(pgVol, "保存后立即生效, 无需重启", LX, 118);

        // 分类切换
        Panel[] pages = { pgTr, pgOcr, pgCap, pgClip, pgVol };
        Action showPage = delegate
        {
            for (int i = 0; i < pages.Length; i++) pages[i].Visible = (cats.SelectedIndex == i);
        };
        cats.SelectedIndex = 0;
        cats.SelectedIndexChanged += (s, e) => showPage();
        showPage();

        // 按钮工厂 (窗体级)
        Button save = new Button(); save.Text = "保存"; save.FlatStyle = FlatStyle.Flat; save.FlatAppearance.BorderSize = 0;
        save.BackColor = cAccent; save.ForeColor = cText; save.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        save.Cursor = Cursors.Hand; save.SetBounds(f.ClientSize.Width - 210, f.ClientSize.Height - 44, 96, 32); f.Controls.Add(save); save.BringToFront();
        Button cancel = new Button(); cancel.Text = "取消"; cancel.FlatStyle = FlatStyle.Flat; cancel.FlatAppearance.BorderSize = 0;
        cancel.BackColor = cBtn; cancel.ForeColor = cText; cancel.Font = new Font("Microsoft YaHei UI", 9.5f);
        cancel.Cursor = Cursors.Hand; cancel.SetBounds(f.ClientSize.Width - 106, f.ClientSize.Height - 44, 96, 32); f.Controls.Add(cancel); cancel.BringToFront();
        save.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };

        // Esc = 取消
        f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { f.DialogResult = DialogResult.Cancel; f.Close(); } };

        // ---- 测试: 内置英文示例 -> 翻译成中文, 结果写界面 (零弹窗) ----
        test.Click += (s, e) =>
        {
            bool isBaidu = prov.SelectedIndex == 1;
            string tAppid = appid.Text.Trim(), tKey = key.Text.Trim();
            string tEp = ep.Text.Trim(), tModel = model.Text.Trim(), tAk = lak.Text.Trim();
            if (isBaidu && (tAppid.Length == 0 || tKey.Length == 0))
            {
                if (tAppid.Length == 0 && loadedBaiduKey.Length == 0)
                { testResult.ForeColor = Color.FromArgb(230, 120, 110); testResult.Text = "请先填 APP ID 和密钥"; return; }
                if (tKey.Length == 0) tKey = loadedBaiduKey;
            }
            test.Enabled = false; test.Text = "测试中...";
            testResult.ForeColor = cDim;
            testResult.Text = "测试中, 请稍候...";
            Task.Run(() =>
            {
                string okMsg = null, errMsg = null;
                try
                {
                    ITranslateProvider tp = isBaidu
                        ? (ITranslateProvider)new BaiduTranslateProvider(tAppid, tKey)
                        : (ITranslateProvider)new LocalLlmTranslateProvider(tEp, tModel, tAk);
                    string r = tp.TranslateAsync("The quick brown fox jumps over the lazy dog.", "zh").GetAwaiter().GetResult();
                    okMsg = string.IsNullOrEmpty(r) ? "(返回为空 — 检查引擎/地址/模型)" : r;
                }
                catch (Exception ex) { errMsg = ex.Message; }
                try
                {
                    f.BeginInvoke(new MethodInvoker(() =>
                    {
                        test.Enabled = true; test.Text = "测试";
                        if (errMsg != null)
                        {
                            testResult.ForeColor = Color.FromArgb(230, 120, 110);
                            testResult.Text = "✗ 测试失败 (" + (isBaidu ? "baidu" : "local") + "): " + errMsg;
                        }
                        else
                        {
                            testResult.ForeColor = Color.FromArgb(120, 200, 140);
                            testResult.Text = "✓ 测试成功 (" + (isBaidu ? "baidu" : "local") + "): " + okMsg;
                        }
                    }));
                }
                catch { }
            });
        };

        if (f.ShowDialog() == DialogResult.OK)
        {
            d["translate.provider"] = (prov.SelectedIndex == 1) ? "baidu" : "local";
            d["translate.to"] = to.Text.Trim();
            d["translate.endpoint"] = ep.Text.Trim();
            d["translate.model"] = model.Text.Trim();
            d["translate.apiKey"] = lak.Text.Trim().Length > 0 ? lak.Text.Trim() : loadedApiKey;
            d["translate.baiduAppId"] = appid.Text.Trim();
            d["translate.baiduKey"] = key.Text.Trim().Length > 0 ? key.Text.Trim() : loadedBaiduKey;
            d["ocr.provider"] = "qwen3vl";
            d["ocr.endpoint"] = ocrEp.Text.Trim();
            d["capture.dir"] = capDir.Text.Trim();
            d["capture.hotkeyRegion"] = hkRegion.Text.Trim();
            d["capture.hotkeyFull"] = hkFull.Text.Trim();
            d["capture.hotkeyPin"] = hkPin.Text.Trim();
            d["clipboard.enabled"] = clipEn.Checked ? "1" : "0";
            int cmv; d["clipboard.max"] = (int.TryParse(clipMaxT.Text.Trim(), out cmv) && cmv >= 5 && cmv <= 500) ? cmv.ToString() : "50";
            d["volume.enabled"] = volEn.Checked ? "1" : "0";
            int vsv; d["volume.step"] = (int.TryParse(volStepT.Text.Trim(), out vsv) && vsv >= 1 && vsv <= 20) ? vsv.ToString() : "2";
            d["volume.reverse"] = volRev.Checked ? "1" : "0";
            try
            {
                SaveCfgDict(d);
                // 立即生效项
                clipMax = int.Parse(d["clipboard.max"]);
                clipEnabled = d["clipboard.enabled"] == "1" ? 1 : 0;
                volEnabled = d["volume.enabled"] == "1" ? 1 : 0;
                volStep = int.Parse(d["volume.step"]);
                volReverse = d["volume.reverse"] == "1" ? 1 : 0;
                // 截图目录即时生效 (新截图即用新目录)
                if (d["capture.dir"].Trim().Length > 0)
                {
                    string nd = Environment.ExpandEnvironmentVariables(d["capture.dir"].Trim());
                    if (nd != ShotDir) { ShotDir = nd; try { if (!Directory.Exists(ShotDir)) Directory.CreateDirectory(ShotDir); } catch { } }
                }
                else
                {
                    string defDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                    if (ShotDir != defDir) ShotDir = defDir;
                }
                TrayNotify("设置已保存", "音量/剪贴板/截图目录已即时生效；热键修改重启后生效");
            }
            catch (Exception ex)
            {
                Log("config SAVE FAILED: " + ex.Message);
                MessageBox.Show("保存失败:\n" + ex.Message + "\n\n目标文件: " + cfgPath, "Win Desktop Helper");
            }
        }
    }

    // 窗体级按钮工厂 (设置中心用)
    static Button mkBtn(Form f, Color back, Color fore)
    {
        Button b = new Button(); b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
        b.BackColor = back; b.ForeColor = fore; b.Font = new Font("Microsoft YaHei UI", 9.5f);
        b.Cursor = Cursors.Hand; f.Controls.Add(b); return b;
    }
}
