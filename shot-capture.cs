using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;

// 区域截图覆盖层 + 入口 (M1 截图 / M2 OCR+翻译 / M3 基础标注)
// 与 shot-service.cs 同属 ShotService 类(partial), 共享 Log/ShotDir/hkForm/TrayIcon 等成员
//
// 交互 = 照抄 PixPin/ShareX (ShareX RegionCaptureForm 同款手法):
//   1) 弹遮罩前先 CopyFromScreen 冻结全屏; 「原图+35%暗层」一次性预合成为背景图 (BackgroundImage),
//      拖动时系统只做 blit — 绝不在 OnPaint 里逐帧画全屏暗层 (那是上一版拖动巨卡的根因)
//   2) 拖框: 红框 + 框内露出原亮度; 只 Invalidate 新旧框脏区, 不全屏重绘
//   3) 松手: 遮罩保持! 选区保持高亮, 图标工具条贴着选区弹出 (PixPin 同款) — 选区绝不闪没
//   4) 工具条: 深色圆角 + 自绘图标 (标注组/撤销组/识别组/输出组), hover 高亮 + tooltip
//   5) 标注 (PixPin 同款): 矩形/椭圆/箭头/画笔/文字/序号, 画在选区上, 撤销回退, 保存/复制导出合成图
//   6) 点动作(保存/复制/OCR/翻译)完成后才关遮罩; Esc/右键/取消 = 放弃
//
// ⚠️ 历史坑 (都实测踩过):
//   - TransparencyKey=BackColor 做透明遮罩 → 整窗完全不可见+鼠标穿透+托盘假死。绝不再用
//   - OnPaint 逐帧 DrawImage 全屏 + alpha FillRectangle 全屏 → 拖动巨卡。背景必须预合成
//   - 松手立即关遮罩 → 选区视觉丢失 (PixPin 是遮罩常驻到动作完成)
//   - 灰色系统文字按钮排一排 → 用户: "工具条看着垃圾的很"。图标+深色圆角+hover 是底线
partial class ShotService
{
    const int SHOT_HOTKEY_ID = 0x5713; // 与 HOTKEY_ID(0x5712) 区分
    static string shotHotkeyName = "";
    static bool captureBusy = false;

    // 区域截图入口: 遮罩在 hk(STA) 线程显示, 与热键窗同消息循环。
    // 托盘线程必须走 BeginInvoke 立即返回 — 同步 Invoke 一旦遮罩卡住, 整个托盘假死 (实测踩坑)
    static void ShowCaptureOverlay()
    {
        if (captureBusy) { Log("capture: busy, ignore"); return; }
        Form hk = hkForm;
        if (hk == null || !hk.IsHandleCreated) { Log("capture: hotkey form not ready"); return; }
        captureBusy = true; // 提前置位防重入; 由遮罩 FormClosed 复位
        try
        {
            if (hk.InvokeRequired) hk.BeginInvoke(new MethodInvoker(ShowCaptureOverlayCore));
            else ShowCaptureOverlayCore();
        }
        catch (Exception ex) { captureBusy = false; Log("capture: dispatch err: " + ex.Message); }
    }

    static void ShowCaptureOverlayCore()
    {
        CaptureOverlay ov = null;
        try
        {
            Log("capture: overlay start");
            // 照 PixPin/ShareX: 遮罩显示前先冻结全屏(此时屏幕无任何遮罩物), 选区最终从冻结图裁剪
            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap pre = null;
            try
            {
                pre = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(pre))
                    g.CopyFromScreen(vs.X, vs.Y, 0, 0, vs.Size);
            }
            catch (Exception ex) { Log("capture: preshot err: " + ex.Message); }
            if (pre == null) { captureBusy = false; return; } // 截屏失败绝不弹黑罩锁屏

            ov = new CaptureOverlay(pre);
            ov.Show(hkForm); // 非模态: 遮罩自管生命周期(动作完成/取消时 Close), 关闭时复位 captureBusy
        }
        catch (Exception ex)
        {
            Log("capture overlay err: " + ex.Message);
            captureBusy = false;
            if (ov != null) { try { ov.Dispose(); } catch { } }
        }
    }

    static void ShowTrayInfo(string msg)
    {
        try { if (TrayIcon != null) TrayIcon.ShowBalloonTip(1500, "Win Desktop Helper", msg, ToolTipIcon.Info); }
        catch { }
    }

    // 保存截图到 ShotDir (类级: 遮罩工具栏共用)
    static string SaveToShotDir(Bitmap bmp)
    {
        try { if (!System.IO.Directory.Exists(ShotDir)) System.IO.Directory.CreateDirectory(ShotDir); } catch { }
        string name = "shot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".png";
        string path = System.IO.Path.Combine(ShotDir, name);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    // ---- 标注数据模型 (PixPin 基础款: 矩形/椭圆/箭头/画笔/文字/序号; 一条记录一个笔画) ----
    class Annot
    {
        public const int K_RECT = 0, K_ELLIPSE = 1, K_ARROW = 2, K_PEN = 3, K_TEXT = 4, K_SEQ = 5;
        public int Kind;
        public Rectangle Rect;      // rect/ellipse/arrow 的包围盒; seq/text 的定位点在 Rect.Location
        public List<Point> Pts;     // 画笔折线 (屏坐标)
        public string Text;         // 文字内容
        public int No;              // 序号数字
        public Color Color = Color.FromArgb(255, 70, 70); // PixPin 默认红
    }

    // 全屏框选窗 (PixPin/ShareX 同款交互)
    class CaptureOverlay : Form
    {
        readonly Bitmap frozen;  // 冻结的全屏原图 (尺寸=虚拟屏)
        Point start;
        Rectangle sel;
        bool dragging = false;
        bool hasSel = false;
        ToolStrip bar;           // 图标工具条 (松手后出现, 贴选区)
        readonly List<Annot> annots = new List<Annot>(); // 已提交标注 (屏坐标)
        Annot cur;               // 正在绘制的标注
        string tool = null;      // 当前标注工具: rect/ellipse/arrow/pen/text/seq; null=未选
        int seqNext = 1;
        TextBox textInput;       // 文字标注的行内输入框
        Point textPt;
        static readonly Font annotFont = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);

        public CaptureOverlay(Bitmap preShot)
        {
            frozen = preShot;
            // 一次性预合成暗化背景 (原亮度图 + 35% 暗层) — 拖动/显示期间系统只 blit 这张图
            Bitmap dim = new Bitmap(frozen.Width, frozen.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dim))
            {
                g.DrawImage(frozen, 0, 0, frozen.Width, frozen.Height);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                    g.FillRectangle(b, 0, 0, dim.Width, dim.Height);
            }
            BackgroundImage = dim;
            BackgroundImageLayout = ImageLayout.None;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen; // 覆盖全部显示器(含负坐标副屏)
            TopMost = true;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate(); // 拿焦点: Esc 才可达
            Log("capture: overlay shown bounds=" + Bounds);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { CancelAll(); return; }
            if (e.Button != MouseButtons.Left) return;
            Point sp = PointToScreen(e.Location);
            if (!hasSel)
            {
                // 框选阶段
                Rectangle old = sel;
                start = sp;
                sel = new Rectangle(start, Size.Empty);
                dragging = true;
                hasSel = false;
                HideBar();
                InvalidateSelArea(old);
                return;
            }
            if (tool != null && sel.Contains(sp))
            {
                // 标注阶段: 在选区内开始一笔
                CommitTextInput(); // 若有未提交文字先落字
                if (tool == "text")
                {
                    OpenTextInput(sp);
                    return;
                }
                cur = new Annot();
                cur.Kind = tool == "rect" ? Annot.K_RECT : tool == "ellipse" ? Annot.K_ELLIPSE :
                           tool == "arrow" ? Annot.K_ARROW : tool == "seq" ? Annot.K_SEQ : Annot.K_PEN;
                if (cur.Kind == Annot.K_SEQ)
                {
                    cur.No = seqNext++;
                    cur.Rect = new Rectangle(sp.X - 14, sp.Y - 14, 28, 28);
                    annots.Add(cur);
                    InvalidateAnnot(cur);
                    Log("capture: annot seq #" + cur.No);
                    cur = null; // 序号一笔即成
                }
                else if (cur.Kind == Annot.K_PEN)
                {
                    cur.Pts = new List<Point> { sp };
                }
                else cur.Rect = new Rectangle(sp, Size.Empty);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point sp = PointToScreen(e.Location);
            if (dragging)
            {
                Rectangle old = sel;
                sel = Normalize(start, sp);
                hasSel = sel.Width > 2 && sel.Height > 2;
                InvalidateSelArea(old);
            }
            else if (cur != null)
            {
                if (cur.Kind == Annot.K_PEN) { cur.Pts.Add(sp); InvalidateAnnot(cur); }
                else
                {
                    Rectangle old = cur.Rect;
                    cur.Rect = Normalize(cur.Rect.Location, sp);
                    InvalidateAnnot(old);
                    InvalidateAnnot(cur);
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Point sp = PointToScreen(e.Location);
            if (dragging)
            {
                dragging = false;
                Log("capture: mouseup sel=" + sel.Width + "x" + sel.Height + " at " + sel.Location);
                if (hasSel) ShowBar(); // 遮罩与选区保持, 工具条贴上来 (PixPin 同款)
                return;
            }
            if (cur != null)
            {
                if (cur.Kind == Annot.K_PEN) { if (cur.Pts.Count > 1) { annots.Add(cur); InvalidateAnnot(cur); } }
                else if (cur.Rect.Width > 2 && cur.Rect.Height > 2) { annots.Add(cur); InvalidateAnnot(cur); }
                cur = null;
            }
        }

        // 只重绘「旧框∪新框」外扩区域 — 全屏 blit 由 BackgroundImage 系统完成
        void InvalidateSelArea(Rectangle oldSel)
        {
            Rectangle u = Rectangle.Union(RectangleToClient(sel), RectangleToClient(oldSel));
            u.Inflate(70, 45); // 覆盖红框线宽 + 尺寸文字 + 露亮边缘
            Invalidate(u);
        }

        void InvalidateAnnot(Rectangle screenRect)
        {
            Rectangle c = RectangleToClient(screenRect);
            c.Inflate(24, 24);
            Invalidate(c);
        }

        void InvalidateAnnot(Annot a) { InvalidateAnnot(BoundsOf(a)); }

        static Rectangle BoundsOf(Annot a)
        {
            if (a.Kind == Annot.K_PEN)
            {
                Rectangle r = new Rectangle(a.Pts[0], Size.Empty);
                foreach (Point p in a.Pts) r = Rectangle.Union(r, new Rectangle(p, Size.Empty));
                return r;
            }
            if (a.Kind == Annot.K_TEXT)
            {
                Size ts = TextRenderer.MeasureText(a.Text, annotFont);
                return new Rectangle(a.Rect.Location, new Size(ts.Width + 8, ts.Height + 8));
            }
            return a.Rect;
        }

        void CancelAll() { Log("capture: cancelled (esc/right-click)"); Close(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hasSel)
            {
                Rectangle d = RectangleToClient(sel); // 客户区坐标 = 冻结图坐标 (窗体原点=虚拟屏原点)
                // 框内露出原亮度 (把暗层"挖亮": 局部重画冻结原图)
                g.SetClip(d);
                g.DrawImage(frozen, d, d, GraphicsUnit.Pixel);
                DrawAnnots(g, Point.Empty); // 选区内标注 (客户区即冻结图坐标, 偏移0)
                g.ResetClip();
                using (Pen p = new Pen(Color.Red, 2)) g.DrawRectangle(p, d);
                using (Font f = new Font("Consolas", 11))
                using (Brush b = new SolidBrush(Color.Yellow))
                using (Brush bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                {
                    string size = sel.Width + " x " + sel.Height;
                    SizeF ts = g.MeasureString(size, f);
                    float tx = Math.Min(d.Right + 4, ClientSize.Width - ts.Width - 4);
                    float ty = Math.Min(d.Bottom + 4, ClientSize.Height - ts.Height - 4);
                    g.FillRectangle(bg, tx - 2, ty - 1, ts.Width + 4, ts.Height + 2);
                    g.DrawString(size, f, b, tx, ty);
                }
            }
        }

        // 画标注 (屏坐标 -> 客户区偏移 offset; 导出时 offset = -sel.Location, 裁剪坐标系)
        void DrawAnnots(Graphics g, Point offset)
        {
            DrawOne(g, cur, offset); // 正在绘制的画在最上层
            for (int i = 0; i < annots.Count; i++) DrawOne(g, annots[i], offset);
        }

        static void DrawOne(Graphics g, Annot a, Point offset)
        {
            if (a == null) return;
            using (Pen p = new Pen(a.Color, 2.5f))
            {
                switch (a.Kind)
                {
                    case Annot.K_RECT:
                        g.DrawRectangle(p, a.Rect.X - offset.X, a.Rect.Y - offset.Y, a.Rect.Width, a.Rect.Height);
                        break;
                    case Annot.K_ELLIPSE:
                        g.DrawEllipse(p, a.Rect.X - offset.X, a.Rect.Y - offset.Y, a.Rect.Width, a.Rect.Height);
                        break;
                    case Annot.K_ARROW:
                        DrawArrow(g, p, a.Rect.X - offset.X, a.Rect.Y - offset.Y,
                                  a.Rect.Right - offset.X, a.Rect.Bottom - offset.Y);
                        break;
                    case Annot.K_PEN:
                        if (a.Pts != null && a.Pts.Count > 1)
                        {
                            Point[] pts = new Point[a.Pts.Count];
                            for (int i = 0; i < a.Pts.Count; i++) pts[i] = new Point(a.Pts[i].X - offset.X, a.Pts[i].Y - offset.Y);
                            g.DrawLines(p, pts);
                        }
                        break;
                    case Annot.K_TEXT:
                        using (Brush b = new SolidBrush(a.Color))
                            g.DrawString(a.Text, annotFont, b, a.Rect.X - offset.X, a.Rect.Y - offset.Y);
                        break;
                    case Annot.K_SEQ:
                        Rectangle r = a.Rect; r.Offset(-offset.X, -offset.Y);
                        using (SolidBrush b = new SolidBrush(a.Color)) g.FillEllipse(b, r);
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            using (Brush wb = new SolidBrush(Color.White))
                                g.DrawString(a.No.ToString(), annotFont, wb, (RectangleF)r, sf);
                        }
                        break;
                }
            }
        }

        static void DrawArrow(Graphics g, Pen p, float x1, float y1, float x2, float y2)
        {
            g.DrawLine(p, x1, y1, x2, y2);
            double ang = Math.Atan2(y2 - y1, x2 - x1);
            float hl = 14; // 箭头头长
            PointF a1 = new PointF(x2 - hl * (float)Math.Cos(ang - 0.45), y2 - hl * (float)Math.Sin(ang - 0.45));
            PointF a2 = new PointF(x2 - hl * (float)Math.Cos(ang + 0.45), y2 - hl * (float)Math.Sin(ang + 0.45));
            using (SolidBrush b = new SolidBrush(p.Color))
                g.FillPolygon(b, new PointF[] { new PointF(x2, y2), a1, a2 });
        }

        // ---- 导出: 冻结图选区 + 标注 合成 ----
        Bitmap CropTaken()
        {
            Rectangle d = sel;
            d.Offset(-SystemInformation.VirtualScreen.X, -SystemInformation.VirtualScreen.Y);
            Bitmap outBmp = frozen.Clone(d, frozen.PixelFormat);
            using (Graphics g = Graphics.FromImage(outBmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawAnnots(g, new Point(sel.X, sel.Y)); // 屏坐标 -> 选区局部坐标
            }
            return outBmp;
        }

        // ---- 行内文字输入 (PixPin 同款: 点击处直接打字, 回车落字 Esc 取消) ----
        void OpenTextInput(Point sp)
        {
            CommitTextInput();
            textPt = sp;
            textInput = new TextBox();
            textInput.Font = annotFont;
            textInput.ForeColor = Color.FromArgb(255, 70, 70);
            textInput.BackColor = Color.White;
            textInput.BorderStyle = BorderStyle.FixedSingle;
            Point c = RectangleToClient(new Rectangle(sp, Size.Empty)).Location;
            textInput.Location = c;
            textInput.Width = 220;
            textInput.TextChanged += (s, ev) => { textInput.Width = Math.Max(120, TextRenderer.MeasureText(textInput.Text + "宽", textInput.Font).Width); };
            textInput.KeyDown += (s, ev) =>
            {
                if (ev.KeyCode == Keys.Enter) { ev.SuppressKeyPress = true; CommitTextInput(); }
                else if (ev.KeyCode == Keys.Escape) { ev.SuppressKeyPress = true; DropTextInput(); }
            };
            Controls.Add(textInput);
            textInput.Show();
            textInput.Focus();
            Log("capture: text input opened at " + sp);
        }

        void CommitTextInput()
        {
            if (textInput == null) return;
            string t = textInput.Text;
            Point pt = textPt;
            DropTextInput();
            if (!string.IsNullOrEmpty(t))
            {
                Annot a = new Annot();
                a.Kind = Annot.K_TEXT;
                a.Text = t;
                a.Rect = new Rectangle(pt, Size.Empty);
                annots.Add(a);
                InvalidateAnnot(a);
                Log("capture: annot text (" + t.Length + " chars)");
            }
        }

        void DropTextInput()
        {
            if (textInput != null)
            {
                try { textInput.Dispose(); } catch { }
                textInput = null;
                Focus(); // 焦点回遮罩, Esc/快捷键继续可用
            }
        }

        // ---- 图标工具条 (照 PixPin: 深色圆角浮条 + 自绘图标 + hover 高亮 + tooltip + 分组分隔) ----
        void ShowBar()
        {
            if (bar != null) { PlaceBar(); bar.Visible = true; return; }
            bar = new ToolStrip();
            bar.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Dock = DockStyle.None; // ⚠️ ToolStrip 默认 Dock=Top, 会把工具条吸到屏幕顶 — 必须关掉才能自由贴选区
            bar.AutoSize = true;       // 自适应全部图标宽度 (AutoSize=false 且不设 Width 会把图标挤进溢出区)
            bar.CanOverflow = false;
            bar.BackColor = Color.FromArgb(30, 31, 36);
            bar.Renderer = new DarkToolRenderer();
            bar.ShowItemToolTips = true;
            bar.Padding = new Padding(6, 2, 6, 2);

            // [标注组] 矩形/椭圆/箭头/画笔/文字/序号 | [撤销] | [OCR 翻译] | [保存 复制 另存 取消]
            AddToolBtn(bar, "rect", "矩形", "矩形标注", true);
            AddToolBtn(bar, "ellipse", "椭圆", "椭圆标注", true);
            AddToolBtn(bar, "arrow", "箭头", "箭头标注", true);
            AddToolBtn(bar, "pen", "画笔", "自由画笔", true);
            AddToolBtn(bar, "text", "文字", "文字标注 (点击选区内位置输入, 回车落字)", true);
            AddToolBtn(bar, "seq", "序号", "序号标记 (自动递增)", true);
            bar.Items.Add(new ToolStripSeparator());
            AddActBtn(bar, "undo", "撤销", delegate { ActUndo(); });
            bar.Items.Add(new ToolStripSeparator());
            AddActBtn(bar, "ocr", "OCR 识别", delegate { ActOcr(); });
            AddActBtn(bar, "translate", "翻译", delegate { ActTranslate(); });
            bar.Items.Add(new ToolStripSeparator());
            AddActBtn(bar, "save", "保存", delegate { ActSave(); });
            AddActBtn(bar, "copy", "复制", delegate { ActCopy(); });
            AddActBtn(bar, "saveas", "另存", delegate { ActSaveAs(); });
            AddActBtn(bar, "cancel", "取消 (Esc)", delegate { CancelAll(); });

            Controls.Add(bar);
            PlaceBar();
            bar.Visible = true;
            Log("capture: toolbar shown (overlay kept, selection highlighted)");
        }

        ToolStripButton undoBtn;
        void AddActBtn(ToolStrip ts, string icon, string tip, EventHandler onClick)
        {
            ToolStripButton b = new ToolStripButton();
            b.Image = MakeIcon(icon);
            b.DisplayStyle = ToolStripItemDisplayStyle.Image;
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.ToolTipText = tip;
            b.Click += onClick;
            if (icon == "undo") undoBtn = b;
            ts.Items.Add(b);
        }

        void AddToolBtn(ToolStrip ts, string id, string label, string tip, bool toggle)
        {
            ToolStripButton b = new ToolStripButton();
            b.Image = MakeIcon(id == "rect" ? "rect" : id == "ellipse" ? "ellipse" : id);
            b.DisplayStyle = ToolStripItemDisplayStyle.Image;
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.ToolTipText = tip;
            b.Tag = id;
            b.Click += delegate
            {
                CommitTextInput();
                tool = (tool == id) ? null : id; // 再点一次取消工具
                foreach (ToolStripItem it in ts.Items)
                {
                    ToolStripButton bb = it as ToolStripButton;
                    if (bb != null && bb.Tag is string) bb.BackColor = ((string)bb.Tag == tool) ? Color.FromArgb(70, 110, 200) : Color.Transparent;
                }
                Cursor = tool == null ? Cursors.Cross : Cursors.Cross;
                Log("capture: tool=" + (tool ?? "(none)"));
            };
            ts.Items.Add(b);
        }

        void PlaceBar()
        {
            if (bar == null) return;
            Rectangle vs = SystemInformation.VirtualScreen;
            int w = bar.Width + 8;
            int x = sel.Right + 6, y = sel.Bottom + 6;
            if (x + w > vs.Right - 4) x = vs.Right - w - 4;
            if (y + bar.Height > vs.Bottom - 4) y = sel.Top - bar.Height - 6; // 下方放不下翻到选区上方
            if (y < vs.Top + 4) y = vs.Top + 4;
            Point c = RectangleToClient(new Rectangle(new Point(x, y), Size.Empty)).Location;
            bar.Left = c.X; bar.Top = c.Y;
        }

        void HideBar() { if (bar != null) bar.Visible = false; }

        void ActUndo()
        {
            CommitTextInput();
            if (annots.Count == 0) return;
            Annot a = annots[annots.Count - 1];
            annots.RemoveAt(annots.Count - 1);
            if (a.Kind == Annot.K_SEQ) seqNext = Math.Max(1, seqNext - 1);
            InvalidateAnnot(a);
            InvalidateSelArea(sel);
            Log("capture: undo (" + annots.Count + " left)");
        }

        void ActSave()
        {
            try
            {
                CommitTextInput();
                using (Bitmap bmp = CropTaken())
                {
                    string path = SaveToShotDir(bmp);
                    try { Clipboard.SetImage(bmp); } catch { }
                    Log("capture saved: " + path + " (image copied to clipboard)");
                    ShowTrayInfo("已截图: " + path);
                }
                Close();
            }
            catch (Exception ex) { Log("capture save err: " + ex.Message); ShowTrayInfo("保存失败: " + ex.Message); }
        }

        void ActCopy()
        {
            try
            {
                CommitTextInput();
                using (Bitmap bmp = CropTaken()) { try { Clipboard.SetImage(bmp); } catch { } }
                Log("capture copied to clipboard");
                ShowTrayInfo("已复制图片到剪贴板");
            }
            catch (Exception ex) { Log("capture copy err: " + ex.Message); ShowTrayInfo("复制失败: " + ex.Message); }
            Close();
        }

        void ActSaveAs()
        {
            try
            {
                CommitTextInput();
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|位图|*.bmp";
                    dlg.FileName = "shot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png";
                    dlg.DefaultExt = "png";
                    dlg.Title = "另存为截图";
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        ImageFormat fmt = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Jpeg :
                                          dlg.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Bmp : ImageFormat.Png;
                        using (Bitmap bmp = CropTaken()) bmp.Save(dlg.FileName, fmt);
                        Log("capture saved as: " + dlg.FileName);
                        ShowTrayInfo("已保存: " + dlg.FileName);
                        Close();
                    }
                }
            }
            catch (Exception ex) { Log("capture saveas err: " + ex.Message); ShowTrayInfo("另存失败: " + ex.Message); }
        }

        // OCR: 遮罩与选区保持, 完成后关遮罩弹结果窗; 失败气泡报错可重试
        ToolStripButton ocrBtn;
        void ActOcr()
        {
            if (ocrBtn == null) ocrBtn = FindBtn("OCR 识别");
            CommitTextInput();
            SetBusy("OCR 识别中...");
            Bitmap bmp = CropTaken();
            Task.Run(async () =>
            {
                string text = null, err = null;
                try { text = await OcrProvider().RecognizeAsync(bmp); }
                catch (Exception ex) { err = ex.Message; }
                finally { try { bmp.Dispose(); } catch { } }
                BeginOnUi(() =>
                {
                    if (err != null)
                    {
                        Log("ocr err: " + err);
                        SetBusy(null);
                        ShowTrayInfo("OCR 失败: " + err);
                        return; // 遮罩还在, 可直接重试
                    }
                    Log("capture: ocr ok (" + (text == null ? 0 : text.Length) + " chars)");
                    bool empty = string.IsNullOrEmpty(text);
                    Close();
                    if (!empty) { try { Clipboard.SetText(text); } catch { } ShowResult(text, "OCR 识别结果"); }
                    else ShowTrayInfo("未识别到文字");
                });
            });
        }

        ToolStripButton trBtn;
        void ActTranslate()
        {
            if (trBtn == null) trBtn = FindBtn("翻译");
            CommitTextInput();
            SetBusy("识别+翻译中...");
            Bitmap bmp = CropTaken();
            string toLang = Cfg("translate.to", "zh");
            Task.Run(async () =>
            {
                string tr = null, err = null;
                try
                {
                    string text = await OcrProvider().RecognizeAsync(bmp);
                    if (string.IsNullOrEmpty(text)) err = "未识别到文字, 无法翻译";
                    else tr = await TranslateProvider().TranslateAsync(text, toLang);
                }
                catch (Exception ex) { err = ex.Message; }
                finally { try { bmp.Dispose(); } catch { } }
                BeginOnUi(() =>
                {
                    if (err != null)
                    {
                        Log("translate err: " + err);
                        SetBusy(null);
                        ShowTrayInfo("翻译失败: " + err);
                        return;
                    }
                    Log("capture: translate ok");
                    bool empty = string.IsNullOrEmpty(tr);
                    Close();
                    if (!empty) { try { Clipboard.SetText(tr); } catch { } ShowResult(tr, "翻译结果"); }
                    else ShowTrayInfo("翻译失败");
                });
            });
        }

        ToolStripButton FindBtn(string tip)
        {
            foreach (ToolStripItem it in bar.Items)
            {
                ToolStripButton b = it as ToolStripButton;
                if (b != null && b.ToolTipText == tip) return b;
            }
            return null;
        }

        void SetBusy(string msg)
        {
            foreach (ToolStripItem it in bar.Items)
            {
                ToolStripButton b = it as ToolStripButton;
                if (b == null) continue;
                if (msg != null) { b.Enabled = false; if (b.ToolTipText == "OCR 识别") b.ToolTipText = msg; }
                else { b.Enabled = true; if (b.ToolTipText != null && b.ToolTipText.EndsWith("中...")) b.ToolTipText = "OCR 识别"; }
            }
        }

        // 回 hk UI 线程 (遮罩所在线程), BeginInvoke 不等待 — 任何情况不卡后台任务
        void BeginOnUi(Action a)
        {
            try { BeginInvoke(new MethodInvoker(delegate { try { a(); } catch (Exception ex) { Log("capture ui err: " + ex.Message); } })); }
            catch (Exception ex) { Log("capture beginui err: " + ex.Message); }
        }

        void ShowResult(string text, string title)
        {
            try { using (var rf = new ResultForm(title, text)) rf.ShowDialog(hkForm); }
            catch (Exception ex) { Log("result form err: " + ex.Message); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (textInput != null) { DropTextInput(); return true; }
                if (cur != null) { var c = cur; cur = null; InvalidateAnnot(c); return true; } // 丢弃当前笔画
                CancelAll();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Z)) { ActUndo(); return true; } // Ctrl+Z 撤销
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Log("capture: overlay closed (" + annots.Count + " annots)");
            captureBusy = false; // 遮罩退出(动作完成或取消), 允许下一次截图
            if (BackgroundImage != null) { try { BackgroundImage.Dispose(); } catch { } BackgroundImage = null; }
            if (frozen != null) { try { frozen.Dispose(); } catch { } }
        }

        static Rectangle Normalize(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
    }

    // 深色工具条渲染: 深底 + hover 蓝灰高亮 + 深色分隔线
    class DarkToolRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolRenderer() : base(new DarkToolColors()) { RoundedEdges = false; }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Color.FromArgb(55, 57, 66)))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }
    class DarkToolColors : ProfessionalColorTable
    {
        Color hover = Color.FromArgb(60, 65, 80), press = Color.FromArgb(70, 110, 200), sep = Color.FromArgb(55, 57, 66);
        public override Color ImageMarginGradientBegin { get { return Color.Transparent; } }
        public override Color ImageMarginGradientMiddle { get { return Color.Transparent; } }
        public override Color ImageMarginGradientEnd { get { return Color.Transparent; } }
        public override Color ButtonSelectedGradientBegin { get { return hover; } }
        public override Color ButtonSelectedGradientMiddle { get { return hover; } }
        public override Color ButtonSelectedGradientEnd { get { return hover; } }
        public override Color ButtonPressedGradientBegin { get { return press; } }
        public override Color ButtonPressedGradientMiddle { get { return press; } }
        public override Color ButtonPressedGradientEnd { get { return press; } }
        public override Color ButtonSelectedHighlight { get { return hover; } }
        public override Color ButtonSelectedBorder { get { return Color.Transparent; } }
        public override Color SeparatorDark { get { return sep; } }
        public override Color SeparatorLight { get { return sep; } }
    }

    // ---- 自绘 16x16 图标 (白色线条, 透明底; GDI+ 矢量画, 零图片依赖) ----
    static Bitmap MakeIcon(string kind)
    {
        Bitmap bmp = new Bitmap(18, 18);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen w = new Pen(Color.FromArgb(230, 232, 238), 1.6f))
            {
                w.StartCap = LineCap.Round; w.EndCap = LineCap.Round; w.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case "rect": // 空心矩形
                        g.DrawRectangle(w, 2.5f, 4.5f, 13, 9);
                        break;
                    case "ellipse": // 空心椭圆
                        g.DrawEllipse(w, 2.5f, 4.5f, 13, 9);
                        break;
                    case "arrow": // 斜箭头
                        g.DrawLine(w, 3, 15, 14, 4);
                        g.DrawLine(w, 14, 4, 9.5f, 5);
                        g.DrawLine(w, 14, 4, 13, 8.5f);
                        break;
                    case "pen": // 画笔(斜杆+笔尖)
                        g.DrawLine(w, 4, 14, 12, 6);
                        g.DrawLine(w, 12, 6, 14.5f, 3.5f);
                        g.DrawLine(w, 4, 14, 3, 15);
                        break;
                    case "text": // T
                        g.DrawLine(w, 4, 4, 14, 4);
                        g.DrawLine(w, 9, 4, 9, 15);
                        break;
                    case "seq": // 圆圈+1
                        g.DrawEllipse(w, 3, 3, 12, 12);
                        using (Font f = new Font("Consolas", 7.5f, FontStyle.Bold))
                        using (Brush b = new SolidBrush(Color.FromArgb(230, 232, 238)))
                            g.DrawString("1", f, b, 6.2f, 4.6f);
                        break;
                    case "undo": // 左弧箭头
                        g.DrawArc(w, 4, 4, 11, 10, -30, 220);
                        g.DrawLine(w, 4.5f, 7.5f, 3.5f, 3.5f);
                        g.DrawLine(w, 4.5f, 7.5f, 8.5f, 7);
                        break;
                    case "ocr": // 扫描框 + T
                        g.DrawLine(w, 2, 5, 2, 2); g.DrawLine(w, 2, 2, 5, 2);
                        g.DrawLine(w, 16, 5, 16, 2); g.DrawLine(w, 16, 2, 13, 2);
                        g.DrawLine(w, 2, 13, 2, 16); g.DrawLine(w, 2, 16, 5, 16);
                        g.DrawLine(w, 16, 13, 16, 16); g.DrawLine(w, 16, 16, 13, 16);
                        g.DrawLine(w, 6, 6, 12, 6);
                        g.DrawLine(w, 9, 6, 9, 13);
                        break;
                    case "translate": // A/文 双字
                        using (Font f = new Font("Consolas", 7f, FontStyle.Bold))
                        using (Brush b = new SolidBrush(Color.FromArgb(230, 232, 238)))
                        {
                            g.DrawString("A", f, b, 1.5f, 1.5f);
                            using (Font f2 = new Font("Microsoft YaHei UI", 7f, FontStyle.Bold))
                                g.DrawString("文", f2, b, 7.5f, 7.5f);
                        }
                        break;
                    case "save": // 软盘
                        g.DrawRectangle(w, 2.5f, 2.5f, 13, 13);
                        g.DrawRectangle(w, 6, 3.5f, 6, 4);
                        g.DrawLine(w, 5, 15, 5, 10); g.DrawLine(w, 5, 10, 13, 10); g.DrawLine(w, 13, 10, 13, 15);
                        break;
                    case "copy": // 双矩形
                        g.DrawRectangle(w, 5.5f, 5.5f, 10, 10);
                        g.DrawLines(w, new PointF[] { new PointF(12.5f, 3), new PointF(3, 3), new PointF(3, 12.5f) });
                        break;
                    case "saveas": // 托盘+下箭头
                        g.DrawLines(w, new PointF[] { new PointF(2, 11), new PointF(2, 15), new PointF(16, 15), new PointF(16, 11) });
                        g.DrawLine(w, 9, 2, 9, 10);
                        g.DrawLine(w, 9, 10, 6, 7);
                        g.DrawLine(w, 9, 10, 12, 7);
                        break;
                    case "cancel": // X
                        g.DrawLine(w, 4, 4, 14, 14);
                        g.DrawLine(w, 14, 4, 4, 14);
                        break;
                }
            }
        }
        return bmp;
    }

    // M2: OCR/翻译结果展示窗 (只读 + 复制按钮)
    class ResultForm : Form
    {
        public ResultForm(string title, string text)
        {
            Text = title; Width = 520; Height = 340; StartPosition = FormStartPosition.CenterScreen; TopMost = true;
            var tb = new TextBox() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Text = text, Font = new Font("Consolas", 11) };
            var btn = new Button() { Text = "复制", Dock = DockStyle.Bottom, Height = 34 };
            btn.Click += (s, ev) => { try { Clipboard.SetText(text); MessageBox.Show("已复制", "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { } };
            Controls.Add(tb); Controls.Add(btn);
        }
    }
}
