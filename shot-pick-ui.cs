using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// 划词悬浮球的三个窗体: 小圆点 / 工具条 / 结果卡片
//
// 两条铁律(都踩过, 别改回去):
// 1) 绝不抢前台: 全部 ShowWithoutActivation + WS_EX_NOACTIVATE + TOOLWINDOW。
// 2) 这些窗体收不到 WinForms 的 MouseEnter/Click(免激活窗实测收不到), 所以 hover/点击/拖动
//    一律由 shot-pick.cs 的低级鼠标钩子 + PickTick 心跳驱动, 窗体只负责画 + 给出命中区。
//
// 另外: 构造函数里设的 TopMost=true 会被 ShowWithoutActivation 路径丢掉, 必须 Show() 之后
// 显式 SetWindowPos(HWND_TOPMOST) —— 见 shot-pick.cs 的 PickTopMost。
//
// 圆角用 TransparencyKey + 自绘实现(不用 Region): Region 在 PerMonitorV2 下会被 Show() 时
// 的 DPI suggested rect 改写尺寸, 导致内容被裁 —— 这个坑 annotation 弹层已经踩过。

static class PickStyle
{
    public static readonly Color Key = Color.FromArgb(255, 0, 254);      // 透出色, 真实界面不会出现
    public static readonly Color CardBg = Color.FromArgb(28, 29, 34);
    public static readonly Color Border = Color.FromArgb(62, 65, 74);
    public static readonly Color InkHi = Color.FromArgb(232, 234, 237);
    public static readonly Color InkMid = Color.FromArgb(158, 164, 172);
    public static readonly Color InkDim = Color.FromArgb(112, 118, 126);
    public static readonly Color Field = Color.FromArgb(37, 39, 46);
    public static readonly Color Btn = Color.FromArgb(52, 55, 64);
    public static readonly Color Accent = Color.FromArgb(24, 110, 210);

    // 免激活悬浮窗统一关 DPI 自动缩放: 命中矩形按物理像素算, 缩放会让点击偏位
    public static void ApplyChrome(Form f)
    {
        f.FormBorderStyle = FormBorderStyle.None;
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.StartPosition = FormStartPosition.Manual;
        f.AutoScaleMode = AutoScaleMode.None;
        f.BackColor = Key;
        f.TransparencyKey = Key;
    }

    public static void PaintPanel(Form f, PaintEventArgs e, int w, int h, int radius)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Key);
        using (var path = Round(w, h, radius))
        using (var bg = new SolidBrush(CardBg))
            e.Graphics.FillPath(bg, path);
        using (var pen = new Pen(Border, 1f))
        using (var p2 = Round(w - 1, h - 1, radius))
            e.Graphics.DrawPath(pen, p2);
    }

    public static GraphicsPath Round(int w, int h, int r)
    {
        var p = new GraphicsPath();
        if (r <= 0) { p.AddRectangle(new Rectangle(0, 0, w, h)); return p; }
        int d = r * 2;
        if (d > w) d = w;
        if (d > h) d = h;
        p.AddArc(0, 0, d, d, 180, 90);
        p.AddArc(w - d, 0, d, d, 270, 90);
        p.AddArc(w - d, h - d, d, d, 0, 90);
        p.AddArc(0, h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ★ 字体一律用【像素】单位, 绝不用 Point —— 布局是 AutoScaleMode.None 按物理像素写死的,
    //   而 Point 是逻辑单位会被系统 DPI 缩放(本机 125%)放大 1.25 倍 → 字变大而行高不变 →
    //   文字互相挤压重叠, 肉眼看着就是"字太大+像乱码"(2026-09-07 老大截图实锤)。
    //   参数值 = 期望的物理像素字号(原磅值 × 96/72 换算)。
    public static Font F(float px, FontStyle st) { return new Font("Microsoft YaHei UI", px, st, GraphicsUnit.Pixel); }
    public const float FS_TITLE = 13f;   // 原 9.5pt 粗体
    public const float FS_BODY = 14f;    // 原 10.5pt
    public const float FS_MID = 12f;     // 原 9pt
    public const float FS_SMALL = 11f;   // 原 8.5pt
    public const float FS_TINY = 10f;    // 原 7.5pt
}

// ==================== 小圆点 ====================
sealed class PickDotForm : Form
{
    public const int DOT = 26;

    public PickDotForm()
    {
        PickStyle.ApplyChrome(this);
        Size = new Size(DOT, DOT);
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        // 只加 TOOLWINDOW|NOACTIVATE。手加 WS_EX_LAYERED 会让整窗全透明(窗口在但屏幕上看不见)
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x00000080 | 0x08000000; return cp; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(PickStyle.Key);
        using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillEllipse(sh, 4, 5, DOT - 7, DOT - 7);          // 投影
        using (var br = new SolidBrush(PickStyle.Accent))
            g.FillEllipse(br, 3, 3, DOT - 7, DOT - 7);          // 实心蓝
        using (var pen = new Pen(Color.White, 2f))
            g.DrawEllipse(pen, 3, 3, DOT - 7, DOT - 7);         // 白描边, 深浅背景都能看见
        g.FillEllipse(Brushes.White, DOT / 2 - 2, DOT / 2 - 2, 4, 4);
    }
}

// ==================== 工具条: 翻译 / 问 AI / 复制 ====================
sealed class PickBarForm : Form
{
    public const int BAR_W = 200, BAR_H = 40;
    public Action<string> OnAction;          // 供外部直接派发; 物理点击走钩子命中 PickBarActionAt
    // 每个按钮 [左,右) 本地 x 区间 + 动作(顺序与 AddBtn 一致), 供钩子做命中
    public string[] ActIds = new string[0];
    public int[] ActXs = new int[0];
    const int BTN_Y = 7, BTN_H = 26, BTN_W = 58;

    public PickBarForm()
    {
        PickStyle.ApplyChrome(this);
        Size = new Size(BAR_W, BAR_H);
        DoubleBuffered = true;
        AddBtn("翻译", 6, "translate");
        AddBtn("问 AI", 72, "ask");
        AddBtn("复制", 138, "copy");
    }
    void AddBtn(string text, int x, string act)
    {
        var al = new System.Collections.Generic.List<string>(ActIds); al.Add(act); ActIds = al.ToArray();
        var xl = new System.Collections.Generic.List<int>(ActXs); xl.Add(x); xl.Add(x + BTN_W + 3); ActXs = xl.ToArray();
        var b = new Button();
        b.Text = text;
        b.Location = new Point(x, BTN_Y);
        b.Size = new Size(BTN_W, BTN_H);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = PickStyle.Btn;
        b.ForeColor = PickStyle.InkHi;
        b.Font = PickStyle.F(PickStyle.FS_MID, FontStyle.Regular);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        // 不订阅 Click: 免激活窗收不到, 且会与钩子命中双触发
        Controls.Add(b);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x00000080 | 0x08000000; return cp; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        PickStyle.PaintPanel(this, e, BAR_W, BAR_H, 10);
    }
}

// ==================== 结果卡片 ====================
// 420x300: 标题栏(=拖动区) / 正文 / 原文(=拖动区) / 复制结果 + 复制原文
sealed class PickCardForm : Form
{
    public const int CARD_W = 420, CARD_H = 300;
    public const int HEAD_H = 38;
    const int PAD = 10;
    const int BODY_Y = HEAD_H + 6;               // 44
    const int BODY_H = 148;
    const int SRC_Y = BODY_Y + BODY_H + 8;       // 200
    const int SRC_H = 46;
    const int BTN_Y = SRC_Y + SRC_H + 8;         // 254
    const int BTN_H = 28;
    const int BTN_W = 76;                        // 三个按钮(反转翻译/复制结果/复制原文)要放得下

    Label titleL;
    TextBox body, src;
    Button swapB, copyB, copySrcB, closeB;

    public string BodyText { get { return body == null ? "" : body.Text; } }
    public string SourceText { get { return src == null ? "" : src.Text; } }

    public PickCardForm()
    {
        PickStyle.ApplyChrome(this);
        Size = new Size(CARD_W, CARD_H);
        DoubleBuffered = true;
        Font = PickStyle.F(PickStyle.FS_MID, FontStyle.Regular);

        closeB = MakeBtn("×", CARD_W - 34, 8, 26, 22);
        swapB = MakeBtn("反转翻译", PAD, BTN_Y, BTN_W, BTN_H);
        copyB = MakeBtn("复制结果", PAD + BTN_W + 6, BTN_Y, BTN_W, BTN_H);
        copySrcB = MakeBtn("复制原文", PAD + (BTN_W + 6) * 2, BTN_Y, BTN_W, BTN_H);

        titleL = new Label();
        titleL.Text = "处理中";
        titleL.ForeColor = Color.FromArgb(205, 210, 216);
        titleL.BackColor = Color.Transparent;
        titleL.Font = PickStyle.F(PickStyle.FS_TITLE, FontStyle.Bold);
        titleL.Location = new Point(PAD + 24, 0);
        titleL.Size = new Size(CARD_W - (PAD + 24) - 40, HEAD_H);
        titleL.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(titleL);

        body = MakeField(PAD, BODY_Y, CARD_W - PAD * 2, BODY_H, PickStyle.InkHi, PickStyle.FS_BODY);
        src = MakeField(PAD, SRC_Y, CARD_W - PAD * 2, SRC_H, PickStyle.InkMid, PickStyle.FS_MID);
    }

    Button MakeBtn(string text, int x, int y, int w, int h)
    {
        var b = new Button();
        b.Text = text;
        b.Location = new Point(x, y);
        b.Size = new Size(w, h);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = PickStyle.Btn;
        b.ForeColor = PickStyle.InkHi;
        b.Font = PickStyle.F(PickStyle.FS_MID, FontStyle.Regular);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        Controls.Add(b);   // 命中统一走 HitButton(钩子驱动), 不订阅 Click
        return b;
    }

    TextBox MakeField(int x, int y, int w, int h, Color fg, float size)
    {
        var t = new TextBox();
        t.Multiline = true;
        t.ReadOnly = true;
        t.ScrollBars = ScrollBars.Vertical;
        t.BorderStyle = BorderStyle.None;
        t.BackColor = PickStyle.Field;
        t.ForeColor = fg;
        t.Font = PickStyle.F(size, FontStyle.Regular);
        t.Location = new Point(x, y);
        t.Size = new Size(w, h);
        t.WordWrap = true;
        t.TabStop = false;
        t.Cursor = Cursors.Default;      // 只读展示: 别画文本光标
        Controls.Add(t);
        return t;
    }

    public void SetContent(string title, string text, string source)
    {
        titleL.Text = title;
        body.Text = text ?? "";
        try { body.SelectionStart = 0; body.ScrollToCaret(); } catch { }
        src.Text = string.IsNullOrEmpty(source) ? "" : "原文：" + source;
        // 「反转翻译」只对翻译结果有意义(把译文再翻回去), AI 回答上隐藏
        if (swapB != null) swapB.Visible = (title ?? "").IndexOf("翻译") >= 0;
    }
    public void SetTitle(string title) { if (titleL != null) titleL.Text = title; }

    // ---- 命中(本地坐标, 由 shot-pick.cs 换算物理坐标) ----
    public const int HIT_NONE = 0, HIT_CLOSE = 1, HIT_COPY = 2, HIT_COPY_SRC = 3, HIT_SWAP = 4;
    public int HitButton(int lx, int ly)
    {
        if (lx >= CARD_W - 34 && lx <= CARD_W - 8 && ly >= 8 && ly <= 30) return HIT_CLOSE;
        if (ly >= BTN_Y && ly <= BTN_Y + BTN_H)
        {
            if (lx >= PAD && lx <= PAD + BTN_W) return HIT_SWAP;
            if (lx >= PAD + BTN_W + 6 && lx <= PAD + BTN_W * 2 + 6) return HIT_COPY;
            if (lx >= PAD + (BTN_W + 6) * 2 && lx <= PAD + BTN_W * 3 + 12) return HIT_COPY_SRC;
        }
        return HIT_NONE;
    }
    // 拖动区 = 标题栏(避开关闭按钮) + 原文行。正文留给选字/滚动。
    public bool HitDraggable(int lx, int ly)
    {
        if (ly < HEAD_H && !(lx >= CARD_W - 40)) return true;
        if (ly >= SRC_Y && ly <= SRC_Y + SRC_H) return true;
        return false;
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x00000080 | 0x08000000; return cp; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PickStyle.PaintPanel(this, e, CARD_W, CARD_H, 14);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(PickStyle.Border, 1f))
            g.DrawLine(pen, 1, HEAD_H, CARD_W - 1, HEAD_H);            // 标题栏分隔
        using (var br = new SolidBrush(PickStyle.InkDim))
            for (int i = 0; i < 3; i++)
                g.FillEllipse(br, PAD + 3 + i * 5, HEAD_H / 2 - 2, 3, 3);   // 拖动抓手
        DrawField(e, PAD, BODY_Y, CARD_W - PAD * 2, BODY_H);
        DrawField(e, PAD, SRC_Y, CARD_W - PAD * 2, SRC_H);
        using (var fnt = PickStyle.F(PickStyle.FS_TINY, FontStyle.Regular))
        using (var br = new SolidBrush(PickStyle.InkDim))
            g.DrawString("按住标题栏/原文行可拖动", fnt, br, CARD_W - 158, BTN_Y + 8);
    }
    void DrawField(PaintEventArgs e, int x, int y, int w, int h)
    {
        using (var pen = new Pen(PickStyle.Border, 1f))
        using (var path = PickStyle.Round(w - 1, h - 1, 8))
            e.Graphics.DrawPath(pen, path);
    }
}

// ==================== 问 AI 提问框 ====================
// 划词后点「问 AI」弹出: 允许本次现输入要问的话(每次可不同), 回车发送;
// 留空回车 = 按内置默认提示词直接解释划选内容; Esc / 点框外 = 取消。
// ★ 这是全套悬浮窗里【唯一允许抢焦点(正常 Show 激活)】的窗 —— 因为要让用户打字。
//   所以刻意不加 WS_EX_NOACTIVATE, 也不 ShowWithoutActivation。
sealed class PickAskForm : Form
{
    public const int ASK_W = 400, ASK_H = 96;
    public bool Confirmed;                       // 回车=true, Esc/点外面=false
    public string Result = "";                   // KeyDown 里先存好(FormClosed 时控件可能已释放)
    public string Question { get { return input == null ? Result : input.Text.Trim(); } }
    TextBox input;

    public PickAskForm(string srcPreview)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = PickStyle.Key;
        TransparencyKey = PickStyle.Key;
        Size = new Size(ASK_W, ASK_H);
        TopMost = true;
        DoubleBuffered = true;
        Font = PickStyle.F(PickStyle.FS_MID, FontStyle.Regular);

        Label hint = new Label();
        hint.Text = "问 AI：可补充这次要问的话（回车发送 · 留空=按默认 · Esc 取消）";
        hint.ForeColor = PickStyle.InkMid;
        hint.BackColor = Color.Transparent;
        hint.Font = PickStyle.F(PickStyle.FS_SMALL, FontStyle.Regular);
        hint.Location = new Point(12, 9);
        hint.Size = new Size(ASK_W - 24, 16);
        Controls.Add(hint);

        input = new TextBox();
        input.Multiline = false;
        input.BorderStyle = BorderStyle.None;
        input.BackColor = PickStyle.Field;
        input.ForeColor = PickStyle.InkHi;
        input.Font = PickStyle.F(PickStyle.FS_BODY, FontStyle.Regular);
        input.Location = new Point(12, 34);
        input.Size = new Size(ASK_W - 24, 22);
        input.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Result = input.Text.Trim(); Confirmed = true; e.SuppressKeyPress = true; Close(); }
            else if (e.KeyCode == Keys.Escape) { Result = ""; Confirmed = false; e.SuppressKeyPress = true; Close(); }
        };
        Controls.Add(input);

        string pv = srcPreview ?? "";
        if (pv.Length > 34) pv = pv.Substring(0, 34) + "…";
        Label eg = new Label();
        eg.Text = "划选内容：" + pv;
        eg.ForeColor = PickStyle.InkDim;
        eg.BackColor = Color.Transparent;
        eg.Font = PickStyle.F(PickStyle.FS_TINY, FontStyle.Regular);
        eg.Location = new Point(12, 66);
        eg.Size = new Size(ASK_W - 24, 14);
        Controls.Add(eg);

        Shown += delegate
        {
            try
            {
                Activate();
                input.Focus();
                // 激活成功后才"武装"失焦自关: 用户点别处 = 取消提问
                BeginInvoke(new MethodInvoker(delegate { _armed = true; }));
            }
            catch { }
        };
        Deactivate += delegate { if (_armed) { try { Close(); } catch { } } };
    }
    bool _armed;
    protected override CreateParams CreateParams
    {
        // 只加 TOOLWINDOW(不占任务栏) —— 不加 NOACTIVATE, 这个窗要拿焦点打字
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x00000080; return cp; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        PickStyle.PaintPanel(this, e, ASK_W, ASK_H, 12);
    }
}
