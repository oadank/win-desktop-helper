using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// M3: 设置/登录入口 (让用户自助填 OCR/翻译配置, 存 shot-service.json 热生效)
// 与 shot-service.cs 同属 ShotService 类(partial), 共享 Cfg/Log/TrayIcon
// UI 深色主题 (与剪贴板历史窗一致); 密钥留空=保留原值 (防止"读显示空→保存→清掉真值"恶性循环)
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

    // ---- 设置窗 (深色, 自绘标题栏可拖动; 托盘菜单"设置..."触发) ----
    static void ShowSettingsForm()
    {
        string cfgPath = ConfigPath();
        var d = LoadCfgDict();
        string loadedBaiduKey = d["translate.baiduKey"]; // 留空保护基准: UI 清空不回写覆盖
        string loadedApiKey = d["translate.apiKey"];

        Color cBg = Color.FromArgb(35, 36, 40), cPanel = Color.FromArgb(28, 29, 33), cField = Color.FromArgb(22, 23, 27),
              cText = Color.FromArgb(225, 228, 232), cDim = Color.FromArgb(130, 136, 146),
              cBtn = Color.FromArgb(52, 54, 62), cAccent = Color.FromArgb(64, 108, 190);

        Form f = new Form();
        f.Text = "设置";
        f.FormBorderStyle = FormBorderStyle.None;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.Size = new Size(500, 446);
        f.TopMost = true;
        f.BackColor = cBg;
        f.KeyPreview = true;

        // ---- 自绘标题栏: 标题 + 可拖动 + × ----
        Panel title = new Panel(); title.Dock = DockStyle.Top; title.Height = 38; title.BackColor = Color.FromArgb(22, 23, 26);
        Label tl = new Label(); tl.Text = "  设置 — 翻译 / OCR"; tl.ForeColor = cText; tl.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        tl.AutoSize = false; tl.Dock = DockStyle.Fill; tl.TextAlign = ContentAlignment.MiddleLeft;
        Button bx = new Button(); bx.Text = "×"; bx.FlatStyle = FlatStyle.Flat; bx.FlatAppearance.BorderSize = 0;
        bx.BackColor = Color.Transparent; bx.ForeColor = cText; bx.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        bx.Size = new Size(38, 38); bx.Dock = DockStyle.Right;
        bx.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        title.Controls.Add(tl); title.Controls.Add(bx);
        MouseEventHandler dragH = (s, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };
        title.MouseDown += dragH; tl.MouseDown += dragH;
        f.Controls.Add(title);

        int LX = 24, FX = 140, FW = 336; // 标签左缘 / 字段左缘 / 字段宽

        // 深色控件工厂
        Func<string, int, int, Label> mkLabel = (text, x, y) =>
        {
            Label l = new Label(); l.Text = text; l.Left = x; l.Top = y; l.AutoSize = true;
            l.ForeColor = cText; l.Font = new Font("Microsoft YaHei UI", 9.5f); f.Controls.Add(l); return l;
        };
        Func<TextBox> mkText = () =>
        {
            TextBox t = new TextBox(); t.BackColor = cField; t.ForeColor = cText; t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = new Font("Microsoft YaHei UI", 9.5f); f.Controls.Add(t); return t;
        };
        Func<Button> mkBtn = () =>
        {
            Button b = new Button(); b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.BackColor = cBtn; b.ForeColor = cText; b.Font = new Font("Microsoft YaHei UI", 9.5f);
            b.Cursor = Cursors.Hand; f.Controls.Add(b); return b;
        };

        mkLabel("翻译引擎:", LX, 52);
        ComboBox prov = new ComboBox(); prov.Left = FX; prov.Top = 49; prov.Width = FW; prov.DropDownStyle = ComboBoxStyle.DropDownList;
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
        f.Controls.Add(prov);

        mkLabel("目标语言:", LX, 90);
        TextBox to = mkText(); to.Left = FX; to.Top = 87; to.Width = 120; to.Text = d["translate.to"];

        // ---- 面板: 本地 LLM ----
        Panel pLocal = new Panel(); pLocal.Left = LX; pLocal.Top = 126; pLocal.Size = new Size(452, 150);
        pLocal.BackColor = cPanel; f.Controls.Add(pLocal);
        Func<Panel, string, int, int, Label> mkPLabel = (p, text, x, y) =>
        {
            Label l = new Label(); l.Text = text; l.Left = x; l.Top = y; l.AutoSize = true;
            l.ForeColor = cDim; l.Font = new Font("Microsoft YaHei UI", 9f); p.Controls.Add(l); return l;
        };
        Func<Panel, TextBox> mkPText = (p) =>
        {
            TextBox t = new TextBox(); t.BackColor = cField; t.ForeColor = cText; t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = new Font("Microsoft YaHei UI", 9.5f); p.Controls.Add(t); return t;
        };
        Label llt = new Label(); llt.Text = "本地 LLM (Ollama)"; llt.Left = 12; llt.Top = 10; llt.AutoSize = true;
        llt.ForeColor = cText; llt.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold); pLocal.Controls.Add(llt);
        mkPLabel(pLocal, "地址 endpoint:", 12, 42);
        TextBox ep = mkPText(pLocal); ep.Left = 130; ep.Top = 39; ep.Width = 306; ep.Text = d["translate.endpoint"];
        mkPLabel(pLocal, "模型名:", 12, 74);
        TextBox model = mkPText(pLocal); model.Left = 130; model.Top = 71; model.Width = 306; model.Text = d["translate.model"];
        mkPLabel(pLocal, "API Key (选填):", 12, 106);
        TextBox lak = mkPText(pLocal); lak.Left = 130; lak.Top = 103; lak.Width = 306; lak.PasswordChar = '*'; lak.Text = d["translate.apiKey"];

        // ---- 面板: 百度 ----
        Panel pBaidu = new Panel(); pBaidu.Left = LX; pBaidu.Top = 126; pBaidu.Size = new Size(452, 150);
        pBaidu.BackColor = cPanel; f.Controls.Add(pBaidu);
        Label lbt = new Label(); lbt.Text = "百度翻译"; lbt.Left = 12; lbt.Top = 10; lbt.AutoSize = true;
        lbt.ForeColor = cText; lbt.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold); pBaidu.Controls.Add(lbt);
        mkPLabel(pBaidu, "APP ID:", 12, 46);
        TextBox appid = mkPText(pBaidu); appid.Left = 130; appid.Top = 43; appid.Width = 306; appid.Text = d["translate.baiduAppId"];
        mkPLabel(pBaidu, "密钥 Key:", 12, 82);
        TextBox key = mkPText(pBaidu); key.Left = 130; key.Top = 79; key.Width = 306; key.PasswordChar = '*'; key.Text = d["translate.baiduKey"];
        Label ltip = new Label(); ltip.Text = "标准 API 只需 APP ID + 密钥；密钥留空保存 = 不修改已存的密钥"; ltip.Left = 12; ltip.Top = 118;
        ltip.AutoSize = true; ltip.ForeColor = cDim; ltip.Font = new Font("Microsoft YaHei UI", 8.5f); pBaidu.Controls.Add(ltip);

        // 引擎切换
        prov.SelectedIndexChanged += (s, e) => { bool bd = prov.SelectedIndex == 1; pBaidu.Visible = bd; pLocal.Visible = !bd; };
        { bool bd = prov.SelectedIndex == 1; pBaidu.Visible = bd; pLocal.Visible = !bd; }

        // ---- 操作行 ----
        CheckBox chkShow = new CheckBox(); chkShow.Text = "显示密钥"; chkShow.Left = FX; chkShow.Top = 288; chkShow.AutoSize = true;
        chkShow.ForeColor = cDim; chkShow.Font = new Font("Microsoft YaHei UI", 9f); f.Controls.Add(chkShow);
        chkShow.CheckedChanged += (s, e) => { char pc = chkShow.Checked ? '\0' : '*'; key.PasswordChar = pc; lak.PasswordChar = pc; };

        Button test = mkBtn(); test.Text = "测试连通"; test.SetBounds(FX, 322, 100, 32);
        Button save = mkBtn(); save.Text = "保存"; save.BackColor = cAccent; save.SetBounds(FX + FW - 96, 322, 96, 32);
        Button cancel = mkBtn(); cancel.Text = "取消"; cancel.SetBounds(FX + FW - 200, 322, 96, 32);
        save.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.AcceptButton = save;

        // ---- 底部: 配置文件路径 + 打开 ----
        Label pl = new Label(); pl.Text = "配置文件: " + cfgPath; pl.Left = LX; pl.Top = 368; pl.AutoSize = false;
        pl.Size = new Size(340, 34); pl.ForeColor = cDim; pl.Font = new Font("Microsoft YaHei UI", 8.5f);
        pl.AutoEllipsis = true; f.Controls.Add(pl);
        Button openCfg = mkBtn(); openCfg.Text = "打开配置"; openCfg.SetBounds(FX + FW - 96, 364, 96, 30);
        openCfg.Click += (s, e) =>
        {
            try
            {
                if (!File.Exists(cfgPath)) File.WriteAllText(cfgPath, "{\n}\n", Encoding.UTF8);
                System.Diagnostics.Process.Start("notepad.exe", "\"" + cfgPath + "\"");
            }
            catch (Exception ex) { MessageBox.Show(f, "打开失败: " + ex.Message, "Win Desktop Helper"); }
        };

        // Esc = 取消
        f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { f.DialogResult = DialogResult.Cancel; f.Close(); } };

        // ---- 测试连通: 按当前面板值实测一次翻译 (不依赖已保存) ----
        test.Click += (s, e) =>
        {
            bool isBaidu = prov.SelectedIndex == 1;
            string tAppid = appid.Text.Trim(), tKey = key.Text.Trim();
            string tEp = ep.Text.Trim(), tModel = model.Text.Trim(), tAk = lak.Text.Trim(), tTo = to.Text.Trim();
            if (isBaidu && (tAppid.Length == 0 || tKey.Length == 0))
            {
                // 留空保护: 用已存密钥测
                if (tAppid.Length == 0 && loadedBaiduKey.Length == 0)
                { MessageBox.Show(f, "请先填 APP ID 和密钥", "翻译测试"); return; }
                if (tKey.Length == 0) tKey = loadedBaiduKey;
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
                        test.Enabled = true; test.Text = "测试连通";
                        if (errMsg != null)
                            MessageBox.Show(f, "❌ 测试失败:\n" + errMsg, "翻译测试 (" + (isBaidu ? "baidu" : "local") + ")", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        else
                            MessageBox.Show(f, "✅ 测试成功, 译文: " + okMsg, "翻译测试 (" + (isBaidu ? "baidu" : "local") + ")", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            // 密钥留空 = 保留文件原值 (防"显示空→保存→清掉真值"恶性循环)
            d["translate.apiKey"] = lak.Text.Trim().Length > 0 ? lak.Text.Trim() : loadedApiKey;
            d["translate.baiduAppId"] = appid.Text.Trim();
            d["translate.baiduKey"] = key.Text.Trim().Length > 0 ? key.Text.Trim() : loadedBaiduKey;
            try
            {
                SaveCfgDict(d);
                TrayNotify("设置已保存", "provider=" + d["translate.provider"] + "，已写入配置文件，OCR/翻译即时生效");
            }
            catch (Exception ex)
            {
                Log("config SAVE FAILED: " + ex.Message);
                MessageBox.Show("保存失败:\n" + ex.Message + "\n\n目标文件: " + cfgPath, "Win Desktop Helper");
            }
        }
    }
}
