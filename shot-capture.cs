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
        readonly List<Annot> annots = new List<Annot>(); // 已提交标注 (屏坐标)
        readonly List<Annot> redoStack = new List<Annot>(); // 撤销弹出的标注 (重做用, 新笔画清空)
        Annot cur;               // 正在绘制的标注
        string tool = null;      // 当前标注工具: rect/ellipse/arrow/pen/text/seq; null=未选
        int seqNext = 1;
        TextBox textInput;       // 文字标注的行内输入框
        Point textPt;
        static readonly Font annotFont = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);

        public CaptureOverlay(Bitmap preShot)
        {
            frozen = preShot;
            // 性能关键 (PixPin 同款"反向遮罩"): 背景直接用冻结原图 (框内=原亮度透出),
            // 暗层在 OnPaint 里画成「选区外的 4 块纯色矩形」— 每帧零大图 DrawImage, 只填纯色。
            // 旧做法(预合成暗化图 blit + 选区亮块 DrawImage)每帧两次大图采样, 实测拖动巨卡
            BackgroundImage = frozen;
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
                    PushAnnot(cur);
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
                if (cur.Kind == Annot.K_PEN) { if (cur.Pts.Count > 1) PushAnnot(cur); }
                else if (cur.Rect.Width > 2 && cur.Rect.Height > 2) PushAnnot(cur);
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
            if (!hasSel) { using (SolidBrush dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(dim, e.ClipRectangle); return; }
            Rectangle d = RectangleToClient(sel); // 客户区坐标 = 冻结图坐标 (窗体原点=虚拟屏原点)
            // 反向遮罩: 只填「选区外的 4 块纯色矩形」(纯色 alpha 填充, 无大图采样, 微秒级) — 框内原亮度由背景图透出
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            {
                int W = ClientSize.Width, H = ClientSize.Height;
                g.FillRectangle(dim, 0, 0, W, Math.Max(0, d.Top));                              // 上
                g.FillRectangle(dim, 0, d.Bottom, W, Math.Max(0, H - d.Bottom));                // 下
                g.FillRectangle(dim, 0, d.Top, Math.Max(0, d.Left), d.Height);                  // 左
                g.FillRectangle(dim, d.Right, d.Top, Math.Max(0, W - d.Right), d.Height);       // 右
            }
            DrawAnnots(g, Point.Empty); // 选区内标注 (客户区即冻结图坐标, 偏移0)
            using (Pen p = new Pen(Color.Red, 2)) g.DrawRectangle(p, d);
            using (Font f = new Font("Consolas", 11))
            using (Brush b = new SolidBrush(Color.Yellow))
            using (Brush bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
            {
                string size = sel.Width + " x " + sel.Height;
                SizeF ts = g.MeasureString(size, f);
                float tx = d.X + d.Width / 2f - ts.Width / 2f; // 选区上方居中 (PixPin 同款)
                float ty = d.Top - ts.Height - 6;
                if (ty < 2) { ty = d.Bottom + 4; } // 出顶界翻到下方
                if (tx < 2) tx = 2;
                if (tx + ts.Width > ClientSize.Width - 2) tx = ClientSize.Width - ts.Width - 2;
                g.FillRectangle(bg, tx - 3, ty - 1, ts.Width + 6, ts.Height + 2);
                g.DrawString(size, f, b, tx, ty);
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
                PushAnnot(a);
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

        // ---- 自绘紧凑工具条 (PixPin 同款密度: 32px 按钮 0 间距 / hover 圆角 / 细分隔线; ToolStrip 间距不可控已弃用) ----
        ToolbarPanel bar;
        void ShowBar()
        {
            if (bar != null) { PlaceBar(); bar.Visible = true; return; }
            bar = new ToolbarPanel();
            // [标注组]
            bar.Add("rect", "矩形标注", delegate { PickTool("rect"); }, true).ToolKey = "rect";
            bar.Add("ellipse", "椭圆标注", delegate { PickTool("ellipse"); }, true).ToolKey = "ellipse";
            bar.Add("arrow", "箭头标注", delegate { PickTool("arrow"); }, true).ToolKey = "arrow";
            bar.Add("pen", "自由画笔", delegate { PickTool("pen"); }, true).ToolKey = "pen";
            bar.Add("text", "文字标注 (点击选区内输入, 回车落字)", delegate { PickTool("text"); }, true).ToolKey = "text";
            bar.Add("seq", "序号标记 (自动递增)", delegate { PickTool("seq"); }, true).ToolKey = "seq";
            bar.AddSep();
            // [编辑组]
            bar.Add("undo", "撤销 (Ctrl+Z)", delegate { ActUndo(); });
            bar.Add("redo", "重做 (Ctrl+Y)", delegate { ActRedo(); });
            bar.AddSep();
            // [功能组]
            bar.Add("ocr", "OCR 文字识别", delegate { ActOcr(); });
            bar.Add("translate", "翻译", delegate { ActTranslate(); });
            bar.Add("pin", "贴图 (钉到桌面)", delegate { ActPin(); });
            bar.Add("save", "保存到截图目录", delegate { ActSave(); });
            bar.AddSep();
            // [输出组]
            bar.Add("ok", "复制并完成", delegate { ActCopy(); });
            bar.Add("cancel", "取消 (Esc)", delegate { CancelAll(); });

            Controls.Add(bar);
            PlaceBar();
            bar.Visible = true;
            Log("capture: toolbar shown (overlay kept, selection highlighted)");
        }

        // 标注工具选中态 (再点同工具=取消; 高亮保持, PixPin 同款)
        void PickTool(string id)
        {
            CommitTextInput();
            tool = (tool == id) ? null : id;
            foreach (var b in bar.Btns)
                if (b.IsToggle) b.On = (b.ToolKey == tool);
            bar.Invalidate();
            Log("capture: tool=" + (tool ?? "(none)"));
        }

        void PlaceBar()
        {
            if (bar == null) return;
            Rectangle vs = SystemInformation.VirtualScreen;
            int x = sel.Right + 8, y = sel.Bottom + 8;
            if (x + bar.Width > vs.Right - 4) x = vs.Right - bar.Width - 4;
            if (y + bar.Height > vs.Bottom - 4) y = sel.Top - bar.Height - 8; // 下方放不下翻到选区上方
            if (y < vs.Top + 4) y = vs.Top + 4;
            Point c = RectangleToClient(new Rectangle(new Point(x, y), Size.Empty)).Location;
            bar.Left = c.X; bar.Top = c.Y;
        }

        void HideBar() { if (bar != null) bar.Visible = false; }

        // 提交一笔标注: 入栈 + 清空重做栈(新笔画使重做失效, 标准行为)
        void PushAnnot(Annot a)
        {
            annots.Add(a);
            redoStack.Clear();
            InvalidateAnnot(a);
        }

        void ActUndo()
        {
            CommitTextInput();
            if (annots.Count == 0) return;
            Annot a = annots[annots.Count - 1];
            annots.RemoveAt(annots.Count - 1);
            redoStack.Add(a);
            if (a.Kind == Annot.K_SEQ) seqNext = Math.Max(1, seqNext - 1);
            InvalidateAnnot(a);
            Log("capture: undo (" + annots.Count + " left, " + redoStack.Count + " redoable)");
        }

        void ActRedo()
        {
            if (redoStack.Count == 0) return;
            Annot a = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            annots.Add(a);
            if (a.Kind == Annot.K_SEQ) seqNext = Math.Max(seqNext, a.No + 1);
            InvalidateAnnot(a);
            Log("capture: redo (" + annots.Count + " annots)");
        }

        // 贴图 (PixPin 同款): 选区合成图钉到桌面原位置, 可拖动/滚轮缩放/双击关闭
        void ActPin()
        {
            CommitTextInput();
            Bitmap bmp = CropTaken();
            PinForm pf = new PinForm(bmp, sel);
            pf.Show();
            Log("capture: pinned " + sel.Width + "x" + sel.Height + " to screen");
            Close();
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


        // OCR: 遮罩与选区保持, 完成后关遮罩弹结果窗; 失败气泡报错可重试
        void ActOcr()
        {
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

        void ActTranslate()
        {
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

        // 忙碌: 禁用全部按钮; 空参=恢复
        void SetBusy(string msg)
        {
            if (bar == null) return;
            bar.SetEnabledAll(msg == null);
            bar.Invalidate();
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
            if (keyData == (Keys.Control | Keys.Y)) { ActRedo(); return true; } // Ctrl+Y 重做
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


    // ---- 自绘 18x18 图标 (白色线性紧凑版, 密度对齐 PixPin; GDI+ 矢量画, 零图片依赖) ----
    static Bitmap MakeIcon(string kind)
    {
        Bitmap bmp = new Bitmap(18, 18);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen w = new Pen(Color.FromArgb(232, 234, 240), 1.8f))
            {
                w.StartCap = LineCap.Round; w.EndCap = LineCap.Round; w.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case "rect": g.DrawRectangle(w, 2.5f, 4f, 13, 10); break;
                    case "ellipse": g.DrawEllipse(w, 2.5f, 4f, 13, 10); break;
                    case "arrow":
                        g.DrawLine(w, 3, 15, 15, 3);
                        g.DrawLine(w, 15, 3, 9.5f, 4);
                        g.DrawLine(w, 15, 3, 14, 8.5f);
                        break;
                    case "pen":
                        g.DrawLine(w, 4, 14, 12, 6);
                        g.DrawLine(w, 12, 6, 15, 3);
                        g.DrawLine(w, 4, 14, 3, 15);
                        break;
                    case "text":
                        g.DrawLine(w, 4, 4, 14, 4);
                        g.DrawLine(w, 9, 4, 9, 15);
                        break;
                    case "seq":
                        g.DrawEllipse(w, 3, 3, 12, 12);
                        using (Font f = new Font("Consolas", 7.5f, FontStyle.Bold))
                        using (Brush b = new SolidBrush(Color.FromArgb(232, 234, 240)))
                            g.DrawString("1", f, b, 6.3f, 4.5f);
                        break;
                    case "undo": // ↩ 顶弧 + 左指实心箭头
                        g.DrawArc(w, 4f, 6f, 11f, 9f, -20, 195);
                        using (SolidBrush b = new SolidBrush(w.Color))
                            g.FillPolygon(b, new PointF[] { new PointF(1.5f, 8.5f), new PointF(8f, 5.8f), new PointF(7.2f, 11.8f) });
                        break;
                    case "redo": // ↪ 镜像
                        g.DrawArc(w, 3f, 6f, 11f, 9f, 5, 195);
                        using (SolidBrush b = new SolidBrush(w.Color))
                            g.FillPolygon(b, new PointF[] { new PointF(16.5f, 8.5f), new PointF(10f, 5.8f), new PointF(10.8f, 11.8f) });
                        break;
                    case "pin": // 图钉: 斜针 + 圆头
                        g.DrawLine(w, 6.5f, 15.5f, 11, 9);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(232, 234, 240))) g.FillEllipse(b, 9, 2.5f, 6.5f, 6.5f);
                        g.DrawEllipse(w, 9, 2.5f, 6.5f, 6.5f);
                        break;
                    case "ocr": // 扫描框 + T
                        g.DrawLine(w, 1.5f, 5.5f, 1.5f, 1.5f); g.DrawLine(w, 1.5f, 1.5f, 5.5f, 1.5f);
                        g.DrawLine(w, 16.5f, 5.5f, 16.5f, 1.5f); g.DrawLine(w, 16.5f, 1.5f, 12.5f, 1.5f);
                        g.DrawLine(w, 1.5f, 12.5f, 1.5f, 16.5f); g.DrawLine(w, 1.5f, 16.5f, 5.5f, 16.5f);
                        g.DrawLine(w, 16.5f, 12.5f, 16.5f, 16.5f); g.DrawLine(w, 16.5f, 16.5f, 12.5f, 16.5f);
                        g.DrawLine(w, 6.5f, 6, 11.5f, 6);
                        g.DrawLine(w, 9, 6, 9, 13);
                        break;
                    case "translate":
                        using (Font f = new Font("Consolas", 7.5f, FontStyle.Bold))
                        using (Brush b = new SolidBrush(Color.FromArgb(232, 234, 240)))
                        {
                            g.DrawString("A", f, b, 1.5f, 1.5f);
                            using (Font f2 = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Bold))
                                g.DrawString("文", f2, b, 8f, 8f);
                        }
                        break;
                    case "save": // 软盘
                        g.DrawRectangle(w, 2.5f, 2.5f, 13, 13);
                        g.DrawRectangle(w, 6, 3.5f, 6, 4);
                        g.DrawLine(w, 5, 15.5f, 5, 10.5f); g.DrawLine(w, 5, 10.5f, 13, 10.5f); g.DrawLine(w, 13, 10.5f, 13, 15.5f);
                        break;
                    case "ok": // ✓
                        g.DrawLines(w, new PointF[] { new PointF(3, 10), new PointF(7, 14), new PointF(15, 4) });
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

    // 自绘紧凑工具条按钮条 (PixPin 同款密度): 32px 按钮 0 间距 / hover 圆角高亮 / 细分隔线 / tooltip。
    // 弃用 ToolStrip (默认间距巨大, 排出来稀稀拉拉)
    class ToolbarPanel : Panel
    {
        public class Btn
        {
            public string Icon, Tip, ToolKey;
            public Action OnClick;
            public bool IsToggle, On, Enabled = true;
            public Rectangle Rect;
        }

        public readonly List<Btn> Btns = new List<Btn>();
        int hoverIdx = -1;
        readonly ToolTip tip = new ToolTip();
        string shownTip;

        public ToolbarPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(26, 27, 31);
        }

        public Btn Add(string icon, string tipText, Action onClick, bool toggle = false)
        {
            Btn b = new Btn();
            b.Icon = icon; b.Tip = tipText; b.OnClick = onClick; b.IsToggle = toggle;
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        public void AddSep()
        {
            Btns.Add(new Btn { Icon = "|" });
            Relayout(); Invalidate();
        }

        void Relayout()
        {
            int x = 5;
            foreach (Btn b in Btns)
            {
                if (b.Icon == "|") { b.Rect = new Rectangle(x, 10, 1, 20); x += 11; }
                else { b.Rect = new Rectangle(x, 4, 32, 32); x += 32; }
            }
            Width = x + 5; Height = 40;
        }

        public void SetEnabledAll(bool en)
        {
            foreach (Btn b in Btns) b.Enabled = en;
        }

        int Hit(Point p)
        {
            for (int i = 0; i < Btns.Count; i++)
            {
                Btn b = Btns[i];
                if (b.Icon != "|" && b.Rect.Contains(p)) return i;
            }
            return -1;
        }

        static void RoundFill(Graphics g, Brush br, Rectangle r, int rad)
        {
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
                gp.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
                gp.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
                gp.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
                gp.CloseFigure();
                g.FillPath(br, gp);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen sp = new Pen(Color.FromArgb(62, 64, 72)))
                foreach (Btn b in Btns)
                    if (b.Icon == "|")
                        g.DrawLine(sp, b.Rect.X, b.Rect.Top, b.Rect.X, b.Rect.Bottom);
            for (int i = 0; i < Btns.Count; i++)
            {
                Btn b = Btns[i];
                if (b.Icon == "|") continue;
                if (i == hoverIdx || b.On)
                {
                    using (SolidBrush br = new SolidBrush(b.On ? DarkUI.Accent : (i == hoverIdx ? Color.FromArgb(64, 68, 80) : Color.Transparent)))
                        RoundFill(g, br, b.Rect, 6);
                }
                using (Bitmap ic = MakeIcon(b.Icon))
                {
                    if (!b.Enabled)
                    {
                        System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f };
                        using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            ia.SetColorMatrix(cm);
                            g.DrawImage(ic, new Rectangle(b.Rect.X + 7, b.Rect.Y + 7, 18, 18), 0, 0, 18, 18, GraphicsUnit.Pixel, ia);
                        }
                    }
                    else
                        g.DrawImage(ic, b.Rect.X + 7, b.Rect.Y + 7, 18, 18);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            if (h != hoverIdx)
            {
                hoverIdx = h;
                Invalidate();
                if (h >= 0 && Btns[h].Enabled)
                {
                    shownTip = Btns[h].Tip;
                    tip.Show(shownTip, this, Btns[h].Rect.X, Bottom - 4, 1400);
                }
                else { shownTip = null; tip.Hide(this); }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIdx = -1; shownTip = null;
            tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { Focus(); hoverIdx = Hit(e.Location); Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = Hit(e.Location);
            if (e.Button == MouseButtons.Left && h >= 0 && h == hoverIdx)
            {
                Btn b = Btns[h];
                if (b.Enabled && b.OnClick != null) b.OnClick();
            }
        }
    }

    // ---- 深色 UI 主题 (全窗体统一色板; 设置窗/剪贴板历史窗/结果窗/贴图窗共用) ----
    static class DarkUI
    {
        public static readonly Color Bg = Color.FromArgb(35, 36, 40);      // 窗体底
        public static readonly Color BgPanel = Color.FromArgb(28, 29, 33); // 面板
        public static readonly Color BgField = Color.FromArgb(22, 23, 27); // 输入框
        public static readonly Color BgTitle = Color.FromArgb(22, 23, 26); // 自绘标题栏
        public static readonly Color Text = Color.FromArgb(225, 228, 232);
        public static readonly Color TextDim = Color.FromArgb(130, 136, 146);
        public static readonly Color Btn = Color.FromArgb(52, 54, 62);
        public static readonly Color Accent = Color.FromArgb(64, 108, 190);
        public static readonly Color Danger = Color.FromArgb(200, 60, 60);

        // 给任意窗装上深色自绘标题栏(标题+×+拖动), 返回标题栏 Panel
        public static Panel MakeTitleBar(Form f, string title)
        {
            Panel p = new Panel(); p.Dock = DockStyle.Top; p.Height = 38; p.BackColor = BgTitle;
            Label tl = new Label(); tl.Text = "  " + title; tl.ForeColor = Text; tl.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            tl.AutoSize = false; tl.Dock = DockStyle.Fill; tl.TextAlign = ContentAlignment.MiddleLeft;
            Button bx = new Button(); bx.Text = "×"; bx.FlatStyle = FlatStyle.Flat; bx.FlatAppearance.BorderSize = 0;
            bx.BackColor = Color.Transparent; bx.ForeColor = Text; bx.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            bx.Size = new Size(38, 38); bx.Dock = DockStyle.Right;
            bx.FlatAppearance.MouseOverBackColor = Danger;
            bx.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
            p.Controls.Add(tl); p.Controls.Add(bx);
            MouseEventHandler drag = (s, e) =>
            {
                if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
            };
            p.MouseDown += drag; tl.MouseDown += drag;
            f.Controls.Add(p);
            return p;
        }
    }

    // 贴图窗 (PixPin 同款): 选区图钉在桌面原位置, 左键拖动 / 滚轮缩放 / 双击关闭 / 右键深色菜单
    class PinForm : Form
    {
        readonly Bitmap img;
        readonly float ratio;

        public PinForm(Bitmap image, Rectangle screenRect)
        {
            img = image;
            ratio = (float)img.Height / img.Width;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = screenRect;
            BackgroundImage = img;
            BackgroundImageLayout = ImageLayout.Stretch;
            DoubleBuffered = true;
            KeyPreview = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = Color.FromArgb(40, 41, 46); menu.ForeColor = DarkUI.Text;
            menu.Items.Add("放大 (+)", null, delegate { ScaleBy(1.1f); });
            menu.Items.Add("缩小 (-)", null, delegate { ScaleBy(1f / 1.1f); });
            menu.Items.Add("实际大小 (1:1)", null, delegate { ResizeTo(img.Width, img.Height); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("复制图片", null, delegate { try { Clipboard.SetImage(img); } catch { } });
            menu.Items.Add("关闭 (双击/Esc)", null, delegate { Close(); });
            ContextMenuStrip = menu;
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW 阴影
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add) ScaleBy(1.1f);
            else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract) ScaleBy(1f / 1.1f);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } // 拖动
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (e.Button == MouseButtons.Left) Close(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScaleBy(e.Delta > 0 ? 1.1f : 1f / 1.1f);
        }

        void ScaleBy(float k)
        {
            ResizeTo((int)(Width * k), (int)(Width * k * ratio));
        }

        void ResizeTo(int w, int h)
        {
            if (w < 40) w = 40;
            if (h < 30) h = 30;
            if (w > SystemInformation.VirtualScreen.Width) w = SystemInformation.VirtualScreen.Width;
            h = (int)(w * ratio);
            // 中心锚
            int cx = Left + Width / 2, cy = Top + Height / 2;
            SetBounds(cx - w / 2, cy - h / 2, w, h);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            BackgroundImage = null;
            try { img.Dispose(); } catch { }
        }
    }

    // M2: OCR/翻译结果窗 (深色: 自绘标题栏 + 中文友好字体 + 复制内联反馈, 不弹系统 MessageBox)
    class ResultForm : Form
    {
        readonly string text;
        readonly Button copyBtn;

        public ResultForm(string title, string text)
        {
            this.text = text;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(600, 440);
            TopMost = true;
            BackColor = DarkUI.Bg;
            KeyPreview = true;

            DarkUI.MakeTitleBar(this, title);

            TextBox tb = new TextBox();
            tb.Multiline = true; tb.ReadOnly = true; tb.ScrollBars = ScrollBars.Vertical;
            tb.Dock = DockStyle.Fill;
            tb.BackColor = DarkUI.BgField; tb.ForeColor = DarkUI.Text; tb.BorderStyle = BorderStyle.None;
            tb.Font = new Font("Microsoft YaHei UI", 10.5f);
            tb.Text = text;
            tb.Margin = new Padding(10);
            tb.Padding = new Padding(10);
            tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.C && e.Control) { CopyNow(); e.SuppressKeyPress = true; } };
            Controls.Add(tb);
            tb.BringToFront();

            Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 52; bottom.BackColor = DarkUI.BgPanel;
            Label info = new Label(); info.Text = text.Length + " 字符 · Ctrl+C 复制 · Esc 关闭"; info.Left = 14;
            info.AutoSize = true; info.ForeColor = DarkUI.TextDim; info.Font = new Font("Microsoft YaHei UI", 9f);
            info.TextAlign = ContentAlignment.MiddleLeft; info.Dock = DockStyle.Fill;
            copyBtn = new Button(); copyBtn.Text = "复制"; copyBtn.FlatStyle = FlatStyle.Flat; copyBtn.FlatAppearance.BorderSize = 0;
            copyBtn.BackColor = DarkUI.Accent; copyBtn.ForeColor = DarkUI.Text;
            copyBtn.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            copyBtn.Size = new Size(120, 36); copyBtn.Cursor = Cursors.Hand;
            copyBtn.Dock = DockStyle.Right; copyBtn.Margin = new Padding(8, 8, 10, 8);
            copyBtn.Padding = new Padding(0, 8, 0, 0);
            copyBtn.Click += delegate { CopyNow(); };
            bottom.Controls.Add(info); bottom.Controls.Add(copyBtn);
            Controls.Add(bottom);
            bottom.BringToFront();
            copyBtn.BringToFront();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        void CopyNow()
        {
            try { Clipboard.SetText(text); } catch { }
            copyBtn.Text = "✓ 已复制";
            copyBtn.BackColor = Color.FromArgb(40, 130, 80);
            Timer t = new Timer(); t.Interval = 900;
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Close(); };
            t.Start();
        }
    }
}
