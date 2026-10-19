using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

partial class ShotService
{

// MiniSlider (老大 2026-10-18 三轮定样): 细横线轨道 + 小圆点钮, 替换笨重的 WinForms TrackBar。
    // 拖圆点 / 滚轮调值; ValueLabel 双击 → 就地弹小输入框 (回车生效) 解决精度问题。
    class MiniSlider : Control
    {
        public int Min, Max, Value;
        public string Suffix = "";    // "°" / "%"
        public event Action<int> ValueChanged;
        bool dragging;

        public MiniSlider(int min, int max, int cur, string suffix)
        {
            Min = min; Max = max; Value = Math.Max(min, Math.Min(max, cur)); Suffix = suffix;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            // 🔴 普通 Control 不支持 Transparent BackColor (ArgumentException 实锤崩溃) — 直接用面板底色
            BackColor = Color.FromArgb(40, 41, 46);
            Height = 22;
        }

        int TrackX0 { get { return 6; } }
        int TrackX1 { get { return Width - 6; } }
        float Frac() { return Max <= Min ? 0 : (Value - Min) / (float)(Max - Min); }

        int ValFromX(int x)
        {
            float fr = (x - TrackX0) / (float)(TrackX1 - TrackX0);
            if (fr < 0) fr = 0; if (fr > 1) fr = 1;
            return Min + (int)Math.Round(fr * (Max - Min));
        }

        public void SetValue(int v)
        {
            v = Math.Max(Min, Math.Min(Max, v));
            if (v != Value) { Value = v; Invalidate(); if (ValueChanged != null) ValueChanged(v); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { dragging = true; SetValue(ValFromX(e.X)); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetValue(ValFromX(e.X));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            SetValue(Value + (e.Delta > 0 ? 1 : -1));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cy = Height / 2;
            // 细轨道线 2px
            using (Pen p = new Pen(Color.FromArgb(86, 88, 96), 2f)) g.DrawLine(p, TrackX0, cy, TrackX1, cy);
            int fillX = TrackX0 + (int)Math.Round((TrackX1 - TrackX0) * Frac());
            using (Pen p = new Pen(Color.FromArgb(47, 156, 245), 2f)) g.DrawLine(p, TrackX0, cy, fillX, cy);
            // 小圆点钮
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, fillX - 5, cy - 5, 10, 10);
            using (Pen p = new Pen(Color.FromArgb(47, 156, 245), 1.6f)) g.DrawEllipse(p, fillX - 5, cy - 5, 10, 10);
        }
    }

// 水印属性面板 (PixPin WatermarkInputDialog/WatermarkLineEdit 复刻, 老大 2026-10-18 三轮定样):
    // 🔴 横向紧凑条两行, 吸附工具条正下方, 放不下翻上方, 绝不重叠。
    // 🔴 非模态 (Show 不是 ShowDialog): 之前 ShowDialog 把整个遮罩锁死 — 除了 Esc 什么都点不了 (老大定级最严重 bug)。
    // 滑条 = MiniSlider 细线+小圆点; 数值 Label 双击/单击 → 就地弹小输入框。
    // 行1: 文字输入(带×) / 平铺 / 字体 / 间距
    // 行2: 角度 / 不透明度 / 大小 / 颜色
    class WatermarkInput : Form
    {
        public string Result = null;
        readonly TextBox input;
        Button clrX;
        ComboBox tileCmb, fontCmb;
        MiniSlider angTrk, opaTrk, sizeTrk, spaceTrk;
        readonly Panel colorRow;
        static readonly Color[] WmColors = {
            Color.Red, Color.FromArgb(255, 200, 0), Color.FromArgb(0, 170, 90),
            Color.FromArgb(30, 110, 235), Color.FromArgb(130, 130, 130),
            Color.Black, Color.White };

        public WatermarkInput(string seed)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(40, 41, 46);
            KeyPreview = true;
            Size = new Size(760, 78); // 横向紧凑: 两行
            using (GraphicsPath gp = StylePopup.RoundPath(0, 0, Width, Height, 8)) Region = new Region(gp);

            // ---- 行1 (y=8): 文字输入 230 / 平铺 110 / 字体 170 / 间距 190 ----
            int y1 = 8;
            input = new TextBox();
            input.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            input.BackColor = Color.FromArgb(56, 57, 64);
            input.ForeColor = Color.White;
            input.Font = new Font("Microsoft YaHei UI", 9.5f);
            input.Text = seed == null ? "" : seed;
            input.SetBounds(10, y1, 230, 24);
            Controls.Add(input);
            input.TextChanged += delegate
            {
                CaptureOverlay.WmPreviewText(input.Text.Trim());
                if (clrX != null) clrX.Visible = input.Text.Length > 0;
            };

            clrX = new Button();
            clrX.Text = "×";
            clrX.FlatStyle = FlatStyle.Flat;
            clrX.FlatAppearance.BorderSize = 0;
            clrX.BackColor = Color.FromArgb(56, 57, 64);
            clrX.ForeColor = Color.FromArgb(180, 186, 194);
            clrX.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            clrX.SetBounds(10 + 230 - 22, y1 + 1, 20, 22);
            clrX.Click += delegate { input.Text = ""; input.Focus(); };
            Controls.Add(clrX);
            clrX.Visible = input.Text.Length > 0;

            tileCmb = MkCombo(250, y1, 110);
            tileCmb.Items.AddRange(new object[] { "单条", "平铺" });
            tileCmb.SelectedIndex = CaptureOverlay.wmTile ? 1 : 0;
            tileCmb.SelectedIndexChanged += delegate { CaptureOverlay.WmPreviewTile(tileCmb.SelectedIndex == 1); };

            fontCmb = MkCombo(368, y1, 170);
            foreach (FontFamily ff in FontFamily.Families) fontCmb.Items.Add(ff.Name);
            int fi = fontCmb.Items.IndexOf(CaptureOverlay.wmFont);
            if (fi < 0) { fontCmb.Items.Add(CaptureOverlay.wmFont); fi = fontCmb.Items.Count - 1; }
            fontCmb.SelectedIndex = fi;
            fontCmb.SelectedIndexChanged += delegate { if (fontCmb.SelectedItem != null) CaptureOverlay.WmPreviewFont(fontCmb.SelectedItem.ToString()); };

            spaceTrk = MkSlider(546, y1 - 2, "间距", 200, 0, 120, CaptureOverlay.wmSpace, delegate (int v) { CaptureOverlay.wmSpace = v; OverlayInvalidate(); });

            // ---- 行2 (y=44): 角度 / 不透明度 / 大小 / 颜色一排 ----
            int y2 = 44;
            angTrk = MkSlider(10, y2 - 2, "角度", 170, 0, 90, CaptureOverlay.wmAngle, delegate (int v) { CaptureOverlay.WmPreviewAngle(v); });
            opaTrk = MkSlider(190, y2 - 2, "不透明度", 160, 1, 100, CaptureOverlay.wmOpacity, delegate (int v) { CaptureOverlay.WmPreviewOpacity(v); });
            sizeTrk = MkSlider(360, y2 - 2, "大小", 100, 8, 72, CaptureOverlay.wmSize, delegate (int v) { CaptureOverlay.WmPreviewSize(v); });

            colorRow = new Panel();
            colorRow.SetBounds(470, y2 + 1, 280, 24);
            colorRow.BackColor = Color.Transparent;
            Controls.Add(colorRow);
            int cbW = 26, gap = 6, n = WmColors.Length;
            for (int i = 0; i <= n; i++)
            {
                Button b = new Button();
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = Color.FromArgb(120, 130, 140);
                b.SetBounds(i * (cbW + gap), 0, cbW, 22);
                if (i < n)
                {
                    b.BackColor = WmColors[i];
                    Color c = WmColors[i];
                    b.Click += delegate { CaptureOverlay.WmPreviewColor(c); };
                }
                else
                {
                    b.Text = "+";
                    b.ForeColor = Color.White;
                    b.BackColor = Color.FromArgb(56, 57, 64);
                    b.Click += delegate
                    {
                        using (ColorDialog cd = new ColorDialog())
                        {
                            cd.FullOpen = true;
                            cd.Color = CaptureOverlay.wmColor;
                            if (cd.ShowDialog(this) == DialogResult.OK) CaptureOverlay.WmPreviewColor(cd.Color);
                        }
                    };
                }
                colorRow.Controls.Add(b);
            }

            KeyDown += delegate (object o, KeyEventArgs ev)
            {
                if (ev.KeyCode == Keys.Escape) { Result = null; Close(); }
            };
        }

        ComboBox MkCombo(int x, int y, int w)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Color.FromArgb(56, 57, 64);
            c.ForeColor = Color.White;
            c.Font = new Font("Microsoft YaHei UI", 8.5f);
            c.SetBounds(x, y, w, 24);
            Controls.Add(c);
            return c;
        }

        // MiniSlider + label + 可双击编辑的数值; 总宽 w (label 40 + track + 数值 44)
        MiniSlider MkSlider(int x, int y, string name, int w, int min, int max, int cur, Action<int> onVal)
        {
            Label l = new Label();
            l.Text = name;
            l.ForeColor = Color.FromArgb(190, 196, 204);
            l.Font = new Font("Microsoft YaHei UI", 8.5f);
            l.SetBounds(x, y + 3, 40, 18);
            l.AutoSize = false;
            Controls.Add(l);
            MiniSlider s = new MiniSlider(min, max, cur, name == "角度" ? "°" : (name == "不透明度" ? "%" : ""));
            s.SetBounds(x + 42, y, w - 90, 22);
            Controls.Add(s);
            Label val = new Label();
            val.ForeColor = Color.FromArgb(150, 158, 168);
            val.Font = new Font("Consolas", 8.5f);
            val.SetBounds(x + w - 46, y + 3, 46, 18);
            val.Text = s.Value + s.Suffix;
            val.Cursor = Cursors.IBeam;
            Controls.Add(val);
            s.ValueChanged += delegate (int v)
            {
                val.Text = v + s.Suffix;
                if (onVal != null) onVal(v);
            };
            // 🔴 数值可双击/点击输入 (老大: "滑动很难控制精度"): 就地弹小输入框, 回车生效
            val.DoubleClick += delegate { EditValue(s, val, onVal); };
            val.Click += delegate { EditValue(s, val, onVal); };
            return s;
        }

        void EditValue(MiniSlider s, Label val, Action<int> onVal)
        {
            // 已有编辑框就不再弹
            foreach (Control c in Controls) if (c is TextBox && c.Tag == s) { c.Focus(); return; }
            TextBox ed = new TextBox();
            ed.Tag = s;
            ed.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            ed.BackColor = Color.FromArgb(56, 57, 64);
            ed.ForeColor = Color.White;
            ed.Font = new Font("Consolas", 9f, FontStyle.Bold);
            ed.TextAlign = HorizontalAlignment.Center;
            ed.Text = s.Value.ToString();
            ed.SetBounds(val.Left, val.Top - 2, 44, 22);
            Controls.Add(ed);
            ed.BringToFront();
            ed.KeyPress += delegate (object o, KeyPressEventArgs ev) { if (!char.IsControl(ev.KeyChar) && !char.IsDigit(ev.KeyChar)) ev.Handled = true; };
            Action commit = delegate
            {
                int v;
                if (int.TryParse(ed.Text.Trim(), out v))
                {
                    s.SetValue(v);
                    val.Text = s.Value + s.Suffix;
                    if (onVal != null) onVal(s.Value);
                }
                Controls.Remove(ed);
                ed.Dispose();
            };
            ed.KeyDown += delegate (object o, KeyEventArgs ev)
            {
                if (ev.KeyCode == Keys.Enter) { ev.SuppressKeyPress = true; commit(); }
                else if (ev.KeyCode == Keys.Escape) { ev.SuppressKeyPress = true; Controls.Remove(ed); ed.Dispose(); }
            };
            ed.LostFocus += delegate { try { commit(); } catch { } };
            ed.Focus();
            ed.SelectAll();
        }

        void OverlayInvalidate()
        {
            CaptureOverlay live = CaptureOverlay.live;
            if (live != null && !live.IsDisposed) live.Invalidate();
        }
    }

// 放大镜倍数填充式滚动条弹层 (老大 2026-10-18 定样: PixPin 同款「填充轨道+圆钮」,
    // 不要下拉框; 拖动圆钮或滚轮调倍数, 实时回调)。深色圆角小条。
    class MagSliderPopup : Form
    {
        readonly int min, max;
        int val;
        readonly Action<int> onPick;
        bool dragging;
        readonly int Pad = 6, KnobR = 8, TrackH = 8;

        public int Value { get { return val; } }

        public MagSliderPopup(int min, int max, int current, Action<int> onPick)
        {
            this.min = min; this.max = max; val = current; this.onPick = onPick;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(40, 41, 46);
            DoubleBuffered = true; KeyPreview = true;
            Size = new Size(150, 26);
            using (GraphicsPath gp = StylePopup.RoundPath(0, 0, Width, Height, 13)) Region = new Region(gp);
            Deactivate += delegate { try { Close(); } catch { } };
            KeyDown += delegate (object o, KeyEventArgs ev)
            {
                if (ev.KeyCode == Keys.Escape) { ev.SuppressKeyPress = true; Close(); }
                else if (ev.KeyCode == Keys.Left) { ev.SuppressKeyPress = true; Step(-1); }
                else if (ev.KeyCode == Keys.Right) { ev.SuppressKeyPress = true; Step(1); }
            };
        }

        void Step(int d)
        {
            int nv = Math.Max(min, Math.Min(max, val + d));
            if (nv != val) { val = nv; Invalidate(); if (onPick != null) onPick(val); }
        }

        int TrackX0 { get { return Pad + KnobR; } }
        int TrackX1 { get { return Width - Pad - KnobR; } }

        float Frac()
        {
            if (max <= min) return 0;
            return (val - min) / (float)(max - min);
        }

        int ValFromX(int x)
        {
            float fr = (x - TrackX0) / (float)(TrackX1 - TrackX0);
            if (fr < 0) fr = 0; if (fr > 1) fr = 1;
            return min + (int)Math.Round(fr * (max - min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { dragging = true; int nv = ValFromX(e.X); if (nv != val) { val = nv; Invalidate(); if (onPick != null) onPick(val); } }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { int nv = ValFromX(e.X); if (nv != val) { val = nv; Invalidate(); if (onPick != null) onPick(val); } }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Step(e.Delta > 0 ? 1 : -1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int ty = Height / 2 - TrackH / 2;
            using (GraphicsPath tp = StylePopup.RoundPath(TrackX0, ty, TrackX1 - TrackX0, TrackH, TrackH / 2))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(56, 57, 64)))
                g.FillPath(bg, tp);
            int fillW = (int)Math.Round((TrackX1 - TrackX0) * Frac());
            if (fillW > 0)
                using (GraphicsPath fp = StylePopup.RoundPath(TrackX0, ty, fillW, TrackH, TrackH / 2))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(47, 156, 245)))
                    g.FillPath(fb, fp);
            int kx = TrackX0 + (int)Math.Round((TrackX1 - TrackX0) * Frac());
            using (SolidBrush kb = new SolidBrush(Color.White))
                g.FillEllipse(kb, kx - KnobR / 2, Height / 2 - KnobR / 2, KnobR, KnobR);
        }
    }
}
