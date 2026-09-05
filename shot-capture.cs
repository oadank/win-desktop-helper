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
    const int FULLSHOT_HOTKEY_ID = 0x5714; // 全屏截图热键
    const int PIN_HOTKEY_ID = 0x5715;      // 贴图热键 (剪贴板图片钉到桌面, PixPin F3 同款)
    static string shotHotkeyName = "";
    static string fullShotHotkeyName = "";
    static string pinHotkeyName = "";
    static bool captureBusy = false;

    // 贴图 (PixPin F3 同款): 剪贴板里的图直接钉到桌面 (屏幕居中), 无图则气泡提示
    static void DoPinFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                ShowTrayInfo("剪贴板里没有图片。先复制一张图, 再按 " + (pinHotkeyName == "" ? "贴图热键" : pinHotkeyName));
                return;
            }
            Bitmap img;
            using (Bitmap srcB = (Bitmap)Clipboard.GetImage())
                img = new Bitmap(srcB);
            Rectangle vs = SystemInformation.VirtualScreen;
            int w = Math.Min(img.Width, vs.Width - 40), h = Math.Min(img.Height, vs.Height - 40);
            PinForm pf = new PinForm(img, new Rectangle(vs.Left + (vs.Width - w) / 2, vs.Top + (vs.Height - h) / 2, w, h));
            pf.Show();
            Log("pin from clipboard: " + img.Width + "x" + img.Height);
        }
        catch (Exception ex) { Log("pin from clipboard err: " + ex.Message); }
    }

    // 全屏截图 (热键): 截全屏 → 图片复制到剪贴板 → 轻气泡提示 (仅手动热键触发; HTTP/MCP 调用 DoShot 从不弹泡)
    static void DoFullScreenShot()
    {
        try
        {
            string fp = DoShot(VirtualScreen());
            using (Bitmap bmp = new Bitmap(fp))
            {
                try { Clipboard.SetImage(bmp); } catch { }
            }
            Log("fullscreen shot: " + fp + " (image copied to clipboard)");
            ShowTrayInfo("已截全屏, 图片已复制到剪贴板: " + fp);
        }
        catch (Exception ex) { Log("fullscreen shot err: " + ex.Message); }
    }

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
        // 箭头样式 (PixPin 同款)
        public const int S_ARROW = 0, S_BOTH = 1, S_LINE = 2, S_CALLOUT = 3;
        public int Kind;
        public int Style = S_ARROW;  // 箭头样式
        public float Width = 3f;     // 线宽
        public int FontPt = 14;      // 文字/序号字号
        public int Angle = 0;        // 文字旋转角 (度, 顺时针)
        public string SeqFmt = "1";  // 序号格式: 1=1.2.3  I=I.II.III  a=a.b.c  A=A.B.C
        public const int S_THIN = 4, S_HOLLOW = 5; // 箭头样式 (细尾/空心)
        public string FontFamily = "Microsoft YaHei UI"; // 文字字体
        public Rectangle Rect;      // rect/ellipse/arrow 的包围盒; seq/text 的定位点在 Rect.Location
        public List<Point> Pts;     // 画笔折线 (屏坐标)
        public string Text;         // 文字内容
        public int No;              // 序号数字
        public Color Color = Color.FromArgb(242, 80, 59); // PixPin 默认红
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
        int curArrowStyle = Annot.S_ARROW;          // 属性栏: 箭头样式
        float curWidth = 3f;                        // 属性栏: 线宽 (细2/中3.5/粗6)
        Color curColor = Color.FromArgb(242, 80, 59); // 属性栏: 颜色 (PixPin 默认红)
        int seqNext = 1;
        // text annotation: self-drawn input (TextBox white opaque bg; BackgroundImage ignored by system EDIT control)
        // focus stays on overlay; chars arrive via WM_CHAR/KeyPress (IME works); text+caret painted in OnPaint = truly transparent
        bool textMode = false;
        string textBuf = "";         // 编辑中文字 (自绘渲染, 背景即截图=真透明)
        TextBox textInput;           // 1x1 隐形输入宿主 (IME 候选窗跟随到文字位置)
        int textAngle = 0;           // 旋转角 (0/90/180/270, 左上按钮步进)
        int textDragMode = 0;        // 1=移动 2=缩放 (左下/右下按钮拖动)
        Point textDragStart;
        int textDragFont0;
        int textDragDist0;
        Point textRotCenter;
        int textRotStartAng;
        int textRotBase;
        bool caretOn = true;
        Timer caretTimer;            // 自绘插入符闪烁 (500ms)
        Point textDragPt0;
        Panel editPanel;             // 透明容器: 只含四角按钮+确认/取消
        int hoverTextIdx = -1;       // 文字工具悬停的已提交文字标注 (显示可编辑提示框; 其他工具不检测=文字当背景)
        int textEditIdx = -1;        // 正在重编辑的 annots 下标 (-1 = 新建文字)
        // 自动窗口检测 (PixPin 同款): 未按下时高亮光标下的窗口, 单击即选中; 拖动则转手动框选
        Rectangle hoverWin = Rectangle.Empty;
        bool pendingDown = false;
        Point downPt;
        Rectangle pendingWin = Rectangle.Empty;       // 文字标注的行内输入框
        Point textPt;
        int curFontPt = 14; // 属性栏: 字号 (文字/序号)
        string curSeqFmt = "1"; // 属性栏: 序号格式 (1/I/a/A)
        string curFontFamily = "Microsoft YaHei UI"; // 属性栏: 字体
        static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
        static Font GetAnnotFont(int pt) { return GetAnnotFont("Microsoft YaHei UI", pt); }
        static Font GetAnnotFont(string family, int pt)
        {
            string key = family + "@" + pt;
            lock (fontCache)
            {
                Font f;
                if (!fontCache.TryGetValue(key, out f)) { f = new Font(family, pt, FontStyle.Bold); fontCache[key] = f; }
                return f;
            }
        }

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

        // PixPin 同款: 双击选区 = 复制并完成。
        // ⚠️ 必须在这里处理: WM_LBUTTONUP 不带点击数, MouseUp 的 e.Clicks 恒为 1 (实测踩坑)。
        // 第二次 Down 已在 OnMouseDown 里 return 跳过, 此处状态未被破坏; 复制后关闭, 无残余消息
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && hasSel && tool == null && !textMode && cur == null && !IsDisposed)
            {
                Log("capture: dblclick = copy");
                ActCopy();
                return;
            }
            base.OnMouseDoubleClick(e);
        }

        // 右键: 有选区/工具时 = 清空重新框选 (PixPin 同款); 什么都没有才退出
        void ResetSelection()
        {
            if (textMode) CancelTextInput();
            tool = null;
            if (cur != null) { var c = cur; cur = null; }
            annots.Clear();
            redoStack.Clear();
            hasSel = false;
            sel = Rectangle.Empty;
            HideBar();
            InvalidateHover();
            Invalidate(); // 暗层恢复全屏 (低频操作, 全屏重绘一次可接受)
            Log("capture: reset selection (right-click)");
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (IsDisposed) return;
            if (e.Button == MouseButtons.Right)
            {
                if (hasSel || tool != null || textMode) ResetSelection();
                else CancelAll();
                return;
            }
            if (e.Button == MouseButtons.Middle && hasSel) { ActPin(); return; } // 中键 = 贴图 (PixPin 同款)
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks >= 2) return; // 双击的第二次按下不动作 (Double 事件里复制, 防重复框选)
            Point sp = PointToScreen(e.Location);
            if (!hasSel)
            {
                // 未选区: 记 pending — 移动超阈值 = 手动拖框; 原地松开 = 选中悬停窗口 (自动检测)
                CommitTextInput();
                pendingDown = true;
                downPt = sp;
                start = sp;
                sel = new Rectangle(sp, Size.Empty);
                pendingWin = hoverWin;
                return;
            }
            if (false)
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
                    // 点到已提交文字 = 重编辑 (载入原文/角度/字体原位替换); 否则在此处开新文字
                    int hit = HitTextAnnot(sp);
                    if (hit >= 0) { EditTextInput(hit); return; }
                    OpenTextInput(sp);
                    return;
                }
                cur = new Annot();
                cur.Kind = tool == "rect" ? Annot.K_RECT : tool == "ellipse" ? Annot.K_ELLIPSE :
                           tool == "arrow" ? Annot.K_ARROW : tool == "seq" ? Annot.K_SEQ : Annot.K_PEN;
                cur.Width = curWidth; cur.Color = curColor; cur.Style = curArrowStyle; cur.FontPt = curFontPt; cur.FontFamily = curFontFamily;
                downPt = sp; // 形状基准点 = 按下处 (漂移修复: 旧版用上一次 Rect.Location, 越拖越飘)
                if (cur.Kind == Annot.K_ARROW) cur.Pts = new List<Point> { sp, sp }; // 箭头存真实起终点
                if (cur.Kind == Annot.K_SEQ)
                {
                    try
                    {
                        cur.No = seqNext++;
                        int dia = (int)(curFontPt * 2);
                        cur.Rect = new Rectangle(sp.X - dia / 2, sp.Y - dia / 2, dia, dia);
                        cur.Width = curWidth; cur.Color = curColor; cur.FontPt = curFontPt; cur.FontFamily = curFontFamily; cur.SeqFmt = curSeqFmt;
                        PushAnnot(cur);
                        Log("capture: annot seq #" + cur.No);
                    }
                    catch (Exception ex)
                    {
                        Log("annot seq err: " + ex.Message);
                        ShowTrayInfo("序号异常(已拦截): " + ex.Message);
                    }
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
            // 文字工具悬停检测: 已提交文字 → 提示框 (其他工具不调用 = 文字当背景)
            UpdateTextHover(sp, e.Location);
            if (pendingDown && !dragging)
            {
                // 按下后移动超阈值 -> 转手动拖框 (自动检测让位)
                if (Math.Abs(sp.X - downPt.X) > 6 || Math.Abs(sp.Y - downPt.Y) > 6)
                {
                    pendingDown = false;
                    dragging = true;
                    InvalidateHover();
                }
            }
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
                else if (cur.Kind == Annot.K_ARROW)
                {
                    cur.Pts[1] = sp; // 终点跟手 (方向 = 起点->鼠标)
                    Rectangle old = cur.Rect;
                    cur.Rect = Normalize(cur.Pts[0], sp);
                    InvalidateAnnot(old);
                    InvalidateAnnot(cur);
                }
                else
                {
                    Rectangle old = cur.Rect;
                    cur.Rect = Normalize(downPt, sp); // 基准 = 按下起点 (漂移修复)
                    InvalidateAnnot(old);
                    InvalidateAnnot(cur);
                }
            }
            else if (!hasSel && !pendingDown && tool == null && !textMode)
            {
                UpdateHoverDetect(sp); // 自动窗口检测
            }
        }

        // 只刷文字字形 (caret 闪烁用: 不带动四角按钮重绘, 按钮不闪)
        void InvalidateTextGlyph()
        {
            try
            {
                Size ts = TextRenderer.MeasureText(textBuf.Length > 0 ? textBuf : "测", GetAnnotFont(curFontFamily, curFontPt));
                Rectangle r = new Rectangle(RectangleToClient(new Rectangle(textPt, Size.Empty)).Location,
                                            new Size(ts.Width + 40, ts.Height + 40));
                r.Intersect(ClientRectangle);
                Invalidate(r);
            }
            catch (Exception ex) { Log("invalglyph err: " + ex.Message); }
        }

        void InvalidateTextInput()
        {
            try
            {
                Size ts = TextRenderer.MeasureText(textBuf.Length > 0 ? textBuf : "测", GetAnnotFont(curFontFamily, curFontPt));
                Rectangle r = new Rectangle(RectangleToClient(new Rectangle(textPt, Size.Empty)).Location,
                                            new Size(ts.Width + 60, ts.Height + 60));
                int m = Math.Max(r.Width, r.Height) + ts.Height + 80; // 旋转/缩放后范围
                r.Inflate(m, m);
                r.Intersect(ClientRectangle);
                Invalidate(r);
                if (editPanel != null) { editPanel.Invalidate(); RelocateTextUI(); } // 按钮跟随文字范围
            }
            catch (Exception ex) { Log("invaltext err: " + ex.Message); }
        }

        // 自动窗口检测节流 (EnumWindows 全枚举, 50ms 一次)
        DateTime lastHoverCheck = DateTime.MinValue;

        // 文字工具悬停: 鼠标下有已提交文字 → hoverTextIdx (提示框刷新); 其他工具强制无
        void UpdateTextHover(Point sp, Point clientPt)
        {
            try
            {
                int hit = (tool == "text" && hasSel && !textMode && !dragging && sel.Contains(sp))
                    ? HitTextAnnot(sp) : -1;
                if (hit == hoverTextIdx) return;
                if (hoverTextIdx >= 0 && hoverTextIdx < annots.Count && annots[hoverTextIdx].Kind == Annot.K_TEXT)
                    Invalidate(TextAnnotBounds(annots[hoverTextIdx]));
                hoverTextIdx = hit;
                if (hoverTextIdx >= 0) Invalidate(TextAnnotBounds(annots[hoverTextIdx]));
            }
            catch (Exception ex) { Log("texthover err: " + ex.Message); }
        }

        void UpdateHoverDetect(Point sp)
        {
            try { UpdateHoverDetectCore(sp); }
            catch (Exception ex) { Log("hover detect err: " + ex.Message); }
        }

        void UpdateHoverDetectCore(Point sp)
        {
            if ((DateTime.Now - lastHoverCheck).TotalMilliseconds < 50) return;
            lastHoverCheck = DateTime.Now;
            POINT pp; pp.x = sp.X; pp.y = sp.Y;
            IntPtr h = WindowFromPointEx(pp, Handle);
            Rectangle nr = Rectangle.Empty;
            if (h != IntPtr.Zero)
            {
                RECT r;
                if (GetWindowRect(h, out r) && r.Right > r.Left && r.Bottom > r.Top)
                    nr = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
            if (nr != hoverWin)
            {
                Rectangle old = hoverWin;
                hoverWin = nr;
                Rectangle u = nr != Rectangle.Empty ? RectangleToClient(nr) : Rectangle.Empty;
                if (old != Rectangle.Empty)
                    u = u != Rectangle.Empty ? Rectangle.Union(u, RectangleToClient(old)) : RectangleToClient(old);
                if (u != Rectangle.Empty) { u.Inflate(40, 30); Invalidate(u); }
            }
        }

        void InvalidateHover()
        {
            if (hoverWin == Rectangle.Empty) return;
            Rectangle c = RectangleToClient(hoverWin);
            c.Inflate(40, 30);
            Invalidate(c);
            hoverWin = Rectangle.Empty;
        }

        // 文字输入: 宿主 TextBox 的字符经 KeyPreview 抬升到 Form (IME 确认后的中文照常)
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!textMode) return;
            if (e.KeyChar == '\r') { CommitTextInput(); e.Handled = true; return; }
            if (e.KeyChar == '\b') { if (textBuf.Length > 0) { textBuf = textBuf.Remove(textBuf.Length - 1); textInput.Text = textBuf; } e.Handled = true; return; }
            if (e.KeyChar >= ' ') { textBuf += e.KeyChar; if (textInput != null) textInput.Text = textBuf; InvalidateTextInput(); e.Handled = true; }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            try
            {
            if (IsDisposed) return;
            // pending 未拖动 = 选中悬停窗口 (自动检测完成, 等价框选)
            if (pendingDown && e.Button == MouseButtons.Left && !dragging && !hasSel)
            {
                pendingDown = false;
                if (pendingWin != Rectangle.Empty && pendingWin.Width > 10 && pendingWin.Height > 10)
                {
                    sel = pendingWin;
                    hasSel = true;
                    Log("capture: window picked (auto-detect) " + sel.Width + "x" + sel.Height);
                    ShowBar();
                }
                InvalidateHover();
                return;
            }
            pendingDown = false;
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
                else if (cur.Kind == Annot.K_ARROW) { if (cur.Pts.Count >= 2 && cur.Pts[0] != cur.Pts[1]) PushAnnot(cur); }
                else if (cur.Rect.Width > 2 && cur.Rect.Height > 2) PushAnnot(cur);
                cur = null;
            }
            }
            catch (Exception ex) { Log("mouseup err: " + ex.Message); }
        }

        // 背景 = 冻结原图 blit 脏区 (1:1 无缩放走 fast path, 只处理脏区)
        // 性能关键: NearestNeighbor + Half 让 1:1 blit 走 GDI fast path
        // (DPI 125% 下 Graphics 带 1.25 变换, 默认 Bilinear 走慢速插值管线 = 拖动卡顿真凶)
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (frozen != null)
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                e.Graphics.DrawImage(frozen, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
            }
            else
                base.OnPaintBackground(e);
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
                Size ts = TextRenderer.MeasureText(a.Text, GetAnnotFont(a.FontFamily, a.FontPt));
                return new Rectangle(a.Rect.Location, new Size(ts.Width + 8, ts.Height + 8));
            }
            if (a.Kind == Annot.K_ARROW && a.Pts != null && a.Pts.Count >= 2)
            {
                Rectangle r = new Rectangle(a.Pts[0], Size.Empty);
                r = Rectangle.Union(r, new Rectangle(a.Pts[1], Size.Empty));
                r.Inflate(14, 14); // 箭头头/线宽余量
                return r;
            }
            return a.Rect;
        }

        void CancelAll() { Log("capture: cancelled (esc/right-click)"); Close(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!hasSel)
            {
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(dim, e.ClipRectangle);
                if (hoverWin != Rectangle.Empty)
                {
                    // 悬停窗口: 挖亮 + 橙框 (PixPin 自动检测观感)
                    Rectangle hd = RectangleToClient(hoverWin);
                    hd.Intersect(ClientRectangle);
                    if (hd.Width > 4 && hd.Height > 4)
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.SetClip(hd);
                        g.DrawImage(frozen, hd, hd, GraphicsUnit.Pixel);
                        g.ResetClip();
                        using (Pen hp = new Pen(Color.Orange, 2)) g.DrawRectangle(hp, hd);
                    }
                }
                return;
            }
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
            DrawAnnots(g, Point.Empty);
            if (textMode)
            {
                // 编辑中文字: 自绘在截图上 (真透明), 支持旋转角
                var st = g.Save();
                g.TranslateTransform(d.X + (textPt.X - sel.X), d.Y + (textPt.Y - sel.Y));
                g.RotateTransform(textAngle);
                using (SolidBrush tb = new SolidBrush(curColor))
                {
                    Font pf = GetAnnotFont(curFontFamily, curFontPt); // 缓存字体不可 Dispose (第4处, 漏修导致全局崩溃)
                    g.DrawString(textBuf, pf, tb, 0, 0);
                    if (caretOn)
                    {
                        SizeF tw2 = g.MeasureString(textBuf.Length > 0 ? textBuf : "|", pf);
                        using (Pen cp = new Pen(curColor, 2f)) g.DrawLine(cp, tw2.Width * 0.92f, 2, tw2.Width * 0.92f, pf.Height - 2);
                    }
                }
                g.Restore(st);
                // 编辑线框 (文字范围边界, 蓝 1.5px) — 跟随 textAngle 旋转 (旋转后四角连线, 与文字同转)
                using (Pen bp = new Pen(Color.FromArgb(60, 110, 200), 1.5f))
                    g.DrawPolygon(bp, RotatedTextCorners());
            }
            DrawAnnots(g, Point.Empty); // 选区内标注 (客户区即冻结图坐标, 偏移0)
            // 文字工具悬停已提交文字: 蓝框提示可点击重编辑 (随文字旋转; 其他工具不出现)
            if (tool == "text" && !textMode && hoverTextIdx >= 0 && hoverTextIdx < annots.Count
                && annots[hoverTextIdx].Kind == Annot.K_TEXT)
            {
                Annot ha = annots[hoverTextIdx];
                Size hts = TextRenderer.MeasureText(ha.Text.Length > 0 ? ha.Text : "测", GetAnnotFont(ha.FontFamily, ha.FontPt));
                Point hc = RectangleToClient(new Rectangle(ha.Rect.Location, Size.Empty)).Location;
                var hs = g.Save();
                g.TranslateTransform(hc.X, hc.Y);
                g.RotateTransform(ha.Angle);
                using (Pen hp = new Pen(Color.FromArgb(60, 110, 200), 1.5f))
                    g.DrawRectangle(hp, -4, -4, hts.Width + 10, hts.Height + 10);
                g.Restore(hs);
            }
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
            using (Pen p = new Pen(a.Color, a.Width))
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
                        if (a.Pts != null && a.Pts.Count >= 2)
                            DrawArrowEx(g, p, a.Pts[0].X - offset.X, a.Pts[0].Y - offset.Y,
                                        a.Pts[1].X - offset.X, a.Pts[1].Y - offset.Y, a.Style);
                        else
                            DrawArrowEx(g, p, a.Rect.X - offset.X, a.Rect.Y - offset.Y,
                                        a.Rect.Right - offset.X, a.Rect.Bottom - offset.Y, a.Style);
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
                        {
                            Font f = GetAnnotFont(a.FontFamily, a.FontPt); // 缓存字体不可 Dispose
                            if (a.Angle == 0)
                                g.DrawString(a.Text, f, b, a.Rect.X - offset.X, a.Rect.Y - offset.Y);
                            else
                            {
                                var st = g.Save();
                                g.TranslateTransform(a.Rect.X - offset.X, a.Rect.Y - offset.Y);
                                g.RotateTransform(a.Angle);
                                g.DrawString(a.Text, f, b, 0, 0);
                                g.Restore(st);
                            }
                        }
                        break;
                    case Annot.K_SEQ:
                        Rectangle r = a.Rect; r.Offset(-offset.X, -offset.Y);
                        using (SolidBrush b = new SolidBrush(a.Color)) g.FillEllipse(b, r);
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            using (Brush wb = new SolidBrush(Color.White))
                            {
                                Font f = GetAnnotFont(a.FontFamily, a.FontPt); // 缓存字体不可 Dispose
                                g.DrawString(SeqText(a.No, a.SeqFmt), f, wb, (RectangleF)r, sf);
                            }
                        }
                        break;
                }
            }
        }

        static void DrawArrowHead(Graphics g, Pen p, float tipX, float tipY, float ang, float hl)
        {
            PointF a1 = new PointF(tipX - hl * (float)Math.Cos(ang - 0.45), tipY - hl * (float)Math.Sin(ang - 0.45));
            PointF a2 = new PointF(tipX - hl * (float)Math.Cos(ang + 0.45), tipY - hl * (float)Math.Sin(ang + 0.45));
            using (SolidBrush b = new SolidBrush(p.Color))
                g.FillPolygon(b, new PointF[] { new PointF(tipX, tipY), a1, a2 });
        }

        // 箭头 6 样式 (照 PixPin): 实线 / 双向 / 细尾锥形 / 空心 / 标注线(双竖杠) / 直线
        // 头部大小随线宽缩放: hl=max(14, width*6) — 粗线大箭头 (用户实测要求)
        static void DrawArrowEx(Graphics g, Pen p, float x1, float y1, float x2, float y2, int style)
        {
            float w = p.Width;
            float hl = Math.Max(26, w * 8f);   // 头长 (PixPin 比例: 大醒目)
            float half = Math.Max(12, w * 4f); // 头底半宽
            double ang = Math.Atan2(y2 - y1, x2 - x1);
            double dxc = Math.Cos(ang), dyc = Math.Sin(ang);
            double pxc = -dyc, pyc = dxc;

            if (style == Annot.S_LINE) { g.DrawLine(p, x1, y1, x2, y2); return; }

            if (style == Annot.S_CALLOUT)
            {
                float bl = Math.Max(14, w * 4f);
                float ppx = (float)pxc, ppy = (float)pyc;
                g.DrawLine(p, x1, y1, x2, y2);
                g.DrawLine(p, x1 + bl * ppx, y1 + bl * ppy, x1 - bl * ppx, y1 - bl * ppy);
                g.DrawLine(p, x2 + bl * ppx, y2 + bl * ppy, x2 - bl * ppx, y2 - bl * ppy);
                return;
            }

            if (style == Annot.S_THIN)
            {
                // 细尾锥形: 尾尖 -> 头底渐宽 (填充) + 大实心头 (PixPin 特色款)
                float bx = (float)(x2 - hl * 0.6 * dxc), by = (float)(y2 - hl * 0.6 * dyc);
                float th = Math.Max(2f, w * 0.8f);
                using (SolidBrush b = new SolidBrush(p.Color))
                    g.FillPolygon(b, new PointF[] {
                        new PointF(x1, y1),
                        new PointF((float)(bx + pxc * th), (float)(by + pyc * th)),
                        new PointF(x2, y2),
                        new PointF((float)(bx - pxc * th), (float)(by - pyc * th)),
                    });
                DrawArrowHead(g, p, x2, y2, (float)ang, hl);
                return;
            }

            if (style == Annot.S_HOLLOW)
            {
                float bx = (float)(x2 - hl * 0.9 * dxc), by = (float)(y2 - hl * 0.9 * dyc);
                g.DrawLine(p, x1, y1, bx, by);
                g.DrawPolygon(p, new PointF[] {
                    new PointF(x2, y2),
                    new PointF((float)(bx + pxc * half), (float)(by + pyc * half)),
                    new PointF((float)(bx - pxc * half), (float)(by - pyc * half)),
                });
                return;
            }

            // 实线/双向: 线画到头底 (头不被线穿透) + 大实心头
            float b2x = (float)(x2 - hl * 0.72 * dxc), b2y = (float)(y2 - hl * 0.72 * dyc);
            g.DrawLine(p, x1, y1, b2x, b2y);
            using (SolidBrush b = new SolidBrush(p.Color))
                g.FillPolygon(b, new PointF[] {
                    new PointF(x2, y2),
                    new PointF((float)(b2x + pxc * half), (float)(b2y + pyc * half)),
                    new PointF((float)(b2x - pxc * half), (float)(b2y - pyc * half)),
                });
            if (style == Annot.S_BOTH)
            {
                double angB = Math.Atan2(y1 - y2, x1 - x2);
                float b1x = (float)(x1 + hl * 0.72 * Math.Cos(angB)), b1y = (float)(y1 + hl * 0.72 * Math.Sin(angB));
                g.DrawLine(p, x1, y1, b1x, b1y);
                DrawArrowHead(g, p, x1, y1, (float)angB, hl);
            }
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
        // ---- text input (self-drawn): click inside selection, type directly, Enter commits, Esc cancels ----
        // ---- 文字输入 (PixPin 四角按钮式): 透明背景自绘文字 + 1x1 IME 宿主 + 四角功能按钮 ----
        void OpenTextInput(Point sp) { OpenTextInputCore(sp, null, -1); }

        // 重编辑已提交文字: 载入原标注的内容/角度/字号/颜色/字体, 提交时原位替换 (不新增一笔)
        void EditTextInput(int idx)
        {
            if (idx < 0 || idx >= annots.Count || annots[idx].Kind != Annot.K_TEXT) return;
            Annot a = annots[idx];
            OpenTextInputCore(a.Rect.Location, a, idx);
        }

        void OpenTextInputCore(Point sp, Annot seed, int editIdx)
        {
            CommitTextInput();
            textMode = true;
            if (seed != null)
            {
                // 重编辑: 载入原标注属性 (颜色/字号/字体随属性栏当前值会被覆盖, 这里以原标注为准)
                textBuf = seed.Text; textPt = seed.Rect.Location; textAngle = seed.Angle;
                curColor = seed.Color; curFontPt = seed.FontPt; curFontFamily = seed.FontFamily;
                textEditIdx = editIdx;
            }
            else
            {
                textBuf = "";
                textPt = sp;
                textAngle = 0;
                textEditIdx = -1;
            }

            // 1x1 输入宿主: 定位在文字插入点 (IME 候选窗跟随到文字位置, 不再跑角落)
            textInput = new TextBox();
            textInput.SetBounds(RectangleToClient(new Rectangle(sp, Size.Empty)).X,
                                RectangleToClient(new Rectangle(sp, Size.Empty)).Y, 2, 2);
            textInput.BorderStyle = BorderStyle.None;
            textInput.TabIndex = 98;
            textInput.Text = textBuf; // 重编辑: 宿主载入原文 (IME/退格从原文续算)
            textInput.KeyDown += (s, ev) =>
            {
                if (ev.KeyCode == Keys.Enter) { ev.SuppressKeyPress = true; CommitTextInput(); }
                else if (ev.KeyCode == Keys.Escape) { ev.SuppressKeyPress = true; CancelTextInput(); }
            };
            textInput.TextChanged += (s, ev) => { textBuf = textInput.Text; RelocateTextUI(); InvalidateTextInput(); };
            Controls.Add(textInput);

            // 四角功能按钮 (透明容器内, 20x20, 深底白符号)
            editPanel = new Panel();
            editPanel.BackColor = Color.Transparent;
            int[] dxs = { -26, 26, -26, 26 }, dys = { -26, -26, 26, 26 };
            int[] iconCodes = { 0xE7AD, 0xE711, 0xE7C2, 0xE71F }; // 旋转/关闭/移动/缩放 (MDL2 码点) // 旋转/关闭/移动/缩放 (MDL2)
            string[] tips = { "旋转 (每次90度)", "关闭 (取消输入)", "拖动移动文字", "拖动缩放文字" };
            Action<int> clickAct = null;
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                Button b = new Button();
                if (idx == 3)
                {
                    // 缩放 = 斜向双向箭头 (手画, PixPin 同款语义)
                    b.Text = "";
                    b.Paint += (s, ev) =>
                    {
                        Graphics g2 = ev.Graphics;
                        g2.SmoothingMode = SmoothingMode.AntiAlias;
                        using (Pen pp = new Pen(Color.White, 2f))
                        {
                            pp.StartCap = LineCap.Round; pp.EndCap = LineCap.Round;
                            g2.DrawLine(pp, 5, 17, 15, 7);
                            g2.DrawLine(pp, 5, 17, 5, 11);
                            g2.DrawLine(pp, 5, 17, 11, 17);
                            g2.DrawLine(pp, 15, 7, 9.5f, 7);
                            g2.DrawLine(pp, 15, 7, 15, 12.5f);
                        }
                    };
                }
                else
                    b.Text = char.ConvertFromUtf32(iconCodes[idx]);
                b.Font = new Font("Segoe MDL2 Assets", 9f);
                b.ForeColor = Color.White; b.BackColor = Color.FromArgb(50, 110, 200);
                b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
                b.Size = new Size(22, 22); b.Cursor = Cursors.Hand;
                b.Tag = i;
                if (i == 0)
                {
                    b.MouseDown += (s, ev) =>
                    {
                        if (ev.Button == MouseButtons.Left)
                        {
                            textDragMode = 3; // 旋转 (拖动实时任意角度)
                            Size tsz = TextRenderer.MeasureText(textBuf.Length > 0 ? textBuf : "测", GetAnnotFont(curFontFamily, curFontPt));
                            textRotCenter = new Point(textPt.X + tsz.Width / 2, textPt.Y + tsz.Height / 2);
                            textRotStartAng = (int)(Math.Atan2(Cursor.Position.Y - textRotCenter.Y, Cursor.Position.X - textRotCenter.X) * 180 / Math.PI);
                            textRotBase = textAngle;
                            b.Capture = true;
                        }
                    };
                }
                if (i == 1) b.Click += delegate { CancelTextInput(); };
                if (i == 2) { b.MouseDown += (s, ev) => { if (ev.Button == MouseButtons.Left) { textDragMode = 1; textDragStart = Cursor.Position; textDragPt0 = textPt; b.Capture = true; } }; }
                if (i == 3)
                {
                    b.MouseDown += (s, ev) =>
                    {
                        if (ev.Button == MouseButtons.Left)
                        {
                            textDragMode = 2;
                            textDragStart = Cursor.Position;
                            textDragFont0 = curFontPt;
                            Size tsz0 = TextRenderer.MeasureText(textBuf.Length > 0 ? textBuf : "测", GetAnnotFont(curFontFamily, curFontPt));
                            Point ctr0 = new Point(textPt.X + tsz0.Width / 2, textPt.Y + tsz0.Height / 2);
                            textDragDist0 = Math.Abs(Cursor.Position.X - ctr0.X) + Math.Abs(Cursor.Position.Y - ctr0.Y); // 初始距离基准 (差分防反向)
                            b.Capture = true;
                        }
                    };
                }
                b.MouseMove += (s, ev) =>
                {
                    if (textDragMode == 0 || ev.Button != MouseButtons.Left) return;
                    if (textDragMode == 1)
                    {
                        textPt = new Point(textDragPt0.X + Cursor.Position.X - textDragStart.X, textDragPt0.Y + Cursor.Position.Y - textDragStart.Y);
                        RelocateTextUI();
                        InvalidateTextInput();
                    }
                    else if (textDragMode == 2)
                    {
                        Size tsz = TextRenderer.MeasureText(textBuf.Length > 0 ? textBuf : "测", GetAnnotFont(curFontFamily, curFontPt));
                        Point ctr = new Point(textPt.X + tsz.Width / 2, textPt.Y + tsz.Height / 2);
                        int dist = Math.Abs(Cursor.Position.X - ctr.X) + Math.Abs(Cursor.Position.Y - ctr.Y);
                        curFontPt = Math.Max(8, Math.Min(72, textDragFont0 + (dist - textDragDist0) / 4)); // 差分: 拉远放大/拉近缩小, 不反向
                        RelocateTextUI();
                        InvalidateTextInput();
                    }
                    else if (textDragMode == 3)
                    {
                        int ang = (int)(Math.Atan2(Cursor.Position.Y - textRotCenter.Y, Cursor.Position.X - textRotCenter.X) * 180 / Math.PI);
                        textAngle = (textRotBase + ang - textRotStartAng + 360 * 4) % 360; // 实时任意角度
                        RelocateTextUI();
                        InvalidateTextInput();
                    }
                };
                b.MouseUp += (s, ev) => { textDragMode = 0; b.Capture = false; };
                editPanel.Controls.Add(b);
            }


            RelocateTextUI();
            Controls.Add(editPanel);
            editPanel.BringToFront();
            textInput.Focus();
            if (caretTimer == null)
            {
                caretTimer = new Timer { Interval = 450 };
                caretTimer.Tick += (s, ev) => { caretOn = !caretOn; InvalidateTextGlyph(); };
            }
            caretOn = true;
            caretTimer.Start();
            Log("capture: text input opened at " + sp + " (transparent, 4-corner buttons)");
        }

        // 四角按钮+确认取消 重定位 (文字区域四角 + 下方横排)
        // 文字编辑线框 (客户区): 输入文字的范围边界, OnPaint 画线 + 四角按钮定位 共用
        Rectangle TextBoxBounds()
        {
            Size ts = TextRenderer.MeasureText(textBuf.Length > 0 ? textBuf : "测", GetAnnotFont(curFontFamily, curFontPt));
            Point c = RectangleToClient(new Rectangle(textPt, Size.Empty)).Location;
            return new Rectangle(c.X - 4, c.Y - 4, ts.Width + 10, ts.Height + 10);
        }

        // 文字框四角旋转到客户区 (textPt 为旋转中心; 测量口径与 TextBoxBounds 一致: ±4 边距)
        // 顺序 = 文字自身坐标系 TL/TR/BL/BR —— 旋转后仍一一对应四角按钮 (旋转/关闭/移动/缩放)
        // 修复: 此前线框/按钮锚在未旋转的轴对齐框上, 文字转了框不跟 (老大实测 bug)
        PointF[] RotatedTextCorners()
        {
            Size ts = TextRenderer.MeasureText(textBuf.Length > 0 ? textBuf : "测", GetAnnotFont(curFontFamily, curFontPt));
            Point c = RectangleToClient(new Rectangle(textPt, Size.Empty)).Location;
            float x0 = -4f, y0 = -4f, x1 = ts.Width + 6f, y1 = ts.Height + 6f;
            float ang = textAngle * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(ang), sin = (float)Math.Sin(ang);
            // 环形顺序 TL->TR->BR->BL (DrawPolygon 按序连线; Z字序会连出 X 交叉线)
            PointF[] local = { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
            PointF[] res = new PointF[4];
            for (int i = 0; i < 4; i++)
                res[i] = new PointF(c.X + local[i].X * cos - local[i].Y * sin,
                                    c.Y + local[i].X * sin + local[i].Y * cos);
            return res;
        }

        // 文字工具专用命中: 已提交文字标注 (点逆旋转到文字局部坐标判定), 返回最上层命中下标
        // 其他工具不调用此函数 → 文字不参与命中, 当背景图 (老大要求)
        int HitTextAnnot(Point sp)
        {
            for (int i = annots.Count - 1; i >= 0; i--)
            {
                Annot a = annots[i];
                if (a.Kind != Annot.K_TEXT) continue;
                Size ts = TextRenderer.MeasureText(a.Text.Length > 0 ? a.Text : "测", GetAnnotFont(a.FontFamily, a.FontPt));
                double ang = -a.Angle * Math.PI / 180.0;
                double dx = sp.X - a.Rect.X, dy = sp.Y - a.Rect.Y;
                double lx = dx * Math.Cos(ang) - dy * Math.Sin(ang);
                double ly = dx * Math.Sin(ang) + dy * Math.Cos(ang);
                if (lx >= -4 && lx <= ts.Width + 6 && ly >= -4 && ly <= ts.Height + 6) return i;
            }
            return -1;
        }

        // 已提交文字的屏幕包围盒 (客户区, 含旋转范围; Invalidate 提示框刷新用)
        Rectangle TextAnnotBounds(Annot a)
        {
            Size ts = TextRenderer.MeasureText(a.Text.Length > 0 ? a.Text : "测", GetAnnotFont(a.FontFamily, a.FontPt));
            Point c = RectangleToClient(new Rectangle(a.Rect.Location, Size.Empty)).Location;
            float x0 = -4f, y0 = -4f, x1 = ts.Width + 6f, y1 = ts.Height + 6f;
            float ang = a.Angle * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(ang), sin = (float)Math.Sin(ang);
            // 环形顺序 TL->TR->BR->BL (DrawPolygon 按序连线; Z字序会连出 X 交叉线)
            PointF[] local = { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                float rx = local[i].X * cos - local[i].Y * sin, ry = local[i].X * sin + local[i].Y * cos;
                minX = Math.Min(minX, rx); maxX = Math.Max(maxX, rx);
                minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry);
            }
            Rectangle r = Rectangle.Round(new RectangleF(c.X + minX, c.Y + minY, maxX - minX, maxY - minY));
            r.Inflate(24, 24);
            r.Intersect(ClientRectangle);
            return r;
        }

        void RelocateTextUI()
        {
            if (editPanel == null) return;
            try
            {
                PointF[] corners = RotatedTextCorners(); // 旋转后四角 (TL/TR/BL/BR 一一对应四按钮)
                // 容器 = 四角包围盒外扩 30 (容纳按钮), 底部再留 44 (确认/取消行)
                float minX = corners[0].X, maxX = corners[0].X, minY = corners[0].Y, maxY = corners[0].Y;
                for (int i = 1; i < 4; i++)
                {
                    minX = Math.Min(minX, corners[i].X); maxX = Math.Max(maxX, corners[i].X);
                    minY = Math.Min(minY, corners[i].Y); maxY = Math.Max(maxY, corners[i].Y);
                }
                Point org = new Point((int)minX - 30, (int)minY - 30);
                editPanel.Location = org;
                editPanel.Size = new Size((int)(maxX - minX) + 60 + 8, (int)(maxY - minY) + 60 + 8);
                // 四角按钮贴旋转后四角: Tag 0=旋转(TL) 1=关闭(TR) 2=移动(BL) 3=缩放(BR)
                // corners 环形序 [0]=TL [1]=TR [2]=BR [3]=BL
                int[] tagToCorner = { 0, 1, 3, 2 };
                foreach (Control ctl in editPanel.Controls)
                {
                    Button b = ctl as Button;
                    if (b == null || b.Size.Width != 22) continue;
                    int tag = (b.Tag is int) ? (int)b.Tag : -1;
                    if (tag < 0 || tag > 3) continue;
                    PointF c0 = corners[tagToCorner[tag]];
                    b.Location = new Point((int)(c0.X - org.X - 11), (int)(c0.Y - org.Y - 11));
                }

            }
            catch (Exception ex) { Log("relocatetextui err: " + ex.Message); }
        }

        void CommitTextInput()
        {
            if (!textMode) return;
            string t = textBuf;
            Point pt = textPt;
            int ang = textAngle;
            int editIdx = textEditIdx;
            CloseTextInput(); // 复位 textEditIdx/hoverTextIdx
            if (!string.IsNullOrEmpty(t))
            {
                Annot a = new Annot();
                a.Kind = Annot.K_TEXT;
                a.Text = t;
                a.Color = curColor;
                a.FontPt = curFontPt;
                a.FontFamily = curFontFamily;
                a.Angle = ang;
                a.Rect = new Rectangle(pt, Size.Empty);
                if (editIdx >= 0 && editIdx < annots.Count && annots[editIdx].Kind == Annot.K_TEXT)
                {
                    Rectangle oldB = TextAnnotBounds(annots[editIdx]); // 旧位置 (可能被拖走)
                    annots[editIdx] = a;                               // 重编辑: 原位替换
                    Invalidate(oldB);
                    Invalidate(TextAnnotBounds(a));
                    Log("capture: annot text edited (#" + editIdx + ", " + t.Length + " chars, angle=" + ang + ")");
                }
                else
                {
                    PushAnnot(a);
                    Log("capture: annot text (" + t.Length + " chars, angle=" + ang + ")");
                }
            }
        }

        void CancelTextInput()
        {
            if (!textMode) return;
            CloseTextInput();
            Log("capture: text input cancelled");
        }

        void CloseTextInput()
        {
            textMode = false;
            textBuf = "";
            if (caretTimer != null) caretTimer.Stop();
            if (textInput != null) { try { Controls.Remove(textInput); textInput.Dispose(); } catch { } textInput = null; } // 宿主在遮罩上, 单独销毁 (残留=插入符一直闪)
            if (editPanel != null) { try { editPanel.Dispose(); } catch { } editPanel = null; }
            // 复位悬停/重编辑状态 (悬停提示框区域刷掉, 下次移动重算)
            if (hoverTextIdx >= 0 && hoverTextIdx < annots.Count && annots[hoverTextIdx].Kind == Annot.K_TEXT)
                Invalidate(TextAnnotBounds(annots[hoverTextIdx]));
            hoverTextIdx = -1;
            textEditIdx = -1;
            InvalidateTextInput();
            Focus();
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
            bar.Add("rec", "录屏 (延迟可选)", delegate { ShowRecMenu(); });
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
            ShowPropBar();
            Log("capture: tool=" + (tool ?? "(none)"));
        }

        // ---- 二级属性栏 (PixPin 同款): 样式(箭头) + 粗细 + 颜色; 选中标注工具时贴在主条下方 ----
        ToolbarPanel propBar;

        void ShowPropBar()
        {
            if (propBar == null) { BuildPropBar(); }
            bool need = tool == "arrow" || tool == "rect" || tool == "ellipse" || tool == "pen" || tool == "text" || tool == "seq";
            if (!need) { propBar.Visible = false; return; }
            // 分组可用性: 箭头样式仅箭头; 粗细对文字/序号无意义禁用; 字号仅文字/序号
            foreach (var b in propBar.Btns)
            {
                if (b.ToolKey == null) continue;
                if (b.ToolKey.StartsWith("sty")) b.Enabled = (tool == "arrow");
                else if (b.ToolKey.StartsWith("w_")) b.Enabled = (tool != "text" && tool != "seq");
                else if (b.ToolKey != null && b.ToolKey.StartsWith("f_")) b.Enabled = (tool == "text" || tool == "seq");
            }
            MarkProp();
            PlacePropBar();
            propBar.Visible = true;
        }

        void BuildPropBar()
        {
            propBar = new ToolbarPanel();
            propBar.Add("sty_arrow", "实线箭头", delegate { curArrowStyle = Annot.S_ARROW; MarkProp(); }).ToolKey = "sty_arrow";
            propBar.Add("sty_both", "双向箭头", delegate { curArrowStyle = Annot.S_BOTH; MarkProp(); }).ToolKey = "sty_both";
            propBar.Add("sty_line", "直线 (无箭头)", delegate { curArrowStyle = Annot.S_LINE; MarkProp(); }).ToolKey = "sty_line";
            propBar.Add("sty_callout", "标注线 (两端竖杠)", delegate { curArrowStyle = Annot.S_CALLOUT; MarkProp(); }).ToolKey = "sty_callout";
            propBar.AddSep();
            propBar.Add("w_thin", "细线", delegate { curWidth = 2f; MarkProp(); }).ToolKey = "w_thin";
            propBar.Add("w_mid", "中线", delegate { curWidth = 3.5f; MarkProp(); }).ToolKey = "w_mid";
            propBar.Add("w_bold", "粗线", delegate { curWidth = 6f; MarkProp(); }).ToolKey = "w_bold";
            propBar.AddSep();
            fontBtn = propBar.AddText(curFontPt.ToString(), "字号 (文字/序号)", delegate { ShowFontMenu(); });
            fontBtn.ToolKey = "f_size";
            familyBtn = propBar.AddText(FamilyShort(curFontFamily), "字体 (文字/序号)", delegate { ShowFamilyMenu(); });
            familyBtn.ToolKey = "f_family";
            propBar.AddSep();
            Color[] cols =
            {
                Color.FromArgb(242, 80, 59),   // 红 (PixPin 默认)
                Color.FromArgb(235, 130, 50),  // 橙
                Color.FromArgb(245, 198, 60),  // 黄
                Color.FromArgb(94, 176, 100),  // 绿
                Color.FromArgb(59, 125, 216),  // 蓝
                Color.FromArgb(150, 150, 150), // 灰
                Color.FromArgb(255, 255, 255), // 白
            };
            string[] cnames = { "红色", "橙色", "黄色", "绿色", "蓝色", "灰色", "白色" };
            for (int i = 0; i < cols.Length; i++)
            {
                Color c = cols[i];
                propBar.AddColor(c, cnames[i], delegate { curColor = c; MarkProp(); }).ToolKey = "col" + i;
            }
            // 序号: 格式 (1.2.3/I.II.III/a.b.c/A.B.C) + 起始数字 (PixPin 同款, 只影响新序号)
            ToolbarPanel.Btn fmtBtn = null;
            fmtBtn = propBar.AddText("1.2.3", "序号格式", delegate
            {
                ContextMenuStrip m = DarkMenu();
                string[][] fmts = { new string[] { "1", "1.2.3" }, new string[] { "I", "I.II.III" }, new string[] { "a", "a.b.c" }, new string[] { "A", "A.B.C" } };
                foreach (string[] fm in fmts)
                {
                    string f = fm[0], label = fm[1];
                    ToolStripItem it = m.Items.Add(label, null, delegate
                    {
                        curSeqFmt = f;
                        if (fmtBtn != null) { fmtBtn.DrawStr = label; propBar.Invalidate(); }
                    });
                    if (f == curSeqFmt) it.Font = new Font(it.Font, FontStyle.Bold);
                }
                m.Show(propBar, fmtBtn.Rect.X, fmtBtn.Rect.Bottom + 2);
            });
            fmtBtn.ToolKey = "seqfmt";
            ToolbarPanel.Btn startBtn = null;
            startBtn = propBar.AddText("从" + seqNext, "起始数字", delegate
            {
                ContextMenuStrip m = DarkMenu();
                for (int v = 1; v <= 20; v++)
                {
                    int captured = v;
                    ToolStripItem it = m.Items.Add(v.ToString(), null, delegate
                    {
                        seqNext = captured;
                        if (startBtn != null) { startBtn.DrawStr = "从" + captured; propBar.Invalidate(); }
                        Log("capture: seq start=" + captured);
                    });
                    if (v == seqNext) it.Font = new Font(it.Font, FontStyle.Bold);
                }
                m.Show(propBar, startBtn.Rect.X, startBtn.Rect.Bottom + 2);
            });
            startBtn.ToolKey = "seqstart";
            Controls.Add(propBar);
            propBar.Visible = false;
        }

        void MarkProp()
        {
            foreach (var b in propBar.Btns)
            {
                if (b.ToolKey == null) continue;
                if (b.ToolKey.StartsWith("sty")) b.On = (b.ToolKey == "sty_arrow" && curArrowStyle == Annot.S_ARROW) ||
                                                        (b.ToolKey == "sty_both" && curArrowStyle == Annot.S_BOTH) ||
                                                        (b.ToolKey == "sty_line" && curArrowStyle == Annot.S_LINE) ||
                                                        (b.ToolKey == "sty_callout" && curArrowStyle == Annot.S_CALLOUT);
                else if (b.ToolKey.StartsWith("w_")) b.On = (b.ToolKey == "w_thin" && curWidth <= 2.5f) ||
                                                            (b.ToolKey == "w_mid" && curWidth > 2.5f && curWidth <= 4.5f) ||
                                                            (b.ToolKey == "w_bold" && curWidth > 4.5f);

                else if (b.ToolKey.StartsWith("col")) b.On = b.Swatch == curColor;
            }
            propBar.Invalidate();
        }

        void PlacePropBar()
        {
            if (propBar == null || bar == null) return;
            Rectangle vs = SystemInformation.VirtualScreen;
            int x = bar.Left, y = bar.Bottom + 4;
            if (x + propBar.Width > vs.Right - 4) x = vs.Right - propBar.Width - 4;
            if (y + propBar.Height > vs.Bottom - 4) y = bar.Top - propBar.Height - 4;
            if (y < vs.Top + 4) y = vs.Top + 4;
            Point c = RectangleToClient(new Rectangle(new Point(x, y), Size.Empty)).Location;
            propBar.Left = c.X; propBar.Top = c.Y;
        }

        ToolbarPanel.Btn fontBtn, familyBtn;
        static string FamilyShort(string fam)
        {
            if (fam == "Microsoft YaHei UI") return "雅黑";
            if (fam == "SimSun") return "宋体";
            if (fam == "SimHei") return "黑体";
            if (fam == "KaiTi") return "楷体";
            if (fam == "Consolas") return "代码";
            return fam.Length > 3 ? fam.Substring(0, 3) : fam;
        }

        void ShowFontMenu()
        {
            ContextMenuStrip m = DarkMenu();
            foreach (int pt in new int[] { 11, 14, 18, 22, 26 })
            {
                int captured = pt;
                ToolStripItem it = m.Items.Add(pt.ToString(), null, delegate
                {
                    curFontPt = captured;
                    if (fontBtn != null) { fontBtn.DrawStr = captured.ToString(); propBar.Invalidate(); }
                    Log("capture: font pt=" + captured);
                });
                if (pt == curFontPt) it.Font = new Font(it.Font, FontStyle.Bold);
            }
            m.Show(propBar, fontBtn.Rect.X, fontBtn.Rect.Bottom + 2);
        }

        void ShowFamilyMenu()
        {
            ContextMenuStrip m = DarkMenu();
            string[][] fams =
            {
                new string[] { "Microsoft YaHei UI", "雅黑" },
                new string[] { "SimSun", "宋体" },
                new string[] { "SimHei", "黑体" },
                new string[] { "KaiTi", "楷体" },
                new string[] { "Consolas", "代码(等宽)" },
            };
            foreach (string[] fm in fams)
            {
                string fam = fm[0], label = fm[1];
                ToolStripItem it = m.Items.Add(label, null, delegate
                {
                    curFontFamily = fam;
                    if (familyBtn != null) { familyBtn.DrawStr = FamilyShort(fam); propBar.Invalidate(); }
                    Log("capture: font family=" + fam);
                });
                if (fam == curFontFamily) it.Font = new Font(it.Font, FontStyle.Bold);
            }
            m.Show(propBar, familyBtn.Rect.X, familyBtn.Rect.Bottom + 2);
        }

        static ContextMenuStrip DarkMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.BackColor = Color.FromArgb(40, 41, 46);
            m.ForeColor = DarkUI.Text;
            m.ShowImageMargin = false;
            return m;
        }

        void HidePropBar() { if (propBar != null) propBar.Visible = false; }

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

        void HideBar() { if (bar != null) bar.Visible = false; HidePropBar(); }

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

        // ---- 录屏 (照 PixPin: 二级菜单选延迟; 录选区或全屏; 录制中 HUD 计时+停止) ----
        void ShowRecMenu()
        {
            CommitTextInput();
            ContextMenuStrip m = DarkMenu();
            if (recording)
            {
                m.Items.Add("⏹ 停止录制", null, delegate { RecordStopAndNotify(); });
            }
            else
            {
                m.Items.Add("● 录制选区 (立即)", null, delegate { StartDelayedRecording(0, false); });
                m.Items.Add("● 录制全屏 (立即)", null, delegate { StartDelayedRecording(0, true); });
                m.Items.Add(new ToolStripSeparator());
                int[] delays = { 1, 2, 3, 5, 10 };
                foreach (int dsec in delays)
                {
                    int captured = dsec;
                    m.Items.Add("-" + dsec + "s 后录制选区", null, delegate { StartDelayedRecording(captured, false); });
                }
            }
            m.Show(bar, 8, bar.Bottom - bar.Top + 2);
        }

        // 延迟后录: 倒计时期间遮罩关闭让屏幕可用; 结束后 HUD 计时
        void StartDelayedRecording(int delaySec, bool fullScreen)
        {
            CommitTextInput();
            Rectangle area = fullScreen ? SystemInformation.VirtualScreen : sel;
            Close(); // 录屏开始前遮罩必须关 (录的是用户看到的屏幕)
            if (delaySec > 0) ShowTrayInfo(delaySec + " 秒后开始录制" + (fullScreen ? "全屏" : "选区") + "...");
            Task.Run(() =>
            {
                // ⚠️ 后台线程异常会直接弹 .NET 崩溃框(ThreadException 只兜 UI 线程) — 全包
                try
                {
                    if (delaySec > 0) System.Threading.Thread.Sleep(delaySec * 1000);
                    string r = RecordStart(area.X, area.Y, area.Width, area.Height, 10);
                    Log("record via toolbar: " + r);
                    bool ok = r.Contains("\"ok\":true");
                    RunOnHk(() => { if (ok) ShowRecordHud(); else ShowTrayInfo("录屏启动失败: " + r); });
                }
                catch (Exception ex)
                {
                    Log("record task err: " + ex.Message);
                    RunOnHk(() => ShowTrayInfo("录屏异常: " + ex.Message));
                }
            });
        }

        void RecordStopAndNotify()
        {
            RunOnHk(() =>
            {
                string r = RecordStop();
                CloseRecordHud();
                Log("record stop via toolbar: " + r);
            string file = "";
            int p1 = r.IndexOf("\"file\":\"");
            if (p1 >= 0)
            {
                int p2 = r.IndexOf("\"", p1 + 9);
                file = r.Substring(p1 + 9, p2 - p1 - 9).Replace("\\", "/");
            }
                ShowTrayInfo("录屏已保存: " + file);
            });
        }

        // ---- 录制 HUD: 右下角小浮条 [● REC 00:12] [⏹ 停止] ----
        static Form recHud;
        static Timer hudTimer;

        void ShowRecordHud()
        {
            CloseRecordHud();
            Form hud = new Form();
            recHud = hud;
            hud.FormBorderStyle = FormBorderStyle.None;
            hud.StartPosition = FormStartPosition.Manual;
            hud.Size = new Size(190, 44);
            Rectangle vs = SystemInformation.VirtualScreen;
            hud.Location = new Point(vs.Right - 220, vs.Bottom - 80);
            hud.TopMost = true;
            hud.ShowInTaskbar = false;
            hud.BackColor = Color.FromArgb(30, 31, 36);
            hud.MaximizeBox = false; hud.MinimizeBox = false;
            hud.ShowInTaskbar = false;

            Label rec = new Label(); rec.Text = "● REC 00:00"; rec.Left = 12; rec.Top = 10; rec.AutoSize = true;
            rec.ForeColor = Color.FromArgb(255, 90, 80); rec.Font = new Font("Consolas", 11f, FontStyle.Bold);
            hud.Controls.Add(rec);
            Button stop = new Button(); stop.Text = "⏹ 停止"; stop.FlatStyle = FlatStyle.Flat; stop.FlatAppearance.BorderSize = 0;
            stop.BackColor = Color.FromArgb(200, 60, 60); stop.ForeColor = Color.White;
            stop.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold); stop.Cursor = Cursors.Hand;
            stop.SetBounds(128, 6, 54, 32);
            stop.Click += delegate { RecordStopAndNotify(); };
            hud.Controls.Add(stop);
            stop.BringToFront();

            DateTime t0 = DateTime.Now;
            Timer tm = new Timer { Interval = 500 };
            tm.Tick += (s, e) =>
            {
                try
                {
                    var el = DateTime.Now - t0;
                    rec.Text = "● REC " + ((int)el.TotalSeconds / 60).ToString("00") + ":" + ((int)el.TotalSeconds % 60).ToString("00");
                }
                catch { }
            };
            tm.Start();
            hud.FormClosed += (s, e) => { try { tm.Stop(); tm.Dispose(); } catch { } if (recHud == hud) recHud = null; };
            hud.Show();
        }

        static void CloseRecordHud()
        {
            RunOnHk(() =>
            {
                Form h = recHud;
                if (h != null && !h.IsDisposed) { try { h.Close(); } catch { } recHud = null; }
            });
        }

        // 回 hk UI 线程执行 (WinForms 窗体/控件只能在创建线程访问; 后台线程异常直接弹崩溃框)
        static void RunOnHk(Action a)
        {
            try
            {
                Form hk = hkForm;
                if (hk != null && hk.IsHandleCreated) hk.BeginInvoke(new MethodInvoker(delegate { try { a(); } catch (Exception ex) { Log("hk ui err: " + ex.Message); } }));
                else { try { a(); } catch (Exception ex) { Log("hk ui fallback err: " + ex.Message); } }
            }
            catch (Exception ex) { Log("runonhk err: " + ex.Message); }
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
                    // 遮罩与标注保持 (用户要求: OCR 后继续截图操作), 结果浮窗显示
                    if (!empty) { try { Clipboard.SetText(text); } catch { } new ResultForm("OCR 识别结果", text).Show(); }
                    else ShowTrayInfo("未识别到文字");
                });
            });
        }

        void ActTranslate()
        {
            CommitTextInput();
            SetBusy("识别+翻译中...");
            Bitmap bmp = CropTaken();
            Task.Run(async () =>
            {
                string ocr = null, err = null;
                try
                {
                    ocr = await OcrProvider().RecognizeAsync(bmp);
                    if (string.IsNullOrWhiteSpace(ocr)) err = "未识别到文字, 无法翻译";
                }
                catch (Exception ex) { err = ex.Message; }
                finally { try { bmp.Dispose(); } catch { } }
                string got = ocr, e2 = err;
                BeginOnUi(() =>
                {
                    SetBusy(null);
                    if (e2 != null) { Log("translate err: " + e2); ShowTrayInfo("翻译失败: " + e2); return; }
                    // 自动语言检测 -> 反向翻译; 混合则让用户选
                    string lang = DetectLanguage(got);
                    if (lang == "mixed") ShowLanguagePick(t => RunTranslation(got, t));
                    else RunTranslation(got, (lang == "zh") ? "en" : "zh");
                });
            });
        }

        void RunTranslation(string ocr, string target)
        {
            Close(); // OCR 文本已拿到, 翻译后台跑; 结果用浮动面板, 不叠全屏遮罩
            string srcText = ocr, toLang = target;
            Task.Run(async () =>
            {
                string tr = null, err = null;
                try { tr = await TranslateProvider().TranslateAsync(srcText, toLang); }
                catch (Exception ex) { err = ex.Message; }
                string res = tr, ee = err;
                BeginOnUi(() =>
                {
                    SetBusy(null);
                    if (ee != null) { Log("translate err: " + ee); ShowTrayInfo("翻译失败: " + ee); return; }
                    Log("capture: translate ok target=" + toLang);
                    if (string.IsNullOrWhiteSpace(res)) { ShowTrayInfo("翻译结果为空"); return; }
                    new ResultForm("翻译结果", res).Show(); // 非模态浮动面板
                });
            });
        }

        // 混合语言: 小菜单挑目标语言 (非模态)
        void ShowLanguagePick(Action<string> onPick)
        {
            ContextMenuStrip m = DarkMenu();
            m.Items.Add("翻译为中文", null, (s, ee) => { m.Dispose(); onPick("zh"); });
            m.Items.Add("翻译为英文", null, (s, ee) => { m.Dispose(); onPick("en"); });
            m.Show(Cursor.Position);
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
                if (textMode) { CancelTextInput(); return true; }
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


    // ---- 自绘图标 (白色线性; 矢量画在 18 坐标系再缩放到目标尺寸, 零图片依赖) ----
    static Bitmap MakeIcon(string kind) { return MakeIcon(kind, 22); }
    // Segoe MDL2 Assets 字形 (Windows 自带, 比 GDI 手画精致): 码点经字形网格渲染确认
    static readonly Dictionary<string, string> FontGlyphs = new Dictionary<string, string>
    {
        { "rect", "E71A" }, { "arrow", "E72A" }, { "pen", "E70F" }, { "seq", "E762" },
        { "pin", "E840" }, { "undo", "E7A7" }, { "redo", "E7A6" }, { "translate", "E8C1" },
        { "ok", "E73E" }, { "cancel", "E711" },
    };

    static Bitmap MakeIcon(string kind, int size)
    {
        Bitmap bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (FontGlyphs.ContainsKey(kind))
            {
                string s = char.ConvertFromUtf32(Convert.ToInt32(FontGlyphs[kind], 16));
                using (Font f = new Font("Segoe MDL2 Assets", size * 0.5f))
                    TextRenderer.DrawText(g, s, f, new Rectangle(0, 0, size, size), Color.FromArgb(232, 234, 240),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return bmp;
            }
            g.ScaleTransform(size / 18f, size / 18f); // 18 坐标系统一布局, 按需放大
            using (Pen w = new Pen(Color.FromArgb(232, 234, 240), 2f))
            {
                w.StartCap = LineCap.Round; w.EndCap = LineCap.Round; w.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case "ellipse": g.DrawEllipse(w, 2.5f, 4f, 13, 10); break;
                    case "text":
                        g.DrawLine(w, 4, 4, 14, 4);
                        g.DrawLine(w, 9, 4, 9, 15);
                        break;
                    case "ocr": // 扫描框 + T
                        g.DrawLine(w, 1.5f, 5.5f, 1.5f, 1.5f); g.DrawLine(w, 1.5f, 1.5f, 5.5f, 1.5f);
                        g.DrawLine(w, 16.5f, 5.5f, 16.5f, 1.5f); g.DrawLine(w, 16.5f, 1.5f, 12.5f, 1.5f);
                        g.DrawLine(w, 1.5f, 12.5f, 1.5f, 16.5f); g.DrawLine(w, 1.5f, 16.5f, 5.5f, 16.5f);
                        g.DrawLine(w, 16.5f, 12.5f, 16.5f, 16.5f); g.DrawLine(w, 16.5f, 16.5f, 12.5f, 16.5f);
                        g.DrawLine(w, 6.5f, 6, 11.5f, 6);
                        g.DrawLine(w, 9, 6, 9, 13);
                        break;
                    case "save": // 纯软盘 (无箭头, 用户要求)
                        g.DrawRectangle(w, 2.5f, 2.5f, 13, 13);
                        g.DrawRectangle(w, 6, 3.5f, 6, 4);
                        g.DrawLine(w, 5, 15.5f, 5, 10.5f);
                        g.DrawLine(w, 5, 10.5f, 13, 10.5f);
                        g.DrawLine(w, 13, 10.5f, 13, 15.5f);
                        break;
                    case "rec": // 红圆点 (录屏)
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 82, 70)))
                            g.FillEllipse(b, 4, 4, 10, 10);
                        g.DrawEllipse(w, 4, 4, 10, 10);
                        break;
                    case "sty_arrow": // 实线箭头预览
                        g.DrawLine(w, 2, 9, 14, 9);
                        using (SolidBrush b = new SolidBrush(w.Color))
                            g.FillPolygon(b, new PointF[] { new PointF(16.5f, 9f), new PointF(11.5f, 6f), new PointF(11.5f, 12f) });
                        break;
                    case "sty_both": // 双向
                        g.DrawLine(w, 4, 9, 14, 9);
                        using (SolidBrush b = new SolidBrush(w.Color))
                        {
                            g.FillPolygon(b, new PointF[] { new PointF(1.5f, 9f), new PointF(6.5f, 6f), new PointF(6.5f, 12f) });
                            g.FillPolygon(b, new PointF[] { new PointF(16.5f, 9f), new PointF(11.5f, 6f), new PointF(11.5f, 12f) });
                        }
                        break;
                    case "sty_line": // 直线
                        g.DrawLine(w, 2, 9, 16, 9);
                        break;
                    case "sty_callout": // 标注线 (两端竖杠)
                        g.DrawLine(w, 4, 9, 14, 9);
                        g.DrawLine(w, 4, 4.5f, 4, 13.5f);
                        g.DrawLine(w, 14, 4.5f, 14, 13.5f);
                        break;
                    case "w_thin": // 细
                        using (Pen p2 = new Pen(w.Color, 1.3f)) g.DrawLine(p2, 2, 9, 16, 9);
                        break;
                    case "w_mid": // 中
                        using (Pen p2 = new Pen(w.Color, 3f)) g.DrawLine(p2, 2, 9, 16, 9);
                        break;
                    case "w_bold": // 粗
                        using (Pen p2 = new Pen(w.Color, 5.5f)) g.DrawLine(p2, 2, 9, 16, 9);
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
            public Color Swatch = Color.Empty; // 颜色圆点按钮 (非空时画色块而非图标)
            public string DrawStr;             // 文字按钮 (非空时画文字而非图标, 如字号/字体当前值)
        }

        public Btn AddText(string str, string tipText, Action onClick)
        {
            Btn b = new Btn { Icon = "#text", DrawStr = str, Tip = tipText, OnClick = onClick };
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        public Btn AddColor(Color c, string tipText, Action onClick)
        {
            Btn b = new Btn { Icon = "color", Tip = tipText, OnClick = onClick, Swatch = c };
            Btns.Add(b); Relayout(); Invalidate(); return b;
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
            int x = 6;
            foreach (Btn b in Btns)
            {
                if (b.Icon == "|") { b.Rect = new Rectangle(x, 12, 1, 24); x += 13; }
                else { b.Rect = new Rectangle(x, 4, 40, 40); x += 40; }
            }
            Width = x + 6; Height = 48;
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
                    using (SolidBrush br = new SolidBrush(b.On ? Color.FromArgb(58, 62, 74) : (i == hoverIdx ? Color.FromArgb(64, 68, 80) : Color.Transparent)))
                        RoundFill(g, br, b.Rect, 8);
                    if (b.On && b.Swatch != Color.Empty)
                    { // 色块选中: 白圈描边 (主色高亮底会吃掉色块对比)
                        using (Pen ring = new Pen(Color.FromArgb(235, 238, 244), 2f))
                            g.DrawEllipse(ring, b.Rect.X + 7, b.Rect.Y + 7, 26, 26);
                    }
                }
                if (b.Swatch != Color.Empty)
                {
                    using (SolidBrush sb = new SolidBrush(b.Swatch))
                        g.FillEllipse(sb, b.Rect.X + 10, b.Rect.Y + 10, 20, 20);
                    if (!b.Enabled)
                        using (SolidBrush dim = new SolidBrush(Color.FromArgb(140, 26, 27, 31)))
                            g.FillEllipse(dim, b.Rect.X + 10, b.Rect.Y + 10, 20, 20);
                    continue;
                }
                if (b.DrawStr != null)
                {
                    TextRenderer.DrawText(g, b.DrawStr, new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold), b.Rect,
                                          b.Enabled ? Color.FromArgb(232, 234, 240) : Color.FromArgb(110, 114, 120),
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    continue;
                }
                using (Bitmap ic = MakeIcon(b.Icon, 24))
                {
                    if (!b.Enabled)
                    {
                        System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f };
                        using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            ia.SetColorMatrix(cm);
                            g.DrawImage(ic, new Rectangle(b.Rect.X + 8, b.Rect.Y + 8, 24, 24), 0, 0, 24, 24, GraphicsUnit.Pixel, ia);
                        }
                    }
                    else
                        g.DrawImage(ic, b.Rect.X + 8, b.Rect.Y + 8, 24, 24);
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
                    tip.Show(shownTip, this, Btns[h].Rect.X, Btns[h].Rect.Y - 28, 1400); // 按钮正上方
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
            Log("bar click at " + e.Location + " hit=" + h + " icon=" + (h >= 0 ? Btns[h].Icon : "-") + " barL=" + Left + " barT=" + Top);
            if (e.Button == MouseButtons.Left && h >= 0 && h == hoverIdx)
            {
                Btn b = Btns[h];
                if (b.Enabled && b.OnClick != null) b.OnClick();
            }
        }
    }

    // 语言检测: 按 CJK 占比判 zh/en/mixed —— 点翻译自动反向翻译 (中文→英, 英文→中), 混合让用户选
    static string SeqText(int n, string fmt)
    {
        if (fmt == "I") // 罗马大写
        {
            string[] R = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX", "XXI", "XXII", "XXIII", "XXIV", "XXV", "XXVI", "XXVII", "XXVIII", "XXIX", "XXX" };
            return (n >= 1 && n <= 30) ? R[n] : n.ToString();
        }
        if (fmt == "a") return n >= 1 && n <= 26 ? ((char)('a' + n - 1)).ToString() : n.ToString();
        if (fmt == "A") return n >= 1 && n <= 26 ? ((char)('A' + n - 1)).ToString() : n.ToString();
        return n.ToString(); // "1"
    }

    static string DetectLanguage(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "en";
        int cjk = 0, latin = 0;
        foreach (char c in s)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            else if (char.IsLetter(c)) latin++;
        }
        int total = cjk + latin;
        if (total == 0) return "en";
        double zhRatio = (double)cjk / total;
        if (zhRatio >= 0.6) return "zh";
        if (zhRatio <= 0.15) return "en";
        return "mixed";
    }
}

    // ---- 深色 UI 主题 (全窗体统一色板; 设置窗/剪贴板历史窗/结果窗/贴图窗共用) ----
    static class DarkUI
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
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
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
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


    // M2: OCR/翻译结果浮动面板 (非模态: Show() 直接浮在屏幕, 不阻塞不弹框; 可拖动/复制/Esc关)
    class ResultForm : Form
    {
        readonly string text;
        readonly Button copyBtn;

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // 阴影
        }

        public ResultForm(string title, string text)
        {
            this.text = text;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(560, 320);
            TopMost = true;
            BackColor = DarkUI.Bg;
            KeyPreview = true;

            DarkUI.MakeTitleBar(this, title);

            TextBox tb = new TextBox();
            tb.Multiline = true; tb.ReadOnly = true; tb.ScrollBars = ScrollBars.Vertical;
            tb.Dock = DockStyle.Fill;
            tb.BackColor = DarkUI.BgField; tb.ForeColor = DarkUI.Text; tb.BorderStyle = BorderStyle.None;
            tb.Font = new Font("Microsoft YaHei UI", 11f);
            tb.Text = text;
            tb.Margin = new Padding(10);
            tb.Padding = new Padding(10);
            tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.C && e.Control) { CopyNow(); e.SuppressKeyPress = true; } };
            Controls.Add(tb);
            tb.BringToFront();

            Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 48; bottom.BackColor = DarkUI.BgPanel;
            Label info = new Label(); info.Text = "已自动复制到剪贴板 · Ctrl+C 再复制 · Esc 关闭"; info.Left = 14;
            info.AutoSize = true; info.ForeColor = DarkUI.TextDim; info.Font = new Font("Microsoft YaHei UI", 9f);
            info.Dock = DockStyle.Fill;
            copyBtn = new Button(); copyBtn.Text = "复制"; copyBtn.FlatStyle = FlatStyle.Flat; copyBtn.FlatAppearance.BorderSize = 0;
            copyBtn.BackColor = DarkUI.Accent; copyBtn.ForeColor = DarkUI.Text;
            copyBtn.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            copyBtn.Size = new Size(110, 34); copyBtn.Cursor = Cursors.Hand;
            copyBtn.Dock = DockStyle.Right;
            copyBtn.Click += (s, e) => CopyNow();
            bottom.Controls.Add(info); bottom.Controls.Add(copyBtn);
            Controls.Add(bottom);
            bottom.BringToFront();
            copyBtn.BringToFront();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            // 打开即自动复制 (PixPin 语义: 翻译/识别结果直接可用)
            CopyNow();
        }

        void CopyNow()
        {
            try { Clipboard.SetText(text); } catch { }
            copyBtn.Text = "✓ 已复制";
            copyBtn.BackColor = Color.FromArgb(40, 130, 80);
        }
    }
