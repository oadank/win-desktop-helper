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

    // ==================== 新保存管线 (平搬自 WinControl wc-shot.cs 4.3; 默认名沿用 shot_ 前缀, 不加 PixPin) ====================
    // save.saveTypes 位掩码 → 7 种扩展名 (bit0..bit6 = png,jpg,jpeg,bmp,gif,tif,webp)
    static readonly string[] SaveTypeExts = { "png", "jpg", "jpeg", "bmp", "gif", "tif", "webp" };

    static System.Drawing.Imaging.ImageFormat FormatForExt(string ext)
    {
        switch (ext)
        {
            case "jpg":
            case "jpeg": return System.Drawing.Imaging.ImageFormat.Jpeg;
            case "bmp": return System.Drawing.Imaging.ImageFormat.Bmp;
            case "gif": return System.Drawing.Imaging.ImageFormat.Gif;
            case "tif":
            case "tiff": return System.Drawing.Imaging.ImageFormat.Tiff;
            case "webp": return System.Drawing.Imaging.ImageFormat.Png; // GDI+ 无 WebP 编码器, 降级 PNG
            case "png":
            default: return System.Drawing.Imaging.ImageFormat.Png;
        }
    }

    // 文件名模板展开: $...$ 段按 DateTime.ToString 格式展开
    static string ExpandSaveTemplate(string tmpl, DateTime now)
    {
        if (string.IsNullOrEmpty(tmpl)) tmpl = "shot_$yyyy-MM-dd_HH-mm-ss-fff$.png";
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < tmpl.Length)
        {
            int s = tmpl.IndexOf('$', i);
            if (s < 0) { sb.Append(tmpl.Substring(i)); break; }
            int e = tmpl.IndexOf('$', s + 1);
            if (e < 0) { sb.Append(tmpl.Substring(i)); break; }
            sb.Append(tmpl.Substring(i, s - i));
            string fmt = tmpl.Substring(s + 1, e - s - 1);
            try { sb.Append(now.ToString(fmt)); }
            catch { sb.Append(fmt); }
            i = e + 1;
        }
        return sb.ToString();
    }

    static string SaveTypeExt()
    {
        int types;
        if (!int.TryParse(Cfg("save.saveTypes", "127"), out types)) types = 127;
        for (int i = 0; i < SaveTypeExts.Length; i++)
            if ((types & (1 << i)) != 0) return SaveTypeExts[i];
        return "png";
    }

    // 冲突策略 save.whenConflict: 0=自动重命名绝不覆盖 (默认), 1=覆盖
    static string ResolveSaveConflict(string path)
    {
        int wc;
        if (!int.TryParse(Cfg("save.whenConflict", "0"), out wc)) wc = 0;
        if (wc == 1) return path;
        if (!System.IO.File.Exists(path)) return path;
        string dir = System.IO.Path.GetDirectoryName(path), name = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        for (int n = 1; n < 1000; n++)
        {
            string cand = System.IO.Path.Combine(dir, name + "-" + n + ext);
            if (!System.IO.File.Exists(cand)) return cand;
        }
        return System.IO.Path.Combine(dir, name + "-" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
    }

    static DateTime lastShotCleanup = DateTime.MinValue;
    const int ShotCleanupThrottleMinutes = 5;
    static bool shotCleanupLoopStarted = false;

    static void ShotCleanupLoop()
    {
        while (true)
        {
            try
            {
                DateTime now = DateTime.Now;
                DateTime next = now.Date.AddDays(1).AddMinutes(5);
                double ms = (next - now).TotalMilliseconds;
                System.Threading.Thread.Sleep(ms > 0 && ms < int.MaxValue ? (int)ms : 60000);
                if (Cfg("save.autoCleanupEnabled", "true") != "true") continue;
                RunShotCleanup("daily-0005");
            }
            catch (System.Threading.ThreadInterruptedException) { }
            catch (Exception ex) { Log("shot cleanup loop err: " + ex.Message); try { System.Threading.Thread.Sleep(60000); } catch { } }
        }
    }

    static void RunShotCleanup(string trigger)
    {
        try
        {
            lastShotCleanup = DateTime.Now;
            int days;
            if (!int.TryParse(Cfg("save.cleanupDays", "2"), out days)) days = 2;
            if (days < 0) days = 0;
            int types;
            if (!int.TryParse(Cfg("save.saveTypes", "127"), out types)) types = 127;
            if (!System.IO.Directory.Exists(ShotDir)) return;
            // 只清理与当前保存模板同前缀的截图文件, 绝不误删 shot 目录里其它东西
            string tmpl = Cfg("save.filename", "shot_$yyyy-MM-dd_HH-mm-ss-fff$.png");
            int dpos = tmpl.IndexOf('$');
            string prefix = dpos > 0 ? tmpl.Substring(0, dpos) : "shot_";
            DateTime cutoff = DateTime.Now.AddDays(-days);
            int deleted = 0; long freed = 0;
            foreach (string f in System.IO.Directory.GetFiles(ShotDir))
            {
                try
                {
                    string fn = System.IO.Path.GetFileName(f);
                    if (!fn.StartsWith(prefix)) continue; // 只删自己生成的截图
                    DateTime age = System.IO.File.GetLastWriteTime(f);
                    if (age >= cutoff) continue;
                    long sz = new System.IO.FileInfo(f).Length;
                    System.IO.File.Delete(f);
                    deleted++; freed += sz;
                    Log("shot cleanup delete: " + f + " (" + sz + " bytes)");
                }
                catch (Exception ex1) { Log("shot cleanup file err: " + f + " " + ex1.Message); }
            }
            Log("shot cleanup done (" + trigger + "): deleted=" + deleted + " freed=" + freed + " dir=" + ShotDir + " days=" + days);
        }
        catch (Exception ex) { Log("shot cleanup err: " + ex.Message); }
    }

    static void ShotCleanupAfterSave()
    {
        try
        {
            if (!shotCleanupLoopStarted) { shotCleanupLoopStarted = true; System.Threading.Thread t = new System.Threading.Thread(ShotCleanupLoop); t.IsBackground = true; t.Start(); }
            if (Cfg("save.autoCleanupEnabled", "true") != "true") return;
            if ((DateTime.Now - lastShotCleanup).TotalMinutes < ShotCleanupThrottleMinutes) return;
            RunShotCleanup("after-save");
        }
        catch (Exception ex) { Log("shot cleanup throttle err: " + ex.Message); }
    }

    // 保存管线核心: 建目录 → 模板命名 → 冲突处理 → 落盘 → 自动清理
    static string SaveImagePipelined(Bitmap bmp)
    {
        try { if (!System.IO.Directory.Exists(ShotDir)) System.IO.Directory.CreateDirectory(ShotDir); } catch { }
        string ext = SaveTypeExt();
        string name = ExpandSaveTemplate(Cfg("save.filename", "shot_$yyyy-MM-dd_HH-mm-ss-fff$.png"), DateTime.Now);
        if (string.IsNullOrEmpty(System.IO.Path.GetExtension(name))) name = name + "." + ext;
        string path = ResolveSaveConflict(System.IO.Path.Combine(ShotDir, name));
        bmp.Save(path, FormatForExt(ext));
        Log("shot saved: " + path + " (saveTypes ext=" + ext + ")");
        ShotCleanupAfterSave();
        return path;
    }

    // 保存截图到 ShotDir (类级: 遮罩工具栏共用) — 换新管线, 签名不变
    static string SaveToShotDir(Bitmap bmp)
    {
        return SaveImagePipelined(bmp);
    }

    // ---- 标注数据模型 (PixPin 基础款: 矩形/椭圆/箭头/画笔/文字/序号; 一条记录一个笔画) ----
    class Annot
    {
        public const int K_RECT = 0, K_ELLIPSE = 1, K_ARROW = 2, K_PEN = 3, K_TEXT = 4, K_SEQ = 5;
        public const int K_POLY = 6;  // 折线/曲线 (PixPin BrokenLine: 直线段/平滑曲线 同工具两形态, 单击加点+双击/回车收尾)
        // 马赛克 / 荧光笔 (PixPin EditItemCustom + MarkBar.Mosaic.* 配置键实证)
        public const int K_MOSAIC = 7;    // 马赛克: 拖矩形打码 (PixPin MarkBar.Mosaic.MosaicStrength = 块强度)
        public const int K_HIGHLIGHT = 8; // 荧光笔: 拖动高亮 (PixPin 默认正片叠底, 深色背景可用半透明)
        public const int M_NORMAL = 0, M_MULTIPLY = 1; // 荧光笔混合模式: 0=半透明 1=正片叠底(PixPin 默认)
        // 箭头样式 (PixPin 同款)
        public const int S_ARROW = 0, S_BOTH = 1, S_LINE = 2, S_CALLOUT = 3;
        public const int S_THIN = 4, S_HOLLOW = 5; // 细尾渐变实心 / 细尾空心 (老版保留样式, 用户点名不许丢)
        public int Kind;
        public int Style = S_ARROW;  // 箭头样式 (旧 6 样式兼容; 新模型用下方头型字段)
        // PixPin 头型复刻 (证据级, 逆向自 GenArrowHeadContour, 取值 0-12):
        //   0=无 1=开口 2=实心三角(大) 3=实心三角(小) 4=实心圆 5=空心圆 6=实心菱形 7=空心菱形 8=刻度 9=十字 10=开口(细) 11=尾三角 12=五角
        public int StartType = 0;    // 起点箭头型 (0=无)
        public int EndType = 2;      // 终点箭头型 (默认 2=实心三角, 同 PixPin 默认)
        public bool Smooth = false;   // 平滑曲线 (PixPin FUN_180082b70)
        public int Dash = 0;         // 虚线: 0=实线 2/3/4/5 (见 PixPinArrowGeom.SetDash)
        public bool Fill = false;    // 折线闭合填充 (PixPin MarkBar.BrokenLine.Fill: 首尾闭合成多边形纯色填满, 无头型/虚线)
        public int MosaicStrength = 8; // 马赛克块强度 (PixPin MarkBar.Mosaic.MosaicStrength; 越大块越粗)
        public int HlMode = M_NORMAL; // 荧光笔混合模式 (默认半透明: 多笔重叠自然加深不互相擦; 正片叠底单笔质感好但多笔会覆盖, 待画布架构修)
        public float Width = 3f;     // 线宽
        public int FontPt = 14;      // 文字/序号字号
        public string SeqFmt = "1";  // 序号格式: 1=1.2.3  I=I.II.III  a=a.b.c  A=A.B.C
        public int Angle = 0;        // 文字旋转角 (度, 顺时针)
        public string FontFamily = "Microsoft YaHei UI"; // 文字字体
        public Rectangle Rect;      // rect/ellipse/arrow 的包围盒; seq/text 的定位点在 Rect.Location
        public List<Point> Pts;     // 画笔折线 (屏坐标)
        public string Text;         // 文字内容
        public int No;              // 序号数字
        public Color Color = Color.FromArgb(242, 80, 59); // PixPin 默认红
    }

    // 全屏框选窗 (PixPin/ShareX 同款交互)
    // 自绘深色下拉弹层 (PixPin 同款): #28292E 圆角底, 行悬停/选中=圆角蓝块(#3385FF)左右内缩,
    // 预览直接画在弹层上 (箭头样式复用画布同一 DrawArrowEx — 预览与画布永不漂移, 根治 X2), 无白底/白边/黑顶条/分隔线
    class StylePopup : Form
    {
        public delegate void DrawerDel(Graphics g, RectangleF row, int key, Color ink);
        readonly int[] keys;
        readonly System.Func<int, bool> isCurrent;
        readonly Action<int> onPick;
        readonly DrawerDel drawer;
        readonly System.Func<int, string> tipper;
        readonly Color ink;
        int hover = -1;
        readonly ToolTip tip = new ToolTip();
        readonly int RowH, Pad;
        readonly int Rad, RadRow;
        readonly Size designSize;   // 构造时的设计尺寸 (物理像素)
        Point designPos;            // Show 前设定的位置

        // DPI 缩放系数: 本进程 PerMonitorV2 + AutoScaleMode.None → 逻辑尺寸被当物理像素直接渲染,
        // 150% 屏上弹层只有 104px 宽 (PixPin 同款 166px) 显得又小又挤。这里手动按 DPI 放大。
        public static float UiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    return Math.Max(1f, Math.Min(3f, g.DpiX / 96f));
            }
            catch { return 1f; }
        }

        public StylePopup(int[] keys, System.Func<int, bool> isCurrent, Action<int> onPick, DrawerDel drawer, System.Func<int, string> tipper, Color ink)
        {
            this.keys = keys; this.isCurrent = isCurrent; this.onPick = onPick; this.drawer = drawer; this.tipper = tipper; this.ink = ink;
            float s = UiScale();
            RowH = (int)Math.Round(24 * s);   // 行高 (显示 ~30px; 旧 20*s=25px 太挤, 用户实测"挤到一起")
            Pad = (int)Math.Round(6 * s);     // 内边距 (显示 ~7px)
            Rad = (int)Math.Round(8 * s);
            RadRow = (int)Math.Round(6 * s);
            AutoScaleMode = AutoScaleMode.None; // 关自动缩放: 手动算尺寸, 双重缩放会让行位与可见区错位 (蓝块被裁)
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(40, 41, 46);
            DoubleBuffered = true;
            KeyPreview = true;
            designSize = new Size((int)Math.Round(132 * s), keys.Length * RowH + Pad * 2); // 宽 ~164px (PixPin 同款 166; 旧 100*s=124px 太窄)
            Size = designSize;
            using (GraphicsPath gp = RoundPath(0, 0, Width, Height, Rad)) Region = new Region(gp);
            Log(string.Format("StylePopup ctor: s={0:F3} Size={1}x{2} RowH={3} Pad={4}", s, Width, Height, RowH, Pad));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // WinForms PMv2 首次 Show 会按 DPI suggested rect 改写窗口尺寸 (实测 156→202, 高度也可能被动),
            // 尺寸一变 Region(圆角) 和行布局就与设计不符 → 强制拉回设计尺寸并重建 Region
            if (Size != designSize) Size = designSize;
            if (Location != designPos) Location = designPos;
            using (GraphicsPath gp = RoundPath(0, 0, Width, Height, Rad)) Region = new Region(gp);
            Log(string.Format("StylePopup shown: Size={0}x{1} (design {2}x{3}) region rebuilt", Width, Height, designSize.Width, designSize.Height));
        }

        public new void Show()
        {
            designPos = Location;
            // 屏幕边缘钳制: 加高后的弹层(14 项 ~430px)靠近屏底/屏顶会出界, 整体平移回屏内
            try
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                int h = designSize.Height, w = designSize.Width;
                if (designPos.Y + h > vs.Bottom - 4) designPos.Y = vs.Bottom - h - 4;
                if (designPos.Y < vs.Top + 4) designPos.Y = vs.Top + 4;
                if (designPos.X + w > vs.Right - 4) designPos.X = vs.Right - w - 4;
                if (designPos.X < vs.Left + 4) designPos.X = vs.Left + 4;
            }
            catch { }
            base.Show();
        }

        static GraphicsPath RoundPath(int x, int y, int w, int h, int d)
        {
            GraphicsPath gp = new GraphicsPath();
            gp.AddArc(x, y, d, d, 180, 90);
            gp.AddArc(x + w - d, y, d, d, 270, 90);
            gp.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            gp.AddArc(x, y + h - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        RectangleF RowRect(int i) { return new RectangleF(Pad, Pad + i * RowH, Width - Pad * 2, RowH); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(40, 41, 46));
            for (int i = 0; i < keys.Length; i++)
            {
                RectangleF row = RowRect(i);
                bool sel = isCurrent != null && isCurrent(keys[i]);
                if (i == hover || sel)
                {
                    using (SolidBrush hb = new SolidBrush(i == hover ? Color.FromArgb(64, 142, 255) : Color.FromArgb(51, 133, 255)))
                        using (GraphicsPath gp = RoundPath((int)row.X, (int)row.Y, (int)row.Width, (int)row.Height, RadRow))
                            g.FillPath(hb, gp);
                }
                // 传已内缩 Pad 的行矩形: 调用方只需再留 2px 余量, 箭头预览不会顶到弹层边
                try { drawer(g, row, keys[i], ink); } catch { }
            }
        }

        int RowAt(Point pt)
        {
            if (pt.X < Pad || pt.X > Width - Pad) return -1;
            int i = (pt.Y - Pad) / RowH;
            if (i < 0 || i >= keys.Length || pt.Y < Pad || pt.Y > Height - Pad) return -1;
            return i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int r = RowAt(e.Location);
            if (r != hover)
            {
                hover = r;
                Invalidate();
                try { if (r >= 0 && tipper != null) tip.Show(tipper(keys[r]), this, e.X + 12, e.Y + 18, 1500); else tip.Hide(this); } catch { }
            }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); try { tip.Hide(this); } catch { } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int r = RowAt(e.Location);
            if (r >= 0 && onPick != null) onPick(keys[r]);
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            try { StylePopup self = this; BeginInvoke((Action)delegate { if (Form.ActiveForm != self && !self.IsDisposed) self.Close(); }); } catch { }
        }
    }

    class CaptureOverlay : Form
    {
        readonly Bitmap frozen;  // 冻结的全屏原图 (尺寸=虚拟屏)
        Point start;
        Rectangle sel;
        bool dragging = false;
        bool hasSel = false;
        int resizeHandle = -1;   // 拖拽中的选框手柄 0-7 (TL,T,TR,R,BR,B,BL,L); -1=无 — PixPin 同款四角四边拉区域
        Rectangle resizeOrig;    // 拖拽开始时的选区 (固定对边法)
        readonly List<Annot> annots = new List<Annot>(); // 已提交标注 (屏坐标)
        readonly List<Annot> redoStack = new List<Annot>(); // 撤销弹出的标注 (重做用, 新笔画清空)
        Annot cur;               // 正在绘制的标注
        string tool = null;      // 当前标注工具: rect/ellipse/arrow/pen/text/seq; null=未选
        // ---- 标注参数持久化 (PixPin MarkBar 同款): 首次截图前从 shot-service.json mark 节恢复 ----
        static bool markLoaded = false;
        static float markWidth0 = 3f;
        static int markArrowStyle0 = Annot.S_ARROW;
        static Color markColor0 = Color.FromArgb(242, 80, 59);
        static int markFontPt0 = 14;
        static string markSeqFmt0 = "1";
        static string markFontFamily0 = "Microsoft YaHei UI";
        static int markStartType0 = 0;
        static int markEndType0 = 2;
        static bool markSmooth0 = false;
        static int markDash0 = 0;
        static bool markCallout0 = false;
        static bool markFill0 = false;
        static int markShape0 = 0; // 0=PixPin头型 4=细尾实心 5=细尾空心
        static int markMosaicStrength0 = 8; // 马赛克块强度 (PixPin MarkBar.Mosaic.MosaicStrength)
        static CaptureOverlay()
        {
            try
            {
                markLoaded = true;
                int ms2; if (int.TryParse(Cfg("mark.mosaicStrength", ""), out ms2) && ms2 >= 2 && ms2 <= 40) markMosaicStrength0 = ms2;
                float w; if (float.TryParse(Cfg("mark.width", "3"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w) && w >= 1 && w <= 20) markWidth0 = w;
                int st; if (int.TryParse(Cfg("mark.arrowStyle", ""), out st)) markArrowStyle0 = st;
                int ci; if (int.TryParse(Cfg("mark.color", ""), System.Globalization.NumberStyles.Integer, null, out ci)) markColor0 = Color.FromArgb(ci);
                int fp; if (int.TryParse(Cfg("mark.fontPt", ""), out fp) && fp >= 8 && fp <= 72) markFontPt0 = fp;
                string sf = Cfg("mark.seqFmt", "1"); if (sf.Length == 1 && "1IaA".IndexOf(sf) >= 0) markSeqFmt0 = sf;
                // 新字段: 头型/平滑/虚线/标注线 (与旧 mark.arrowStyle 并存, 优先新字段)
                int aet2; if (int.TryParse(Cfg("mark.arrowEnd", ""), out aet2)) markEndType0 = aet2;
                int ast2; if (int.TryParse(Cfg("mark.arrowStart", ""), out ast2)) markStartType0 = ast2;
                bool sm2; if (bool.TryParse(Cfg("mark.smooth", ""), out sm2)) markSmooth0 = sm2;
                int dsh2; if (int.TryParse(Cfg("mark.dash", ""), out dsh2)) markDash0 = dsh2;
                bool co2; if (bool.TryParse(Cfg("mark.callout", ""), out co2)) markCallout0 = co2;
                bool fl2; if (bool.TryParse(Cfg("mark.fill", ""), out fl2)) markFill0 = fl2;
                int shp2; if (int.TryParse(Cfg("mark.shape", ""), out shp2) && (shp2 == 0 || shp2 == 4 || shp2 == 5)) markShape0 = shp2;
                // 旧版只存 mark.arrowStyle(0-5), 新字段缺失时向后兼容映射到 PixPin 头型
                if (string.IsNullOrEmpty(Cfg("mark.arrowEnd", "")) && string.IsNullOrEmpty(Cfg("mark.shape", "")) && !string.IsNullOrEmpty(Cfg("mark.arrowStyle", "")))
                {
                    int st2; if (int.TryParse(Cfg("mark.arrowStyle", ""), out st2))
                    {
                        switch (st2)
                        {
                            case 0: markStartType0 = 0; markEndType0 = 2; break;       // 实线箭头
                            case 1: markStartType0 = 2; markEndType0 = 2; break;       // 双向
                            case 2: markStartType0 = 0; markEndType0 = 0; break;       // 直线
                            case 3: markCallout0 = true; markEndType0 = 2; break;      // 标注线
                            case 4: markShape0 = 4; break;                             // 细尾实心 (原样保留)
                            case 5: markShape0 = 5; break;                             // 细尾空心 (原样保留)
                        }
                    }
                }
                string ff = Cfg("mark.fontFamily", ""); if (ff.Length > 0) markFontFamily0 = ff;
                Log("mark cfg loaded: w=" + markWidth0 + " style=" + markArrowStyle0 + " start=" + markStartType0 + " end=" + markEndType0 + " smooth=" + markSmooth0 + " dash=" + markDash0 + " callout=" + markCallout0 + " color=" + markColor0.ToArgb() + " pt=" + markFontPt0 + " fam=" + markFontFamily0 + " seq=" + markSeqFmt0);
            }
            catch (Exception ex) { Log("mark cfg load err: " + ex.Message); }
        }
        int curArrowStyle = markArrowStyle0;        // 属性栏: 箭头样式 (旧键兼容, 新模型用下方头型字段)
        int curStartType = markStartType0;
        int curEndType = markEndType0;
        bool curSmooth = markSmooth0;
        int curDash = markDash0;
        bool curCallout = markCallout0;
        bool curFill = markFill0;                  // 折线闭合填充开关
        int curMosaicStrength = markMosaicStrength0;               // 马赛克块强度 (PixPin MarkBar.Mosaic.MosaicStrength)
        int curHlMode = Annot.M_NORMAL;            // 荧光笔混合模式 (默认半透明: 重叠不互相擦)
        int curShape = markShape0;                  // 0=PixPin头型 4=细尾实心 5=细尾空心
        Point polyHover = Point.Empty;              // 折线绘制中的橡皮筋落点 (屏坐标)
        bool polyHoverOk = false;
        // ---- 选区识别 (PixPin「配置-截图-UI 检测」同款) ----
        int uiDetect = 0;                           // 0=检测元素(细,默认) 1=仅窗口 2=不检测 (每次开遮罩现读配置, 改完即生效)
        Rectangle[] hoverChain = new Rectangle[0];  // 父子层级链: [0]=窗口 → [n-1]=最深元素 (滚轮切换用)
        int hoverLevel = -1;                        // -1=自动落在最深一层; >=0=用户滚轮锁定的层级
        bool curBidirectional = (markStartType0 != 0 && markStartType0 == markEndType0);
        float curWidth = markWidth0;                // 属性栏: 线宽 (细2/中3.5/粗6)
        Color curColor = markColor0;                // 属性栏: 颜色 (PixPin 默认红)
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
        int curFontPt = markFontPt0; // 属性栏: 字号 (文字/序号)
        string curSeqFmt = markSeqFmt0; // 属性栏: 序号格式 (1/I/a/A)
        string curFontFamily = markFontFamily0; // 属性栏: 字体
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
            AutoScaleMode = AutoScaleMode.None; // 125% DPI 下 Form 的 Font 缩放会整体放大子控件坐标 → 属性栏网格/列表错位重叠; 本程序全按物理像素设计
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
            int ud; if (int.TryParse(Cfg("capture.uiDetect", "0"), out ud) && ud >= 0 && ud <= 2) uiDetect = ud;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate(); // 拿焦点: Esc 才可达
            // 开罩即高亮光标下窗口 (PixPin 同款: 不必先动鼠标); sp 直接就是屏幕坐标
            try { POINT cp; GetCursorPos(out cp); UpdateHoverDetect(new Point(cp.x, cp.y)); } catch { }
            StartHoverLoop(); // 后台持续检测 (HWND 下钻 + UIA 元素级)
            StartRefiner();   // UIA 细化独立线程 (慢查询不挡悬停跟手)
            Log("capture: overlay shown bounds=" + Bounds);
        }

        // PixPin 同款: 双击选区 = 复制并完成。
        // ⚠️ 必须在这里处理: WM_LBUTTONUP 不带点击数, MouseUp 的 e.Clicks 恒为 1 (实测踩坑)。
        // 第二次 Down 已在 OnMouseDown 里 return 跳过, 此处状态未被破坏; 复制后关闭, 无残余消息
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (cur != null && cur.Kind == Annot.K_POLY && e.Button == MouseButtons.Left) { FinishPoly(); return; } // 折线绘制中: 双击收尾
            if (e.Button == MouseButtons.Left && hasSel && tool == null && !textMode && cur == null && !IsDisposed)
            {
                Log("capture: dblclick = copy");
                ActCopy();
                return;
            }
            base.OnMouseDoubleClick(e);
        }

        // PixPin 同款: 选 UI 元素时滚轮切父子层级 (官方: 「选择 UI 元素时, 可通过鼠标滚轮切换到父子元素」)
        // 上滚=放大到父级(整个卡片/面板), 下滚=缩小到子级(按钮/输入框)。只在还没框选时有效, 链到头就停住。
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!hasSel && hoverChain.Length > 0 && tool == null && !textMode)
            {
                int lv = hoverLevel < 0 ? hoverChain.Length - 1 : hoverLevel;
                int nv = lv + (e.Delta > 0 ? -1 : 1); // 上滚 → 往父一级
                nv = Math.Max(0, Math.Min(hoverChain.Length - 1, nv));
                if (nv != lv)
                {
                    hoverLevel = nv;
                    SetHoverWin(hoverChain[nv]);
                    Log("capture: hover level " + nv + "/" + (hoverChain.Length - 1) + " " + hoverChain[nv].Width + "x" + hoverChain[nv].Height);
                }
                return;
            }
            base.OnMouseWheel(e);
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
            resizeHandle = -1;
            HideBar();
            InvalidateHover();
            Invalidate(); // 暗层恢复全屏 (低频操作, 全屏重绘一次可接受)
            Log("capture: reset selection (right-click)");
        }

        // ---- 选框 8 手柄 (PixPin 同款): 四角+四边中点, 拖拽调整选区 ----
        static readonly Cursor[] HandleCursors = {
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE
        };

        Point[] HandlePts()
        {
            Rectangle c = RectangleToClient(sel);
            int mx = c.X + c.Width / 2, my = c.Y + c.Height / 2;
            return new Point[] {
                new Point(c.Left, c.Top), new Point(mx, c.Top), new Point(c.Right, c.Top),
                new Point(c.Right, my), new Point(c.Right, c.Bottom), new Point(mx, c.Bottom),
                new Point(c.Left, c.Bottom), new Point(c.Left, my)
            };
        }

        int HitHandle(Point clientPt)
        {
            if (!hasSel || textMode || dragging) return -1;
            Point[] ps = HandlePts();
            for (int i = 0; i < 8; i++)
                if (Math.Abs(clientPt.X - ps[i].X) <= 7 && Math.Abs(clientPt.Y - ps[i].Y) <= 7) return i;
            return -1;
        }

        // 手柄拖拽: 固定对边, 拖过对边自动翻转
        Rectangle ResizeSelBy(Rectangle o, Point p, int h)
        {
            int l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
            if (h == 0 || h == 6 || h == 7) l = p.X;
            if (h == 0 || h == 1 || h == 2) t = p.Y;
            if (h == 2 || h == 3 || h == 4) r = p.X;
            if (h == 4 || h == 5 || h == 6) b = p.Y;
            if (l > r) { int x = l; l = r; r = x; }
            if (t > b) { int y = t; t = b; b = y; }
            return new Rectangle(l, t, r - l, b - t);
        }

        void DrawSelHandles(Graphics g)
        {
            if (!hasSel || textMode || dragging || resizeHandle >= 0)
            {
                Log("handles skip: hasSel=" + hasSel + " textMode=" + textMode + " dragging=" + dragging + " rh=" + resizeHandle);
                return;
            }
            foreach (Point pt in HandlePts())
            {
                Rectangle rc = new Rectangle(pt.X - 5, pt.Y - 5, 10, 10);
                using (SolidBrush wb = new SolidBrush(Color.White)) g.FillRectangle(wb, rc);
                using (Pen bp = new Pen(Color.FromArgb(70, 130, 220), 1.5f)) g.DrawRectangle(bp, rc);
            }
        }

        // F1 (workbuddy R9): 遮罩失焦(被别进程抢焦点/UAC/通知) → Esc 打不到本窗, 热键 busy,ignore → 滞留全屏糊罩 DoS。
        // 修法: 失焦且前台属别进程 → 自动关闭; textMode(输入法候选窗夺焦)/自家浮窗(结果/录屏菜单/历史窗) 不触发
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            try
            {
                if (IsDisposed || textMode || recMode) return; // 录制模式: 选区内点击直达下层应用=必然失焦, 遮罩必须留着
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle) return;
                uint pid; GetWindowThreadProcessId(fg, out pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return; // 自家浮窗短暂夺焦
                Log("capture: focus stolen by pid " + pid + ", auto-close to avoid stuck overlay");
                CancelAll();
            }
            catch (Exception ex) { Log("deactivate err: " + ex.Message); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (IsDisposed) return;
            if (recMode) return; // 录制中: 选区外点击不再改变选区 (选区内点击已被 Region 镂空直达下层应用)
            if (e.Button == MouseButtons.Right)
            {
                // PixPin 同款: 正在画的时候右键 = 结束当前这个动作, 不是退出工具/重新框选
                //   实证: pixpin.cn/docs/mark/line —— 折线"最后双击左键或单击右键完成绘制"
                //   折线截图模式: 端点够 → 右键确认; 端点不够 → 右键取消已有端点 (同源语义: 收尾或作废, 都不退工具)
                if (cur != null)
                {
                    if (cur.Kind == Annot.K_POLY) FinishPoly();
                    else { var c = cur; cur = null; polyHoverOk = false; InvalidateAnnot(c); }
                    return;
                }
                if (hasSel || tool != null || textMode) ResetSelection();
                else CancelAll();
                return;
            }
            if (e.Button == MouseButtons.Middle && hasSel) { ActPin(); return; } // 中键 = 贴图 (PixPin 同款)
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks >= 2) return; // 双击的第二次按下不动作 (Double 事件里复制, 防重复框选)
            Point sp = PointToScreen(e.Location);
            if (hasSel && HitHandle(e.Location) >= 0)
            {
                // 手柄按下 = 调整选区 (优先于标注); 工具条临时收起, 松手回位
                CommitTextInput();
                resizeHandle = HitHandle(e.Location);
                resizeOrig = sel;
                HideBar();
                return;
            }
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
                if (tool == "poly" || tool == "curve")
                {
                    // 折线/曲线 (PixPin BrokenLine 输入模式): 单击加点, 双击/回车收尾; 端点创建期钳制不出框
                    if (cur == null)
                    {
                        cur = new Annot();
                        cur.Kind = Annot.K_POLY;
                        cur.Width = curWidth; cur.Color = curColor; cur.FontPt = curFontPt; cur.FontFamily = curFontFamily;
                        cur.StartType = curStartType; cur.EndType = curEndType;
                        cur.Smooth = (tool == "curve"); // 曲线工具 = 平滑样条 (PixPin StraightCurveMode)
                        cur.Dash = curDash; cur.Fill = curFill;
                        cur.Style = Annot.S_ARROW; // 折线走头型字段, 不用旧 6 样式
                        cur.Pts = new List<Point>(); // 🔴 必须初始化: 否则首下点击 cur.Pts.Count 空引用崩进程 (用户实测 NRE)
                        polyHoverOk = false;
                    }
                    Point np = ClampToSel(sp);
                    if (cur.Pts.Count == 0 || cur.Pts[cur.Pts.Count - 1] != np) cur.Pts.Add(np); // 同点不重复加 (双击第二下)
                    InvalidateAnnot(cur);
                    return;
                }
                cur = new Annot();
                cur.Kind = tool == "rect" ? Annot.K_RECT : tool == "ellipse" ? Annot.K_ELLIPSE :
                           tool == "arrow" ? Annot.K_ARROW : tool == "seq" ? Annot.K_SEQ :
                           tool == "mosaic" ? Annot.K_MOSAIC : tool == "highlight" ? Annot.K_HIGHLIGHT : Annot.K_PEN;
                cur.MosaicStrength = curMosaicStrength; cur.HlMode = curHlMode;
                cur.Width = curWidth; cur.Color = curColor; cur.FontPt = curFontPt; cur.FontFamily = curFontFamily;
                cur.StartType = curStartType; cur.EndType = curEndType; cur.Smooth = curSmooth; cur.Dash = curDash;
                cur.Style = curCallout ? Annot.S_CALLOUT :
                            (cur.Kind == Annot.K_ARROW ? (curShape == Annot.S_THIN ? Annot.S_THIN : curShape == Annot.S_HOLLOW ? Annot.S_HOLLOW : Annot.S_ARROW) : curArrowStyle);
                downPt = ClampToSel(sp); // 形状基准点 = 按下处, 钳进选区 (漂移修复 + 不出界)
                sp = downPt; // 创建几何统一用钳制后的坐标
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
                else if (cur.Kind == Annot.K_PEN || cur.Kind == Annot.K_HIGHLIGHT)
                {
                    cur.Pts = new List<Point> { sp };
                    // 荧光笔要够粗才像高亮条 (PixPin: 滚轮调粗细); 沿用当前线宽但保底 14px
                    if (cur.Kind == Annot.K_HIGHLIGHT) { cur.Width = Math.Max(curWidth, 14f); cur.HlMode = curHlMode; }
                }
                else cur.Rect = new Rectangle(sp, Size.Empty);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (recMode) return; // 录制中: 不再响应选区调整
            Point sp = PointToScreen(e.Location);
            if (resizeHandle >= 0)
            {
                // 手柄拖拽中: 固定对边调整选区
                Rectangle old = sel;
                sel = ResizeSelBy(resizeOrig, sp, resizeHandle);
                hasSel = sel.Width > 2 && sel.Height > 2;
                InvalidateSelArea(old);
                return;
            }
            // 文字工具悬停检测: 已提交文字 → 提示框 (其他工具不调用 = 文字当背景)
            UpdateTextHover(sp, e.Location);
            if (hasSel && !pendingDown && !dragging && cur == null && !textMode)
            {
                int hh = HitHandle(e.Location);
                Cursor = hh >= 0 ? HandleCursors[hh] : Cursors.Cross; // 手柄上换方向光标, 离开恢复十字
            }
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
                sp = ClampToSel(sp); // 拖拽时端点钳进选区, 不出界限
                if (cur.Kind == Annot.K_POLY)
                {
                    // 折线绘制中: 只更新橡皮筋落点 (不加点, 点是在 mousedown 加的)
                    if (sp != polyHover)
                    {
                        Rectangle oldB = BoundsOf(cur);
                        if (polyHoverOk) oldB = Rectangle.Union(oldB, new Rectangle(polyHover, Size.Empty));
                        polyHover = sp; polyHoverOk = true;
                        InvalidateAnnot(Rectangle.Union(oldB, new Rectangle(polyHover, Size.Empty)));
                    }
                }
                else if (cur.Kind == Annot.K_PEN || cur.Kind == Annot.K_HIGHLIGHT) { cur.Pts.Add(sp); InvalidateAnnot(cur); }
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
            // 自动窗口检测已移至后台 HoverLoop (180ms 轮询 + UIA 元素级), 内联调用移除
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
            if (uiDetect == 2) return; // 不检测档: 纯手动框选, 这条同步检测路径也别画框
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

        // ---- 自动检测 v2 (PixPin 元素级灵敏度): 后台线程轮询光标, HWND 下钻 + UIA 子树命中。
        // UIA 跨进程调用可能慢 (Electron 大树实测 8s 级), 绝不能上 UI 线程; 400ms 安全阀连续超限即停用本轮 UIA。
        System.Threading.Thread hoverThread;
        volatile bool hoverStop = false;
        volatile int uiaDisabled = 0;
        volatile int uiaFailStreak = 0; // 连续慢/异常计数; 首查可能因 Chromium 惰性建树很慢, 连 5 次才停用

        void StartHoverLoop()
        {
            if (hoverThread != null) return;
            hoverThread = new System.Threading.Thread(HoverLoop);
            hoverThread.IsBackground = true; // 永不阻塞进程退出
            hoverThread.Start();
        }

        void HoverLoop()
        {
            IntPtr lastHwnd = IntPtr.Zero;       // 上轮命中的窗口 (换窗口=立即跟手, 不等 UIA)
            Rectangle uiaCache = Rectangle.Empty; // 细化框缓存: 光标还在里面就一直锁定 (修闪烁: 不再隔轮查询导致大框↔小框交替)
            IntPtr uiaCacheHwnd = IntPtr.Zero;
            while (!hoverStop && !IsDisposed)
            {
                if (recMode) { ApplyHover(Rectangle.Empty); System.Threading.Thread.Sleep(150); continue; } // 录制模式: 选区固定, 停止悬停高亮
                try
                {
                    POINT cp; GetCursorPos(out cp);
                    var sp = new Point(cp.x, cp.y);

                    // 滚轮切中的层级: 光标还在这层里就别自动改回去 (PixPin: 选完层级就锁住, 移开才重算)
                    Rectangle[] chNow = hoverChain;
                    if (chNow.Length > 0 && hoverLevel >= 0 && hoverLevel < chNow.Length && chNow[hoverLevel].Contains(sp))
                    {
                        ApplyHover(chNow[hoverLevel]);
                        System.Threading.Thread.Sleep(60);
                        continue;
                    }
                    if (uiDetect == 2) // 不检测: 纯手动框选
                    {
                        if (hoverChain.Length > 0) { hoverChain = new Rectangle[0]; hoverLevel = -1; }
                        ApplyHover(Rectangle.Empty);
                        lastHwnd = IntPtr.Zero;
                        System.Threading.Thread.Sleep(60);
                        continue;
                    }

                    Rectangle fin = Rectangle.Empty;
                    var path = new System.Collections.Generic.List<IntPtr>();
                    IntPtr h = WindowFromPointEx(cp, Handle, uiDetect == 0, path); // 仅窗口档: 不下钻
                    if (h != IntPtr.Zero)
                    {
                        RECT r;
                        if (GetWindowRect(h, out r) && r.Right > r.Left && r.Bottom > r.Top)
                        {
                            fin = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                            bool cacheHit = uiaCacheHwnd == h && uiaCache != Rectangle.Empty &&
                                            uiaCache.Contains(sp) && fin.Contains(uiaCache);
                            if (cacheHit)
                            {
                                fin = uiaCache; // 锁定: 光标还在细化框内就不换框 (稳定不闪)
                                Rectangle[] chc = hoverChain;
                                if (chc.Length > 0 && hoverLevel >= 0 && hoverLevel < chc.Length && chc[hoverLevel].Contains(sp))
                                    fin = chc[hoverLevel];
                            }
                            else if (uiDetect == 0 && uiaFailStreak < 5)
                            {
                                // 缓存失配: 请求后台细化 (绝不阻塞本循环 — 树冷查询几百 ms, 同步查=高亮冻住不跟手)
                                lock (uiaLock) { reqHwnd = h; reqPt = sp; }
                                uiaKick.Set();
                                // 顺手消费已就绪的结果 (光标还停在结果框内才收)
                                Rectangle[] got = null; Rectangle gotRect = Rectangle.Empty;
                                lock (uiaLock)
                                {
                                    if (uiaResultHwnd == h && uiaResult != Rectangle.Empty &&
                                        uiaResult.Contains(sp) && fin.Contains(uiaResult))
                                    { gotRect = uiaResult; got = uiaResultChain; uiaResult = Rectangle.Empty; }
                                }
                                if (got != null && got.Length > 0)
                                {
                                    uiaCache = gotRect; uiaCacheHwnd = h; fin = gotRect;
                                    // 父子链 = 窗口层级(顶层→最深) + UIA 层级(去掉与窗口末层重复的首项)
                                    var merged = new System.Collections.Generic.List<Rectangle>();
                                    Rectangle lastWin = Rectangle.Empty;
                                    RECT rw;
                                    foreach (IntPtr hh in path)
                                    {
                                        if (GetWindowRect(hh, out rw) && rw.Right > rw.Left && rw.Bottom > rw.Top)
                                        {
                                            var rr = new Rectangle(rw.Left, rw.Top, rw.Right - rw.Left, rw.Bottom - rw.Top);
                                            if (merged.Count == 0 || rr != merged[merged.Count - 1]) merged.Add(rr);
                                            lastWin = rr;
                                        }
                                    }
                                    for (int i = 0; i < got.Length; i++)
                                        if (!(i == 0 && got[i] == lastWin) && got[i] != Rectangle.Empty &&
                                            (merged.Count == 0 || got[i] != merged[merged.Count - 1])) merged.Add(got[i]);
                                    hoverChain = merged.ToArray();
                                    hoverLevel = -1; // 默认落在最深一层

                                }
                            }
                        }
                    }
                    if (h != lastHwnd)
                    {
                        uiaCache = Rectangle.Empty; uiaCacheHwnd = IntPtr.Zero;
                        hoverChain = new Rectangle[0]; hoverLevel = -1;
                        // 诊断: 换窗时记类名+矩形 (任务栏识别不到/识别慢这类问题, 日志直接说话; 换窗才打, 不刷屏)
                        if (h != IntPtr.Zero)
                        {
                            try
                            {
                                System.Text.StringBuilder cn = new System.Text.StringBuilder(64); GetClassNameW(h, cn, 64);
                                RECT hr; GetWindowRect(h, out hr);
                                Log("hover hwnd class=" + cn + " rect=" + hr.Left + "," + hr.Top + " " + (hr.Right - hr.Left) + "x" + (hr.Bottom - hr.Top));
                            }
                            catch { }
                        }
                        else Log("hover hwnd none");
                    }
                    lastHwnd = h;
                    ApplyHover(fin); // 带滑动过渡动画
                }
                catch { }
                System.Threading.Thread.Sleep(60);
            }
        }

        // UIA 细化专用线程: 慢查询全在这里跑, 悬停循环只投请求/收结果 → 换窗/移动永远跟手
        readonly object uiaLock = new object();
        IntPtr reqHwnd = IntPtr.Zero; Point reqPt;
        Rectangle uiaResult = Rectangle.Empty; IntPtr uiaResultHwnd = IntPtr.Zero;
    Rectangle[] uiaResultChain = new Rectangle[0]; // 父子链 (滚轮切换用, 数组引用整体替换 = 跨线程读安全)
        System.Threading.AutoResetEvent uiaKick = new System.Threading.AutoResetEvent(false);
        System.Threading.Thread refinerThread;
        Rectangle lastUiaLogged = Rectangle.Empty; // 诊断: 上次已记日志的细化结果 (变化才记, 不刷屏)

        void StartRefiner()
        {
            if (refinerThread != null) return;
            refinerThread = new System.Threading.Thread(RefineLoop);
            refinerThread.IsBackground = true; // 永不阻塞进程退出
            refinerThread.Start();
        }

        void RefineLoop()
        {
            while (!IsDisposed)
            {
                uiaKick.WaitOne(250);
                IntPtr h; Point pt;
                lock (uiaLock) { h = reqHwnd; pt = reqPt; reqHwnd = IntPtr.Zero; }
                if (h == IntPtr.Zero) continue;
                if (uiDetect != 0) continue; // 仅窗口/不检测档: 不做 UIA 细化
                var swUia = System.Diagnostics.Stopwatch.StartNew();
                Rectangle[] ch = UiaChainAt(h, pt); // 可能几百 ms (树冷) — 反正不挡悬停循环
                lock (uiaLock)
                {
                    uiaResultChain = ch;
                    uiaResult = ch.Length > 0 ? ch[ch.Length - 1] : Rectangle.Empty;
                    uiaResultHwnd = h;
                }
                // 诊断: 细化结果变化才记 (深度不够/太粗/太慢, 日志直接说话)
                Rectangle finUia = ch.Length > 0 ? ch[ch.Length - 1] : Rectangle.Empty;
                if (finUia != lastUiaLogged)
                {
                    lastUiaLogged = finUia;
                    Log("hover uia chain depth=" + ch.Length + " final=" + finUia.Width + "x" + finUia.Height + " in " + swUia.ElapsedMilliseconds + "ms");
                }
            }
        }

        // UIA 子树命中: 在目标窗口辅助功能树里向下找包含 pt 的最小元素 (输入框/气泡/窗格级 = PixPin 手感来源)
        // 返回整条父子链 [0]=窗口 → [n-1]=最深元素; PixPin「鼠标滚轮切换父子元素」就用这条链 (官方: 配置-截图-UI 检测=检测元素)
        Rectangle[] UiaChainAt(IntPtr hwnd, Point pt)
        {
            var chain = new List<Rectangle>();
            try
            {
                int dbgWalk = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var cur = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                Rectangle rf = RectOf(cur);
                if (!rf.IsEmpty) chain.Add(rf);
                for (int depth = 0; depth < 8; depth++)
                {
                    var kids = cur.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                    // walk 诊断日志已移除 (5Hz 刷屏)
                    dbgWalk++;
                    System.Windows.Automation.AutomationElement next = null;
                    double best = double.MaxValue;
                    foreach (System.Windows.Automation.AutomationElement k in kids)
                    {
                        try
                        {
                            // Chromium 渲染空壳: 无任何 a11y 子树, 钻进去就到头 → 跳过它走真分支
                            if (k.Current.ClassName == "Intermediate D3D Window") continue;
                            var r = k.Current.BoundingRectangle;
                            if (r.IsEmpty || r.Width < 4 || r.Height < 4) continue;
                            if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
                            {
                                double area = r.Width * r.Height;
                                if (area < best) { best = area; next = k; }
                            }
                        }
                        catch { }
                    }
                    if (next == null) break;
                    cur = next;
                    Rectangle rn = RectOf(cur);
                    if (!rn.IsEmpty) chain.Add(rn); // 这一层也进链 = 滚轮能停在中间层级
                }
                if (sw.ElapsedMilliseconds > 500)
                {
                    // 首查可能因 Chromium 惰性建树很慢 (实测 1761ms), 连续 5 次才判残废
                    if (++uiaFailStreak >= 5) { uiaDisabled = 1; Log("hover uia slow x5 -> disabled this session"); }
                }
                else uiaFailStreak = 0;
                if (chain.Count == 0) chain.Add(Rectangle.Empty);
                return chain.ToArray();
            }
            catch (Exception ex)
            {
                if (++uiaFailStreak >= 5) { uiaDisabled = 1; Log("hover uia err x5: " + ex.Message); }
                if (chain.Count == 0) chain.Add(Rectangle.Empty);
                return chain.ToArray();
            }
        }

        static Rectangle RectOf(System.Windows.Automation.AutomationElement e)
        {
            try
            {
                var r = e.Current.BoundingRectangle;
                if (r.IsEmpty) return Rectangle.Empty;
                return new Rectangle((int)r.Left, (int)r.Top, (int)r.Width, (int)r.Height);
            }
            catch { return Rectangle.Empty; }
        }

        // 检测结果回 UI 线程: 换框 + 联合区域重绘 (选区已有时只更新字段不重绘, OnPaint 本来就不画 hover)
        // ---- 橙框滑动过渡 (PixPin 同款): 从当前框平滑滑到新框, ~130ms smoothstep ----
        Rectangle animFrom = Rectangle.Empty, animTgt = Rectangle.Empty;
        DateTime animT0;
        Timer animTimer; // UI 线程创建 (ApplyHover 经 BeginInvoke, 天然在 UI 线程)

        void ApplyHover(Rectangle nr)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(new MethodInvoker(delegate
                {
                    try
                    {
                        if (IsDisposed) return;
                        if (nr == animTgt && (animTimer == null || !animTimer.Enabled)) return; // 无变化
                        Rectangle cur = hoverWin; // 动画进行中 hoverWin=当前插值位置, 从这续滑 (修: 用 animFrom 会跳回起点)
                        if (cur == nr) { animTgt = nr; return; }
                        // 空框↔实框 (高亮出现/消失) 直接切换, 滑动只用于框与框之间
                        if (nr == Rectangle.Empty || cur == Rectangle.Empty)
                        {
                            StopAnim();
                            SetHoverWin(nr);
                            return;
                        }
                        animFrom = cur; animTgt = nr; animT0 = DateTime.Now;
                        if (animTimer == null)
                        {
                            animTimer = new Timer { Interval = 16 };
                            animTimer.Tick += delegate
                            {
                                try
                                {
                                    if (IsDisposed) { StopAnim(); return; }
                                    double t = (DateTime.Now - animT0).TotalMilliseconds / 130.0;
                                    if (t >= 1.0)
                                    {
                                        StopAnim();
                                        SetHoverWin(animTgt);
                                        return;
                                    }
                                    t = t * t * (3 - 2 * t); // smoothstep 缓动
                                    Rectangle draw = LerpRect(animFrom, animTgt, t);
                                    SetHoverWin(draw);
                                }
                                catch { }
                            };
                        }
                        animTimer.Start();
                    }
                    catch { }
                }));
            }
            catch { }
        }

        void StopAnim()
        {
            try { if (animTimer != null) animTimer.Stop(); } catch { }
        }

        void SetHoverWin(Rectangle nr)
        {
            Rectangle prev = hoverWin;
            hoverWin = nr;
            if (hasSel) return; // 选区已有时不画悬停框
            if (prev == Rectangle.Empty && nr == Rectangle.Empty) return;
            Rectangle a = prev != Rectangle.Empty ? RectangleToClient(prev) : Rectangle.Empty;
            Rectangle b = nr != Rectangle.Empty ? RectangleToClient(nr) : Rectangle.Empty;
            // 框出现/消失: 只能整块重画
            if (prev == Rectangle.Empty || nr == Rectangle.Empty)
            {
                Rectangle u0 = prev != Rectangle.Empty ? a : b;
                u0.Inflate(40, 30);
                Invalidate(u0);
                return;
            }
            // 框↔框: 只重画「变了的那一圈」= 两框并集 挖掉 交集内部。
            // 为什么必须挖: 窗体背景是整张全屏冻结图, 脏区越大系统重绘背景越慢;
            // 原来一失效就是全屏, 滚轮连切层级时每格都全屏 blit → 卡成幻灯片 (用户实测"超级慢")。
            // 交集内部的画面(亮+原图)根本没变, 挖掉它 = 脏区从整屏降到一条边带。
            Rectangle u = Rectangle.Union(a, b); u.Inflate(6, 6);
            Rectangle inter = Rectangle.Intersect(a, b);
            if (inter.Width > 12 && inter.Height > 12)
            {
                inter.Inflate(-4, -4); // 留 4px 保住边框线本身会被重画
                using (Region rg = new Region(u)) { rg.Exclude(inter); Invalidate(rg); }
            }
            else Invalidate(u);
        }

        static Rectangle LerpRect(Rectangle a, Rectangle b, double t)
        {
            RectangleF fa = new RectangleF(a.X, a.Y, a.Width, a.Height);
            RectangleF fb = new RectangleF(b.X, b.Y, b.Width, b.Height);
            return Rectangle.Round(new RectangleF(
                fa.X + (fb.X - fa.X) * (float)t,
                fa.Y + (fb.Y - fa.Y) * (float)t,
                fa.Width + (fb.Width - fa.Width) * (float)t,
                fa.Height + (fb.Height - fa.Height) * (float)t));
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
            if (recMode) return; // 录制中: 无选区操作
            if (IsDisposed) return;
            // 手柄拖拽结束: 工具条回位 + 补重绘 (拖拽中手柄隐藏, 松开显示)
            if (resizeHandle >= 0 && e.Button == MouseButtons.Left)
            {
                resizeHandle = -1;
                if (hasSel) { ShowBar(); InvalidateSelArea(sel); }
                Log("capture: resized sel=" + sel.Width + "x" + sel.Height + " at " + sel.Location);
                return;
            }
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
                    InvalidateSelArea(sel); // 补重绘画手柄
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
                if (hasSel) { ShowBar(); InvalidateSelArea(sel); } // 补一次重绘: 手柄在"松开"状态才画 (拖拽中 skip)
                return;
            }
            if (cur != null)
            {
                if (cur.Kind == Annot.K_POLY)
                {
                    // 折线/曲线: 单击只加点 (mousedown 已处理), 鼠标松开不收尾不清理 — 等双击/回车
                }
                else
                {
                    if (cur.Kind == Annot.K_PEN || cur.Kind == Annot.K_HIGHLIGHT) { if (cur.Pts.Count > 1) PushAnnot(cur); }
                    else if (cur.Kind == Annot.K_ARROW) { if (cur.Pts.Count >= 2 && cur.Pts[0] != cur.Pts[1]) PushAnnot(cur); }
                    else if (cur.Rect.Width > 2 && cur.Rect.Height > 2) PushAnnot(cur);
                    cur = null;
                }
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
            if (a.Kind == Annot.K_PEN || a.Kind == Annot.K_POLY || a.Kind == Annot.K_HIGHLIGHT)
            {
                // 🔴 K_HIGHLIGHT 必须在这: 荧光笔的 Rect 是空的, 落到默认分支 = 失效区域为空 = 画完不重绘看不见,
                // 直到下一笔(马赛克)的大区域重绘顺带把它带出来 (用户实测"画了没效果, 用马赛克后它出现了")
                if (a.Pts == null || a.Pts.Count == 0) return Rectangle.Empty;
                Rectangle r = new Rectangle(a.Pts[0], Size.Empty);
                foreach (Point p in a.Pts) r = Rectangle.Union(r, new Rectangle(p, Size.Empty));
                if (a.Kind == Annot.K_HIGHLIGHT) r.Inflate((int)a.Width + 2, (int)a.Width + 2); // 荧光笔是粗线, 包住线宽
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
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                {
                    Rectangle hd = Rectangle.Empty;
                    if (hoverWin != Rectangle.Empty)
                    {
                        hd = RectangleToClient(hoverWin);
                        hd.Intersect(ClientRectangle);
                    }
                    if (hd.Width > 4 && hd.Height > 4)
                    {
                        // 挖亮 = 暗层直接挖掉 hover 矩形 (Region Exclude)。
                        // 旧做法"填暗再 DrawImage 原图盖回去"每帧全屏采样, 动画每帧一次大图 blit = 切换不流畅的真凶。
                        using (Region rg = new Region(e.ClipRectangle))
                        {
                            rg.Exclude(hd);
                            g.Clip = rg;
                            g.FillRectangle(dim, e.ClipRectangle);
                            g.ResetClip();
                        }
                        using (Pen hp = new Pen(Color.Orange, 2)) g.DrawRectangle(hp, hd);
                    }
                    else g.FillRectangle(dim, e.ClipRectangle);
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
            if (recMode)
            {
                // 录制模式: Region 把选区挖空了, 画在边线上的框会被裁掉内侧一半 — 外扩画整条红框 (PixPin 录制观感)
                using (Pen p = new Pen(Color.Red, 3)) g.DrawRectangle(p, d.X - 2, d.Y - 2, d.Width + 4, d.Height + 4);
            }
            else
            {
                using (Pen p = new Pen(Color.Red, 2)) g.DrawRectangle(p, d);
                DrawSelHandles(g); // 选框 8 手柄 (PixPin 同款)
            }
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
            // PixPin 同款: 标注端点创建时即钳进选区 (出不界), 故此处无需裁剪
            DrawOne(g, cur, offset, true, frozen); // 正在绘制的画在最上层 (inProgress=true: 填充模式也先画线框)
            for (int i = 0; i < annots.Count; i++) DrawOne(g, annots[i], offset, false, frozen);
            // 折线/曲线绘制中: 橡皮筋 (最后一点→鼠标) + 已加点顶点 dot (PixPin 同款反馈)
            if (cur != null && cur.Kind == Annot.K_POLY && polyHoverOk && cur.Pts != null && cur.Pts.Count >= 1)
            {
                PointF last = new PointF(cur.Pts[cur.Pts.Count - 1].X - offset.X, cur.Pts[cur.Pts.Count - 1].Y - offset.Y);
                PointF hv = new PointF(polyHover.X - offset.X, polyHover.Y - offset.Y);
                using (Pen rp = new Pen(cur.Color, 1.5f))
                {
                    rp.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    g.DrawLine(rp, last, hv);
                }
                foreach (Point pt in cur.Pts)
                    using (SolidBrush vb = new SolidBrush(cur.Color))
                        g.FillEllipse(vb, pt.X - offset.X - 2.5f, pt.Y - offset.Y - 2.5f, 5f, 5f);
            }
        }

        // 荧光笔辅助: 点列包围盒
        static Rectangle RectOfPts(Point[] pts)
        {
            int x0 = pts[0].X, y0 = pts[0].Y, x1 = x0, y1 = y0;
            for (int i = 1; i < pts.Length; i++)
            {
                if (pts[i].X < x0) x0 = pts[i].X; if (pts[i].X > x1) x1 = pts[i].X;
                if (pts[i].Y < y0) y0 = pts[i].Y; if (pts[i].Y > y1) y1 = pts[i].Y;
            }
            return Rectangle.FromLTRB(x0, y0, x1, y1);
        }

        // 正片叠底合成 (PixPin 荧光笔默认混合模式): region(32bpp 副本) 内按 mask 笔画 alpha 逐像素乘色
        // dst = lerp(dst, dst*color/255, maskA) — mask 边缘抗锯齿 alpha 自动变成部分混合
        static void MultiplyBlit(Bitmap region, Bitmap mask, Color c)
        {
            try
            {
                int w = region.Width, h = region.Height, n = w * h * 4;
                var rb = new byte[n]; var mb = new byte[n];
                BitmapData rd = region.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                BitmapData md = mask.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(rd.Scan0, rb, 0, n);
                    System.Runtime.InteropServices.Marshal.Copy(md.Scan0, mb, 0, n);
                    for (int i = 0; i < n; i += 4)
                    {
                        int ma = mb[i + 3];
                        if (ma == 0) continue;
                        rb[i]     = BlendMul(rb[i],     c.B, ma);
                        rb[i + 1] = BlendMul(rb[i + 1], c.G, ma);
                        rb[i + 2] = BlendMul(rb[i + 2], c.R, ma);
                    }
                    System.Runtime.InteropServices.Marshal.Copy(rb, 0, rd.Scan0, n);
                }
                finally { region.UnlockBits(rd); mask.UnlockBits(md); }
            }
            catch { }
        }

        static byte BlendMul(byte dst, byte cv, int ma)
        {
            int mul = dst * cv / 255;
            return (byte)((mul * ma + dst * (255 - ma)) / 255);
        }

        static void DrawOne(Graphics g, Annot a, Point offset) { DrawOne(g, a, offset, false, null); }

        // inProgress=true = 正在绘制的标注 (填充模式也先画线框, 收尾才呈现填充 — 用户实测: 填充开着线段不可见)
        // src = 冻结原图 (马赛克要从原图采样; 其余工具不用)
        static void DrawOne(Graphics g, Annot a, Point offset, bool inProgress, Bitmap src)
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
                        DrawAnnotArrow(g, a, offset);
                        break;
                    case Annot.K_POLY:
                        DrawAnnotPoly(g, a, offset, inProgress);
                        break;
                    case Annot.K_PEN:
                        if (a.Pts != null && a.Pts.Count > 1)
                        {
                            Point[] pts = new Point[a.Pts.Count];
                            for (int i = 0; i < a.Pts.Count; i++) pts[i] = new Point(a.Pts[i].X - offset.X, a.Pts[i].Y - offset.Y);
                            g.DrawLines(p, pts);
                        }
                        break;
                    case Annot.K_MOSAIC:
                    {
                        // 马赛克 (PixPin EditItemCustom/MarkBar.Mosaic.MosaicStrength): 原图块降采样再最近邻放大
                        if (src != null)
                        {
                            Rectangle mr = a.Rect; mr.Offset(-offset.X, -offset.Y);
                            mr.Intersect(new Rectangle(0, 0, src.Width, src.Height));
                            if (mr.Width > 2 && mr.Height > 2)
                            {
                                int block = Math.Max(2, a.MosaicStrength);
                                int sw = Math.Max(1, mr.Width / block), sh = Math.Max(1, mr.Height / block);
                                using (Bitmap small = new Bitmap(sw, sh))
                                using (Graphics gs = Graphics.FromImage(small))
                                {
                                    gs.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                    gs.PixelOffsetMode = PixelOffsetMode.Half;
                                    gs.DrawImage(src, new Rectangle(0, 0, sw, sh), mr, GraphicsUnit.Pixel);
                                    g.PixelOffsetMode = PixelOffsetMode.Half;
                                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                                    g.DrawImage(small, mr); // 最近邻放大 = 方块马赛克
                                    g.InterpolationMode = InterpolationMode.Bilinear;
                                    g.PixelOffsetMode = PixelOffsetMode.Default;
                                }
                            }
                        }
                        break;
                    }
                    case Annot.K_HIGHLIGHT:
                    {
                        // 荧光笔 (PixPin 文档: 默认正片叠底, 深色背景可用半透明; 粗圆头)
                        if (src != null && a.Pts != null && a.Pts.Count > 1)
                        {
                            Point[] pts = new Point[a.Pts.Count];
                            for (int i = 0; i < a.Pts.Count; i++) pts[i] = new Point(a.Pts[i].X - offset.X, a.Pts[i].Y - offset.Y);
                            Rectangle bb = RectOfPts(pts); bb.Inflate((int)a.Width + 2, (int)a.Width + 2);
                            bb.Intersect(new Rectangle(0, 0, src.Width, src.Height));
                            if (bb.Width > 2 && bb.Height > 2)
                            {
                                // 画到离屏小图再合成: 同一笔画来回交叉处不会越描越深
                                using (Bitmap lay = new Bitmap(bb.Width, bb.Height))
                                using (Graphics lg = Graphics.FromImage(lay))
                                {
                                    lg.SmoothingMode = SmoothingMode.AntiAlias;
                                    Point[] lpts = new Point[pts.Length];
                                    for (int i = 0; i < pts.Length; i++) lpts[i] = new Point(pts[i].X - bb.X, pts[i].Y - bb.Y);
                                    using (Pen lp = new Pen(
                                        a.HlMode == Annot.M_MULTIPLY ? a.Color : Color.FromArgb(110, a.Color), a.Width))
                                    {
                                        lp.StartCap = LineCap.Round; lp.EndCap = LineCap.Round; lp.LineJoin = LineJoin.Round;
                                        lg.DrawLines(lp, lpts);
                                    }
                                    if (a.HlMode == Annot.M_MULTIPLY)
                                    {
                                        // 正片叠底: dst = dst * color / 255 (像素级, 只影响笔画 mask)
                                        using (Bitmap region = src.Clone(bb, PixelFormat.Format32bppArgb))
                                        {
                                            MultiplyBlit(region, lay, a.Color);
                                            g.DrawImage(region, bb);
                                        }
                                    }
                                    else g.DrawImage(lay, bb); // 半透明
                                }
                            }
                        }
                        break;
                    }
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
                        Rectangle r = SeqBadgeRect(a); r.Offset(-offset.X, -offset.Y); // 宽度随文字自适应, "10"/"XII"不裁
                        using (SolidBrush b = new SolidBrush(a.Color)) g.FillEllipse(b, r);
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            sf.FormatFlags = StringFormatFlags.NoWrap; // 禁折行: 默认放不下会把"0"换行挤出圆外 (实测两位数只剩"1")
                            using (Brush wb = new SolidBrush(Color.White))
                            {
                                Font f = GetAnnotFont(a.FontFamily, a.FontPt); // 缓存字体不可 Dispose
                                string seqStr = SeqText(a.No, a.SeqFmt);
                                // 文字水平压缩兜底长串(罗马); 数字串不压。测量带 DPI 补偿(实际渲染比 96dpi 测量宽)
                                float avail = r.Width + 16;
                                float sx = 1f;
                                try { using (Font mf = new Font(a.FontFamily, a.FontPt, FontStyle.Bold)) { float tw = TextRenderer.MeasureText(seqStr, mf).Width * 1.25f; if (tw > avail) sx = avail / tw; } } catch { }
                                // 绘制矩形左右加余量: GDI+ 布局矩形超出即裁剪, 不加余量"0"会被裁掉只剩"1" (实测根因)
                                RectangleF drawR = new RectangleF(r.X - 40, r.Y - 4, r.Width + 80, r.Height + 8);
                                if (sx < 1f)
                                {
                                    var st = g.Save();
                                    g.TranslateTransform(r.X + r.Width / 2f, 0);
                                    g.ScaleTransform(sx, 1f);
                                    g.TranslateTransform(-(r.X + r.Width / 2f), 0);
                                    g.DrawString(seqStr, f, wb, drawR, sf);
                                    g.Restore(st);
                                }
                                else g.DrawString(seqStr, f, wb, drawR, sf);
                            }
                        }
                        break;
                }
            }
        }

        // 折线/曲线 (PixPin BrokenLine): 多顶点 + 起止头型 + 虚线 + 平滑样条
        //   Fill=true 时 = 闭合填充 (PixPin MarkBar.BrokenLine.Fill): 首尾闭合成多边形纯色填满, 无头型无虚线
        //   inProgress=true (绘制中) 时填充模式也先画线框 — 否则 2 个点的多边形面积=0, 线段全隐 (用户实测)
        static void DrawAnnotPoly(Graphics g, Annot a, Point offset, bool inProgress)
        {
            if (a == null || a.Pts == null || a.Pts.Count < 2) return;
            PointF[] pts = new PointF[a.Pts.Count];
            for (int i = 0; i < a.Pts.Count; i++) pts[i] = new PointF(a.Pts[i].X - offset.X, a.Pts[i].Y - offset.Y);
            if (a.Fill)
            {
                if (inProgress)
                {
                    // 绘制中: 线框预览 (无头型, 保留虚线), 收尾后才呈现纯色填充
                    PixPinArrowGeom.DrawBrokenLine(g, pts, a.Width, a.Color, 0, 0, a.Smooth, a.Dash);
                    return;
                }
                float sa, ea;
                PointF[] poly = a.Smooth ? PixPinArrowGeom.SampleSmooth(pts, 0.5f, out sa, out ea) : pts;
                if (poly != null && poly.Length >= 3)
                    using (SolidBrush b = new SolidBrush(a.Color)) g.FillPolygon(b, poly);
                return;
            }
            PixPinArrowGeom.DrawBrokenLine(g, pts, a.Width, a.Color, a.StartType, a.EndType, a.Smooth, a.Dash);
        }

        // 箭头渲染 (证据级复刻 PixPin GenerateBrokenLinePath + GenArrowHeadContour):
        //   头部几何全部走 PixPinArrowGeom (13 型, REA 逆向实证); 本方法只负责坐标偏移/特例分发.
        static void DrawAnnotArrow(Graphics g, Annot a, Point offset)
        {
            if (a == null) return;
            // 细尾渐变箭头 (老版保留样式: 实心 S_THIN / 空心 S_HOLLOW) — 几何走 PixPinArrowGeom.DrawTaperArrow
            if (a.Style == Annot.S_THIN || a.Style == Annot.S_HOLLOW)
            {
                PointF tp1, tp2;
                if (a.Pts != null && a.Pts.Count >= 2)
                { tp1 = new PointF(a.Pts[0].X - offset.X, a.Pts[0].Y - offset.Y); tp2 = new PointF(a.Pts[1].X - offset.X, a.Pts[1].Y - offset.Y); }
                else
                { tp1 = new PointF(a.Rect.X - offset.X, a.Rect.Y - offset.Y); tp2 = new PointF(a.Rect.Right - offset.X, a.Rect.Bottom - offset.Y); }
                PixPinArrowGeom.DrawTaperArrow(g, tp1, tp2, a.Width, a.Color, a.Style == Annot.S_HOLLOW);
                return;
            }
            // S_CALLOUT 双竖杠标注线 (PixPin 独立标注工具同款) 保留特例
            if (a.Style == Annot.S_CALLOUT)
            {
                PointF p1, p2;
                if (a.Pts != null && a.Pts.Count >= 2)
                { p1 = new PointF(a.Pts[0].X - offset.X, a.Pts[0].Y - offset.Y); p2 = new PointF(a.Pts[1].X - offset.X, a.Pts[1].Y - offset.Y); }
                else
                { p1 = new PointF(a.Rect.X - offset.X, a.Rect.Y - offset.Y); p2 = new PointF(a.Rect.Right - offset.X, a.Rect.Bottom - offset.Y); }
                DrawCalloutLine(g, p1, p2, a.Width, a.Color);
                return;
            }
            PointF[] pts;
            if (a.Pts != null && a.Pts.Count >= 2)
            {
                pts = new PointF[a.Pts.Count];
                for (int i = 0; i < a.Pts.Count; i++) pts[i] = new PointF(a.Pts[i].X - offset.X, a.Pts[i].Y - offset.Y);
            }
            else
            { pts = new PointF[] { new PointF(a.Rect.X - offset.X, a.Rect.Y - offset.Y), new PointF(a.Rect.Right - offset.X, a.Rect.Bottom - offset.Y) }; }
            PixPinArrowGeom.DrawBrokenLine(g, pts, a.Width, a.Color, a.StartType, a.EndType, a.Smooth, a.Dash);
        }

        // 双竖杠标注引线 (原 S_CALLOUT 几何, 仅形状不变, 颜色/线宽随属性栏)
        static void DrawCalloutLine(Graphics g, PointF p1, PointF p2, float w, Color c)
        {
            using (Pen p = new Pen(c, w)) { p.LineJoin = LineJoin.Round; g.DrawLine(p, p1, p2); }
            double ang = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            float px = (float)-Math.Sin(ang), py = (float)Math.Cos(ang);
            float bl = Math.Min(10f, Math.Max(7f, w * 4f));
            using (Pen bp = new Pen(c, w)) { bp.StartCap = LineCap.Round; bp.EndCap = LineCap.Round;
                g.DrawLine(bp, p1.X + bl * px, p1.Y + bl * py, p1.X - bl * px, p1.Y - bl * py);
                g.DrawLine(bp, p2.X + bl * px, p2.Y + bl * py, p2.X - bl * px, p2.Y - bl * py);
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
            bar.Add("poly", "折线标注 (单击加点, 双击或回车收尾)", delegate { PickTool("poly"); }, true).ToolKey = "poly";
            bar.Add("curve", "曲线标注 (单击加点, 双击或回车收尾)", delegate { PickTool("curve"); }, true).ToolKey = "curve";
            bar.Add("pen", "自由画笔", delegate { PickTool("pen"); }, true).ToolKey = "pen";
            bar.Add("highlight", "荧光笔 (拖动高亮文字/区域)", delegate { PickTool("highlight"); }, true).ToolKey = "highlight";
            bar.Add("mosaic", "马赛克 (拖动打码)", delegate { PickTool("mosaic"); }, true).ToolKey = "mosaic";
            bar.Add("text", "文字标注 (点击选区内输入, 回车落字)", delegate { PickTool("text"); }, true).ToolKey = "text";
            bar.Add("seq", "序号标记 (自动递增)", delegate { PickTool("seq"); }, true).ToolKey = "seq";
            bar.AddSep();
            // [编辑组]
            bar.Add("undo", "撤销 (Ctrl+Z)", delegate { ActUndo(); });
            bar.Add("redo", "重做 (Ctrl+Y)", delegate { ActRedo(); });
            bar.AddSep();
            // [功能组]
            bar.Add("ocr", "OCR 文字识别", delegate { ActOcr(); });
            bar.Add("table", "表格识别 (出 Markdown, 可贴 Excel)", delegate { ActTable(); });
            bar.Add("latex", "公式识别 (出 LaTeX)", delegate { ActLatex(); });
            bar.Add("translate", "翻译", delegate { ActTranslate(); });
            bar.Add("pin", "贴图 (钉到桌面)", delegate { ActPin(); });
            bar.Add("rec", "录屏 (延迟可选)", delegate { ShowRecMenu(); });
            bar.Add("longshot", "长截图 (滚动拼接: 手动滚轮或自动↑↓, 可切横向)", delegate { StartLongShot(sel, this); });
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
            bool need = tool == "arrow" || tool == "rect" || tool == "ellipse" || tool == "pen" || tool == "text" || tool == "seq" || tool == "poly" || tool == "curve" || tool == "highlight" || tool == "mosaic";
            if (!need) { propBar.Visible = false; return; }
            // PixPin 式显隐: 只显示当前工具相关属性 (不相关按钮直接隐藏, 悬空分隔线自动收敛)
            propBar.ShowForTool(tool);
            MarkProp();
            PlacePropBar();
            propBar.Visible = true;
        }

        void BuildPropBar()
        {
            // 终版 PixPin: 箭头样式=6 图标条(图1); 序号=[N⇅][格式▾][大小][字号]+色板(图2); 文字=[字体▾][大小][字号]+色板(图3); 只显当前工具相关
            propBar = new ToolbarPanel();
            string[] lineTools = { "rect", "ellipse", "arrow", "pen", "highlight", "mosaic" }; // 颜色/粗细组 (荧光笔要高亮色; 马赛克用色块边距弱相关但保留一致)
            string[] textTools = { "text", "seq" };
            string[] arrowOnly = { "arrow" };
            string[] arrowTools = { "arrow", "poly", "curve" }; // 头型/双向/虚线: 折线曲线也有 (PixPin BrokenLine 同款配置)
            string[] polyTools = { "poly", "curve" };           // 闭合填充: 仅折线/曲线
            string[] seqOnly = { "seq" };
            // ---- 箭头头型下拉 (照 PixPin populateArrowTypes 11 项 + 标注线特例, 竖向菜单+长预览) ----
            // 头型枚举对应 PixPinArrowGeom.BuildArrowHeadPath: 0无 12五角 1开口 2实心(大) 3实心(小) 4实心圆 5空心圆 6实心菱形 7空心菱形 8刻度 9十字 ; 99=标注线
            int[] styleOrder = { 0, 12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 14, 15, 99 };
            string[] styleNames = { "直线", "五角", "开口箭头", "实心三角(大)", "实心三角(小)", "实心圆", "空心圆", "实心菱形", "空心菱形", "刻度线", "十字", "细尾箭头(实心)", "细尾箭头(空心)", "标注线" };
            ToolbarPanel.Btn styBtn = null;
            styBtn = propBar.AddPreviewDropdown("箭头样式 (点选切换)", delegate
            {
                StylePopup sp = new StylePopup(styleOrder,
                    delegate(int v) {
                        if (v == 99) return curCallout;
                        if (v == 14) return !curCallout && curShape == Annot.S_THIN;
                        if (v == 15) return !curCallout && curShape == Annot.S_HOLLOW;
                        return !curCallout && curShape == 0 && curEndType == v;
                    },
                    delegate(int v) {
                        if (v == 99) { curCallout = true; }
                        else if (v == 14) { curCallout = false; curShape = Annot.S_THIN; }
                        else if (v == 15) { curCallout = false; curShape = Annot.S_HOLLOW; }
                        else
                        {
                            // 双向态推导: 之前是双头(start==end 且非0) → 新品种继续双头; 否则单头
                            bool wasBidir = (curStartType != 0 && curStartType == curEndType);
                            curCallout = false; curShape = 0; curEndType = v; curStartType = wasBidir ? v : 0;
                        }
                        MarkProp();
                    },
                    delegate(Graphics g, RectangleF row, int key, Color ink2)
                    {
                        ToolbarPanel.DrawStyleGlyph(g, row, key, ink2, false); // 预览简化图标: 一眼分清, 不求源码几何
                    },
                    delegate(int v) { return styleNames[System.Array.IndexOf(styleOrder, v)]; },
                    Color.FromArgb(232, 234, 240));
                Point pos = propBar.PointToScreen(new Point(styBtn.Rect.X, styBtn.Rect.Bottom + 4));
                sp.Location = pos;
                sp.Show();
            }, arrowTools);
            styBtn.ToolKey = "styd";
            // ---- 箭头修饰开关: 双向 / 平滑曲线 / 虚线 ----
            propBar.AddText((curStartType != 0 && curStartType == curEndType) ? "双向:开" : "双向:关", "双向箭头 (起点同款头型)", delegate
            {
                // 单一事实源: 双向态由 start/end 实际值推导 (start!=0 即开), 杜绝标志位与实态漂移
                if (curStartType != 0 && curStartType == curEndType) curStartType = 0;
                else curStartType = curEndType;
                curBidirectional = (curStartType != 0);
                MarkProp();
            }, arrowTools).ToolKey = "bidir";
            propBar.AddText(curSmooth ? "平滑:开" : "平滑:关", "平滑曲线", delegate
            {
                curSmooth = !curSmooth;
                MarkProp();
            }, arrowOnly).ToolKey = "smooth";
            ToolbarPanel.Btn dashBtn = null;
            dashBtn = propBar.AddPreviewDropdown("虚线样式", delegate
            {
                int[] dkeys = { 0, 2, 3, 4, 5 }; // PixPin PenStyle 实证 5 项: 实线/疏/点/疏点/疏点(长)
                StylePopup sp = new StylePopup(dkeys,
                    delegate(int k) { return curDash == k; },
                    delegate(int k) { curDash = k; MarkProp(); },
                    delegate(Graphics g, RectangleF row, int key, Color ink2)
                    {
                        float ps2 = StylePopup.UiScale();
                        using (Pen dp = new Pen(ink2, 2.2f * ps2))
                        {
                            dp.StartCap = LineCap.Round; dp.EndCap = LineCap.Round;
                            PixPinArrowGeom.ApplyDash(dp, key);
                            g.DrawLine(dp, row.X + 14f * ps2, row.Y + row.Height / 2f, row.Right - 14f * ps2, row.Y + row.Height / 2f);
                        }
                    },
                    delegate(int k) { return k == 2 ? "虚线(疏)" : (k == 3 ? "点线" : (k == 4 ? "虚点线" : (k == 5 ? "虚点线(长)" : "实线"))); },
                    Color.FromArgb(232, 234, 240));
                Point pos = propBar.PointToScreen(new Point(dashBtn.Rect.X, dashBtn.Rect.Bottom + 4));
                sp.Location = pos;
                sp.Show();
            }, arrowTools);
            dashBtn.ToolKey = "dash";
            // ---- 闭合填充 (PixPin MarkBar.BrokenLine.Fill): 折线首尾闭合成多边形, 纯色填满 ----
            propBar.AddText(curFill ? "填充:开" : "填充:关", "闭合填充 (折线首尾闭合成多边形, 纯色填满; 填充时无头型/虚线)", delegate
            {
                curFill = !curFill;
                MarkProp();
            }, polyTools).ToolKey = "fill";
            propBar.AddSep(lineTools);
            // ---- 粗细下拉 (竖排菜单: 每项一条按实际粗细的长预览线) ----
            float[] widthVals = { 2f, 3.5f, 6f };
            string[] widthNames = { "细线", "中线", "粗线" };
            ToolbarPanel.Btn wdBtn = null;
            wdBtn = propBar.AddPreviewDropdown("线条粗细", delegate
            {
                int[] wkeys = { 20, 35, 60 }; // ×10 存 int (闭包捕获键)
                StylePopup sp = new StylePopup(wkeys,
                    delegate(int k) { float v = k / 10f; return (v <= 2.5f && curWidth <= 2.5f) || (v > 2.5f && v <= 4.5f && curWidth > 2.5f && curWidth <= 4.5f) || (v > 4.5f && curWidth > 4.5f); },
                    delegate(int k) { curWidth = k / 10f; MarkProp(); },
                    delegate(Graphics g, RectangleF row, int key, Color ink2)
                    {
                        using (Pen wp = new Pen(ink2, key / 10f * StylePopup.UiScale()))
                        {
                            wp.StartCap = System.Drawing.Drawing2D.LineCap.Round; wp.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                            g.DrawLine(wp, row.X + 10f * StylePopup.UiScale(), row.Y + row.Height / 2f, row.Right - 10f * StylePopup.UiScale(), row.Y + row.Height / 2f);
                        }
                    },
                    delegate(int k) { return k == 20 ? "细线" : (k == 35 ? "中线" : "粗线"); },
                    Color.FromArgb(232, 234, 240));
                Point pos = propBar.PointToScreen(new Point(wdBtn.Rect.X, wdBtn.Rect.Bottom + 4));
                sp.Location = pos;
                sp.Show();
            }, lineTools);
            wdBtn.ToolKey = "wd";
            propBar.AddSep(seqOnly);
            // ---- 序号组 (图2): [N⇅] [格式▾] ----
            ToolbarPanel.Btn startBtn = null;
            startBtn = propBar.AddSpinner(seqNext.ToString(), "序号起始数字 (▲+1 / ▼-1)", delegate(bool up)
            {
                if (up) { if (seqNext < 99) seqNext++; }
                else { if (seqNext > 1) seqNext--; }
                startBtn.DrawStr = seqNext.ToString(); propBar.RelayoutNow();
                Log("capture: seq start=" + seqNext);
            }, seqOnly);
            startBtn.ToolKey = "seqstart";
            ToolbarPanel.Btn fmtBtn = null;
            fmtBtn = propBar.AddText("1.2.3 ▾", "序号格式", delegate
            {
                ContextMenuStrip m = DarkMenu();
                string[][] fmts = { new string[] { "1", "1.2.3" }, new string[] { "I", "I.II.III" }, new string[] { "a", "a.b.c" }, new string[] { "A", "A.B.C" } };
                foreach (string[] fm in fmts)
                {
                    string f = fm[0], label = fm[1];
                    ToolStripItem it = m.Items.Add(label, null, delegate
                    {
                        curSeqFmt = f;
                        if (fmtBtn != null) { fmtBtn.DrawStr = label + " ▾"; propBar.RelayoutNow(); }
                        SaveMarkNow();
                    });
                    if (f == curSeqFmt) it.Font = new Font(it.Font, FontStyle.Bold);
                }
                m.Show(propBar, fmtBtn.Rect.X, fmtBtn.Rect.Bottom + 2);
            }, seqOnly);
            fmtBtn.ToolKey = "seqfmt";
            propBar.AddSep(textTools);
            // ---- 字体/大小 (图2/图3): [字体▾] [大小] [字号] ----
            familyBtn = propBar.AddText(FamilyShort(curFontFamily) + " ▾", "字体 (文字/序号)", delegate { ShowFamilyMenu(); }, textTools);
            familyBtn.ToolKey = "f_family";
            propBar.AddLabel("大小", textTools);
            fontBtn = propBar.AddText(curFontPt.ToString(), "字号 (点选切换)", delegate { ShowFontMenu(); }, textTools);
            fontBtn.ToolKey = "f_size";
            propBar.AddSep();
            // ---- 色板 (图2 右下: 黑红橙黄绿蓝紫灰白) ----
            Color[] cols =
            {
                Color.FromArgb(17, 17, 17),    // 黑
                Color.FromArgb(242, 80, 59),   // 红 (默认)
                Color.FromArgb(235, 130, 50),  // 橙
                Color.FromArgb(245, 198, 60),  // 黄
                Color.FromArgb(94, 176, 100),  // 绿
                Color.FromArgb(59, 125, 216),  // 蓝
                Color.FromArgb(150, 90, 200),  // 紫
                Color.FromArgb(150, 150, 150), // 灰
                Color.FromArgb(255, 255, 255), // 白
            };
            string[] cnames = { "黑色", "红色", "橙色", "黄色", "绿色", "蓝色", "紫色", "灰色", "白色" };
            for (int i = 0; i < cols.Length; i++)
            {
                Color c = cols[i];
                propBar.AddColor(c, cnames[i], delegate { curColor = c; MarkProp(); }).ToolKey = "col" + i;
            }
            Controls.Add(propBar);
            propBar.Visible = false;
            MarkProp();
        }

        void MarkProp()
        {
            foreach (var b in propBar.Btns)
            {
                if (b.ToolKey == null) continue;
                if (b.ToolKey == "styd") { var po = b.Preview; b.Preview = curFill ? ToolbarPanel.MakeFillPreviewBmp(false) : ToolbarPanel.MakeArrowPreviewBmp(curStartType, curEndType, curSmooth, curDash, curCallout, curShape, false); if (po != null) po.Dispose(); }
                else if (b.ToolKey == "bidir") { bool bd = (curStartType != 0 && curStartType == curEndType); b.DrawStr = bd ? "双向:开" : "双向:关"; b.On = bd; } // 开=按钮高亮, 双头预览不意外
                else if (b.ToolKey == "smooth") b.DrawStr = curSmooth ? "平滑:开" : "平滑:关";
                else if (b.ToolKey == "dash") { var po = b.Preview; b.Preview = ToolbarPanel.MakeDashPreviewBmp(curDash, false); if (po != null) po.Dispose(); }
                else if (b.ToolKey == "fill") b.DrawStr = curFill ? "填充:开" : "填充:关";
                else if (b.ToolKey == "wd") { var po = b.Preview; b.Preview = ToolbarPanel.MakeWidthPreviewBmp(curWidth, false); if (po != null) po.Dispose(); }
                else if (b.ToolKey.StartsWith("col")) b.On = b.Swatch == curColor;
            }
            propBar.RelayoutNow();
            SaveMarkNow(); // PixPin 同款: 改完即记, 下次截图恢复
        }

        // 标注参数落盘 (shot-service.json mark 节)
        void SaveMarkNow()
        {
            try { SaveMarkCfg(curWidth, curStartType, curEndType, curSmooth, curDash, curCallout, curShape, curFill, curColor, curFontPt, curFontFamily, curSeqFmt); } catch { }
        }

        void PlacePropBar()
        {
            if (propBar == null || bar == null) return;
            Rectangle vs = SystemInformation.VirtualScreen;
            int x = bar.Left, y = bar.Bottom + 2; // 紧贴主条 (猜高度锚距会空一行, 用户实测); 压选区时下方逻辑翻主条上方
            // X1 (workbuddy): 主条被顶到选区上方时(选区贴屏底), 属性栏放主条下方会压住选区右下正在标的标注/正文 → 翻到主条上方(选区外)
            Rectangle cand = new Rectangle(x, y, propBar.Width, propBar.Height);
            Rectangle selv = RectangleToClient(sel);
            if (cand.IntersectsWith(selv)) y = bar.Top - propBar.Height - 4;
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
                    if (fontBtn != null) { fontBtn.DrawStr = captured.ToString(); propBar.RelayoutNow(); }
                    SaveMarkNow();
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
                    if (familyBtn != null) { familyBtn.DrawStr = FamilyShort(fam); propBar.RelayoutNow(); }
                    SaveMarkNow();
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
            // 诊断: 每笔标注的 kind/规模/关键参数 (荧光笔画了看不见这类问题, 日志直接说话)
            string extra = a.Kind == Annot.K_HIGHLIGHT ? (" hlMode=" + a.HlMode + " pts=" + (a.Pts != null ? a.Pts.Count : 0)) :
                           a.Kind == Annot.K_MOSAIC ? (" strength=" + a.MosaicStrength + " rect=" + a.Rect.Width + "x" + a.Rect.Height) : "";
            Log("capture: annot push kind=" + a.Kind + extra);
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
                m.Items.Add("● 录制选区 (立即)", null, delegate { StartDelayedRecording(0, false, "mp4"); });
                m.Items.Add("● 录制全屏 (立即)", null, delegate { StartDelayedRecording(0, true, "mp4"); });
                m.Items.Add("◉ GIF 录制选区 (立即)", null, delegate { StartDelayedRecording(0, false, "gif"); });
                m.Items.Add("◉ GIF 录制全屏 (立即)", null, delegate { StartDelayedRecording(0, true, "gif"); });
                m.Items.Add(new ToolStripSeparator());
                int[] delays = { 1, 2, 3, 5, 10 };
                foreach (int dsec in delays)
                {
                    int captured = dsec;
                    m.Items.Add("-" + dsec + "s 后录制选区", null, delegate { StartDelayedRecording(captured, false, "mp4"); });
                }
            }
            m.Show(bar, 8, bar.Bottom - bar.Top + 2);
        }

        // 延迟后录: 倒计时期间遮罩关闭让屏幕可用; 结束后 HUD 计时。fmt: mp4 | gif
        void StartDelayedRecording(int delaySec, bool fullScreen, string fmt)
        {
            CommitTextInput();
            Rectangle area = fullScreen ? SystemInformation.VirtualScreen : sel;
            if (delaySec > 0) ShowTrayInfo(delaySec + " 秒后开始" + (fmt == "gif" ? "GIF 录制" : "录制") + (fullScreen ? "全屏" : "选区") + "...");
            bool barMode = !fullScreen && delaySec == 0; // 立即选区录制: 遮罩转录制条 (PixPin 式, 选区框不消失)
            if (!barMode) Close(); // 延迟倒计时/全屏: 关遮罩走 HUD+红框
            Rectangle areaC = area;
            Task.Run(() =>
            {
                // ⚠️ 后台线程异常会直接弹 .NET 崩溃框(ThreadException 只兜 UI 线程) — 全包
                try
                {
                    if (delaySec > 0) System.Threading.Thread.Sleep(delaySec * 1000);
                    string r = RecordStart(areaC.X, areaC.Y, areaC.Width, areaC.Height, 10, fmt);
                    Log("record via toolbar: " + r);
                    bool ok = r.Contains("\"ok\":true");
                    RunOnHk(() =>
                    {
                        if (!ok) { ShowTrayInfo((fmt == "gif" ? "GIF 录制" : "录屏") + "启动失败: " + r); return; }
                        if (barMode)
                        {
                            EnterRecModeUI(); // 工具条变录制条, 选区框保留, 选区内可自由操作
                        }
                        else
                        {
                            ShowRecordHud();
                            ShowRecBorder();
                            ShowTrayInfo("已开始" + (fmt == "gif" ? " GIF" : "录制") + " " + areaC.Width + "x" + areaC.Height + " — 右下角红色 HUD 的 ⏹ 或托盘菜单可停止");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("record task err: " + ex.Message);
                    RunOnHk(() => ShowTrayInfo("录屏异常: " + ex.Message));
                }
            });
        }

        // ---- 录制模式遮罩: 选区外暗化+拦截, 选区内镂空 (点击直达下层应用=录操作过程); 工具条变 [计时][■][✕] ----
        bool recMode;
        Timer recClock;

        void EnterRecModeUI()
        {
            try
            {
                recMode = true;
                System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath();
                gp.AddRectangle(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                Rectangle hole = RectangleToClient(sel);
                gp.AddRectangle(hole); // Alternate 填充: 内环=洞 (命中测试同样镂空)
                Region = new Region(gp);
                bar.EnterRecMode(RecStopFromBar, RecCancelFromBar);
                PlaceBar(); // 按新按钮数重排 + 回选区下方
                recClock = new Timer { Interval = 500 };
                recClock.Tick += delegate { try { bar.RecTick(); } catch { } };
                recClock.Start();
                Log("capture: rec mode ui, sel=" + sel);
            }
            catch (Exception ex) { Log("rec mode err: " + ex.Message); }
        }

        void StopRecClock()
        {
            try { if (recClock != null) { recClock.Stop(); recClock.Dispose(); } } catch { }
            recClock = null;
        }

        void RecStopFromBar()
        {
            recMode = false;
            StopRecClock();
            RecordStopAndNotify(); // 停止 + 关 HUD/红框 + 气泡报保存路径
            Close();
        }

        void RecCancelFromBar()
        {
            recMode = false;
            StopRecClock();
            string r = RecordStop();
            try
            {
                int p1 = r.IndexOf("\"file\":\"");
                if (r.Contains("\"ok\":true") && p1 >= 0)
                {
                    int p2 = r.IndexOf("\"", p1 + 9);
                    string f = r.Substring(p1 + 9, p2 - p1 - 9);
                    if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
                }
            }
            catch { }
            CloseRecordHud();
            CloseRecBorder();
            ShowTrayInfo("录制已取消, 未保存");
            Close();
        }

        void RecordStopAndNotify()
        {
            RunOnHk(() =>
            {
                string r = RecordStop();
                CloseRecordHud();
                CloseRecBorder();
                Log("record stop via toolbar: " + r);
            string file = "";
            int p1 = r.IndexOf("\"file\":\"");
            if (p1 >= 0)
            {
                int p2 = r.IndexOf("\"", p1 + 9);
                file = r.Substring(p1 + 9, p2 - p1 - 9).Replace("\\", "/");
            }
                ShowTrayInfo((recFmt == "gif" ? "GIF 已保存: " : "录屏已保存: ") + file);
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

            Label rec = new Label(); rec.Text = (recFmt == "gif" ? "● GIF 00:00" : "● REC 00:00"); rec.Left = 12; rec.Top = 10; rec.AutoSize = true;
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
                    rec.Text = (recFmt == "gif" ? "● GIF " : "● REC ") + ((int)el.TotalSeconds / 60).ToString("00") + ":" + ((int)el.TotalSeconds % 60).ToString("00");
                }
                catch { }
            };
            tm.Start();
            hud.FormClosed += (s, e) => { try { tm.Stop(); tm.Dispose(); } catch { } if (recHud == hud) recHud = null; };
            hud.Show();
        }

        internal static void CloseRecordHud()
        {
            RunOnHk(() =>
            {
                Form h = recHud;
                if (h != null && !h.IsDisposed) { try { h.Close(); } catch { } recHud = null; }
            });
        }

        // 回 hk UI 线程执行 (WinForms 窗体/控件只能在创建线程访问; 后台线程异常直接弹崩溃框)
        internal static void RunOnHk(Action a)
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
                    // 老大要求: 不用点了没反应的系统气泡, 保存后直接打开图片
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ox) { Log("capture open err: " + ox.Message); ShowTrayInfo("已保存: " + path); }
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
                Log("capture copied to clipboard"); // [2026-10-16 老大要求] 复制不弹气泡提醒
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

        // 表格/公式识别 (PixPin 同款): 区域 -> VLM 定制提示词 -> 结果浮窗; 表格额外给「贴 Excel」按钮 (Markdown 转 TSV)
        void ActTable() { RunStructuredOcr("表格识别中...", TableOcrPrompt, "表格识别结果 (Markdown)", true); }
        void ActLatex() { RunStructuredOcr("公式识别中...", LatexOcrPrompt, "LaTeX 公式", false); }

        void RunStructuredOcr(string busy, string prompt, string title, bool withTsv)
        {
            CommitTextInput();
            SetBusy(busy);
            Bitmap bmp = CropTaken();
            Task.Run(async () =>
            {
                string text = null, err = null;
                try { text = await OcrProvider().RecognizeAsync(bmp, prompt); }
                catch (Exception ex) { err = ex.Message; }
                finally { try { bmp.Dispose(); } catch { } }
                string got = text, e2 = err;
                bool delivered = BeginOnUi(() =>
                {
                    SetBusy(null);
                    if (e2 != null) { Log("structured ocr err: " + e2); ShowTrayInfo(title + "失败: " + e2); return; }
                    if (string.IsNullOrEmpty(got)) { ShowTrayInfo("未识别到内容"); return; }
                    Log("capture: " + title + " ok (" + got.Length + " chars)");
                    if (withTsv) new ResultForm(title, got, MarkdownToTsv(got)).Show();
                    else new ResultForm(title, got).Show();
                });
                if (!delivered) StructuredFallback(title, got, e2); // 遮罩已关: 剪贴板+气泡兜底, 结果不静默丢
            });
        }

        // Markdown 表 -> TSV (剪贴板文本进 Excel 自动分列; 引号/换行按 Excel 规则转义)
        static string MarkdownToTsv(string md)
        {
            try
            {
                var rows = new List<string[]>();
                foreach (string rawLine in md.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.IndexOf('|') < 0) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[\s|:\-]+$")) continue; // 跳过 |---|---| 分隔行
                    if (line.StartsWith("|")) line = line.Substring(1);
                    if (line.EndsWith("|")) line = line.Substring(0, line.Length - 1);
                    string[] cells = line.Split('|');
                    for (int i = 0; i < cells.Length; i++) cells[i] = cells[i].Trim().Replace("`", "");
                    rows.Add(cells);
                }
                if (rows.Count == 0) return md;
                var sb = new System.Text.StringBuilder();
                for (int r = 0; r < rows.Count; r++)
                {
                    string[] cells = rows[r];
                    for (int c = 0; c < cells.Length; c++)
                    {
                        if (c > 0) sb.Append('\t');
                        sb.Append(cells[c]);
                    }
                    sb.Append("\r\n");
                }
                return sb.ToString();
            }
            catch { return md; }
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
            if (bar == null || bar.IsDisposed) return; // 遮罩已关时后台回调仍会调 SetBusy(null), 炸 ObjectDisposedException 会吞掉后面的结果展示
            bar.SetEnabledAll(msg == null);
            bar.Invalidate();
        }

        // 回 hk UI 线程 (遮罩所在线程), BeginInvoke 不等待 — 任何情况不卡后台任务。
        // 返回 false = 遮罩句柄已没(用户识别期间关了遮罩), 调用方必须自己兜底投递, 不许静默丢结果
        // (2026-10-15 实锤: 云 key#1 超时 45s 期间用户关遮罩 → 回调炸 "句柄未创建" → 表格识别结果无声消失, 用户以为卡死)
        bool BeginOnUi(Action a)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) { Log("capture beginui skip: overlay gone"); return false; }
                BeginInvoke(new MethodInvoker(delegate { try { a(); } catch (Exception ex) { Log("capture ui err: " + ex.Message); } }));
                return true;
            }
            catch (Exception ex) { Log("capture beginui err: " + ex.Message); return false; }
        }

        // 遮罩已关后的结果兜底: 直接进剪贴板 + 托盘气泡 (ResultForm 需要活的消息循环, 遮罩没了就不弹窗)
        static void StructuredFallback(string title, string got, string e2)
        {
            if (e2 != null) { ShowTrayInfo(title + "失败: " + e2); Log("structured fallback err: " + e2); return; }
            if (string.IsNullOrEmpty(got)) { ShowTrayInfo("未识别到内容"); return; }
            System.Threading.Thread t = new System.Threading.Thread(() => { try { System.Windows.Forms.Clipboard.SetText(got); } catch (Exception ex) { Log("fallback clipboard err: " + ex.Message); } });
            t.SetApartmentState(System.Threading.ApartmentState.STA); t.IsBackground = true; t.Start();
            // [2026-10-16 老大要求] 复制不弹气泡提醒 (复制动作本身就是反馈)
            Log("structured fallback delivered: " + got.Length + " chars");
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
                if (recMode) { RecCancelFromBar(); return true; } // 录制中 Esc = 取消录制 (遮罩仍有焦点时可用)
                if (textMode) { CancelTextInput(); return true; }
                if (cur != null) { var c = cur; cur = null; InvalidateAnnot(c); return true; } // 丢弃当前笔画
                CancelAll();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Z)) { ActUndo(); return true; } // Ctrl+Z 撤销
            if (keyData == (Keys.Control | Keys.Y)) { ActRedo(); return true; } // Ctrl+Y 重做
            // 折线/曲线: 回车收尾, 退格删最后一个点 (PixPin BrokenLine 同款手势)
            if (cur != null && cur.Kind == Annot.K_POLY)
            {
                if (keyData == Keys.Enter) { FinishPoly(); return true; }
                if (keyData == Keys.Back)
                {
                    if (cur.Pts.Count > 1) { cur.Pts.RemoveAt(cur.Pts.Count - 1); InvalidateAnnot(cur); }
                    else { var c0 = cur; cur = null; polyHoverOk = false; InvalidateAnnot(c0); }
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // 折线/曲线收尾 (双击或回车): >=2 点才成笔画
        void FinishPoly()
        {
            if (cur == null || cur.Kind != Annot.K_POLY) return;
            if (cur.Pts.Count >= 2)
            {
                PushAnnot(cur);
                Log("capture: annot poly pts=" + cur.Pts.Count + (cur.Fill ? " fill" : "") + (cur.Smooth ? " smooth" : ""));
            }
            else { var c = cur; InvalidateAnnot(c); }
            cur = null;
            polyHoverOk = false;
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

        // PixPin 同款: 标注端点钳进框选区, 拖到边就撞墙停住, 不让出界限
        Point ClampToSel(Point p)
        {
            if (!hasSel || sel.Width <= 0 || sel.Height <= 0) return p;
            int x = Math.Max(sel.X, Math.Min(sel.Right, p.X));
            int y = Math.Max(sel.Y, Math.Min(sel.Bottom, p.Y));
            return new Point(x, y);
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
                    case "poly": // 折线: 三折折线 (PixPin BrokenLine 直线)
                        g.DrawLine(w, 1.5f, 14.5f, 7, 4.5f);
                        g.DrawLine(w, 7, 4.5f, 12, 11.5f);
                        g.DrawLine(w, 12, 11.5f, 16.5f, 3.5f);
                        break;
                    case "curve": // 曲线: 平滑 S 弧 (PixPin BrokenLine 曲线)
                        g.DrawCurve(w, new PointF[] { new PointF(1.5f, 13.5f), new PointF(6.5f, 3f), new PointF(11.5f, 14.5f), new PointF(16.5f, 4.5f) });
                        break;
                    case "mosaic": // 马赛克: 3x3 打码块
                        using (SolidBrush mb = new SolidBrush(Color.FromArgb(232, 234, 240)))
                        {
                            for (int r0 = 0; r0 < 3; r0++)
                                for (int c0 = 0; c0 < 3; c0++)
                                    g.FillRectangle(mb, 2.5f + c0 * 4.7f, 2.5f + r0 * 4.7f, 3.5f, 3.5f);
                        }
                        break;
                    case "highlight": // 荧光笔: 粗高亮条 + 笔尖斜角
                        using (SolidBrush hb = new SolidBrush(Color.FromArgb(232, 234, 240)))
                        {
                            PointF[] tip = { new PointF(2f, 12f), new PointF(5f, 15f), new PointF(16f, 4.5f), new PointF(13.5f, 2f) };
                            g.FillPolygon(hb, tip);
                        }
                        break;
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
                    case "table": // 表格识别: 3x3 网格
                        g.DrawRectangle(w, 2.5f, 2.5f, 13, 13);
                        g.DrawLine(w, 2.5f, 7f, 15.5f, 7f);
                        g.DrawLine(w, 2.5f, 11.5f, 15.5f, 11.5f);
                        g.DrawLine(w, 7f, 2.5f, 7f, 15.5f);
                        g.DrawLine(w, 11.5f, 2.5f, 11.5f, 15.5f);
                        break;
                    case "latex": // 公式识别: Σ 求和符
                        g.DrawLine(w, 14, 3.5f, 5.5f, 3.5f);
                        g.DrawLine(w, 5.5f, 3.5f, 10.5f, 9f);
                        g.DrawLine(w, 10.5f, 9f, 5.5f, 14.5f);
                        g.DrawLine(w, 5.5f, 14.5f, 14, 14.5f);
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
                    case "stop": // 红方块 (停止录制)
                        using (SolidBrush b2 = new SolidBrush(Color.FromArgb(235, 60, 50)))
                            g.FillRectangle(b2, 4.5f, 4.5f, 9, 9);
                        break;
                    case "longshot": // 页面 + 向下箭头 (滚动长截图拼接)
                        g.DrawRectangle(w, 2.5f, 2f, 10, 13.5f);   // 页面
                        g.DrawLine(w, 5, 5.5f, 10.5f, 5.5f);       // 文字行1
                        g.DrawLine(w, 5, 8.5f, 10.5f, 8.5f);       // 文字行2
                        g.DrawLine(w, 5, 11.5f, 8.5f, 11.5f);      // 文字行3(短)
                        g.DrawLine(w, 15, 3.5f, 15, 11f);          // 箭头杆
                        using (SolidBrush b3 = new SolidBrush(w.Color))
                            g.FillPolygon(b3, new PointF[] { new PointF(15f, 15.2f), new PointF(12f, 11.5f), new PointF(18f, 11.5f) });
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
                    case "sty_thin": // 细尾锥形 (PixPin 图1: 尾细头宽实心三角)
                        using (SolidBrush b = new SolidBrush(w.Color))
                            g.FillPolygon(b, new PointF[] { new PointF(1.5f, 8.4f), new PointF(1.5f, 9.6f), new PointF(16.5f, 5.5f), new PointF(16.5f, 12.5f) });
                        break;
                    case "sty_hollow": // 空心箭头 (描边V头+细杆)
                        g.DrawLine(w, 2, 9, 10, 9);
                        using (Pen hp = new Pen(w.Color, 1.6f))
                            g.DrawPolygon(hp, new PointF[] { new PointF(8f, 4.5f), new PointF(16.5f, 9f), new PointF(8f, 13.5f) });
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
            public Action<bool> OnSpin;        // 一体化微调框: true=上三角(+1) false=下三角(-1)
            public bool IsToggle, On, Enabled = true;
            public bool IsSpinner;             // PixPin 同款: 左半当前值文字 + 右半上▲下▼三角
            public Image Preview;              // 下拉按钮的当前预览图 (非空: 画图+右侧▾)
            public Rectangle Rect;
            public Color Swatch = Color.Empty; // 颜色圆点按钮 (非空时画色块而非图标)
            public string DrawStr;             // 文字按钮 (非空时画文字而非图标, 如字号/字体当前值)
            public bool Visible = true;        // 属性栏按工具显隐 (PixPin 式), false 时 Relayout/Hit 跳过
            public string[] ForTools;          // 仅这些工具可见; null = 全工具可见
            public int GridKind;               // 0=普通; 1=箭头样式两行网格(图2); 2=线宽竖排列表(图1)
            public Action<int> OnPick;         // 点中第 N 格(样式=箭头style值; 列表=行号)
            public int[] Values;               // GridKind1: 格位->style值; GridKind2: 行号->width*10
            public Func<bool> IsSelGrid;       // 第 N 格是否选中 (Func 闭包带 gi? 简化: Func<int,bool>)
            public System.Func<int, bool> SelAt; // 格位/行号 -> 是否当前选中
        }

        public Btn AddText(string str, string tipText, Action onClick, string[] forTools = null)
        {
            Btn b = new Btn { Icon = "#text", DrawStr = str, Tip = tipText, OnClick = onClick, ForTools = forTools };
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        // PixPin 同款一体化微调框: 左半显示当前值, 右半上▲/下▼三角 (点击上半+1 下半-1)
        public Btn AddSpinner(string str, string tipText, Action<bool> onSpin, string[] forTools = null)
        {
            Btn b = new Btn { Icon = "#text", DrawStr = str, Tip = tipText, IsSpinner = true, OnSpin = onSpin, ForTools = forTools };
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        public Btn AddColor(Color c, string tipText, Action onClick, string[] forTools = null)
        {
            Btn b = new Btn { Icon = "color", Tip = tipText, OnClick = onClick, Swatch = c, ForTools = forTools };
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        public readonly List<Btn> Btns = new List<Btn>();
        int hoverIdx = -1;
        readonly ToolTip tip = new ToolTip();
        string shownTip;

        public ToolbarPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(40, 41, 46); // 与 StylePopup 弹层同色 (原 26,27,31 近黑 → 与弹层深灰叠出"白黑套色"两层色)
            // tooltip 字号跟控件 Font 走, 不钉死就会继承父窗体的字体 —— 老大实测提示文字变得很大
            Font = new Font("Microsoft YaHei UI", 9f);
        }

        public Btn Add(string icon, string tipText, Action onClick, bool toggle = false, string[] forTools = null)
        {
            Btn b = new Btn();
            b.Icon = icon; b.Tip = tipText; b.OnClick = onClick; b.IsToggle = toggle; b.ForTools = forTools;
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        // ---- 录制模式 (PixPin 式): 常规按钮全藏, 只留 [计时][■停止][✕取消]; 停止后遮罩直接关 ----
        List<Btn> savedBtns;
        Btn recTimeBtn;
        public void EnterRecMode(Action onStop, Action onCancel)
        {
            savedBtns = new List<Btn>(Btns);
            Btns.Clear();
            recTimeBtn = new Btn { Icon = "#text", DrawStr = "00:00", Tip = "录制时长" };
            Btns.Add(recTimeBtn);
            Btns.Add(new Btn { Icon = "stop", Tip = "停止录制并保存 (托盘菜单也可停)", OnClick = onStop });
            Btns.Add(new Btn { Icon = "cancel", Tip = "取消录制, 不保存", OnClick = onCancel });
            Relayout(); Invalidate();
        }

        public void RecTick()
        {
            try
            {
                if (recTimeBtn == null) return;
                var el = DateTime.Now - recStart; // 外层 ShotService 静态
                recTimeBtn.DrawStr = ((int)el.TotalSeconds / 60).ToString("00") + ":" + ((int)el.TotalSeconds % 60).ToString("00");
                Invalidate();
            }
            catch { }
        }

        // 箭头样式两行网格 (PixPin 图2): 6 格 (5+1), 选中蓝底, 格内画放大的样式预览
        public Btn AddStyleGrid(string[] forTools, int[] styleOrder, Action<int> onPick, System.Func<int, bool> selAt)
        {
            Btn b = new Btn { Icon = "grid", Tip = "箭头样式", GridKind = 1, Values = styleOrder, OnPick = onPick, ForTools = forTools, SelAt = selAt };
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        // 线宽竖排列表 (PixPin 图1): 3 行, 每行一条放大加粗的预览线, 选中行高亮
        public Btn AddWidthList(string[] forTools, float[] widths, Action<int> onPick, System.Func<int, bool> selAt)
        {
            Btn b = new Btn { Icon = "wlist", Tip = "线条粗细", GridKind = 2, Values = Array.ConvertAll(widths, w => (int)(w * 10)), OnPick = onPick, ForTools = forTools, SelAt = selAt };
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        // 下拉按钮: 左侧当前预览图 + 右侧▾; 点击弹菜单由调用方闭包实现 (菜单项带长预览图)
        public Btn AddPreviewDropdown(string tipText, Action onClick, string[] forTools = null)
        {
            Btn b = new Btn { Icon = "#prev", Tip = tipText, OnClick = onClick, ForTools = forTools };
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        // 旧 6 样式预览 (兼容): 细尾/空心走真实锥形预览, 其余映射到 PixPin 头型
        public static Bitmap MakeStylePreviewBmp(int style, bool forMenu)
        {
            int end = 2, start = 0; bool callout = false; int shape = 0;
            if (style == Annot.S_BOTH) { start = 2; end = 2; }
            else if (style == Annot.S_LINE) { start = 0; end = 0; }
            else if (style == Annot.S_CALLOUT) { callout = true; }
            else if (style == Annot.S_THIN) { shape = Annot.S_THIN; }
            else if (style == Annot.S_HOLLOW) { shape = Annot.S_HOLLOW; }
            else { start = 0; end = 2; }
            return MakeArrowPreviewBmp(start, end, false, 0, callout, shape, forMenu);
        }

        // 预览专用简化图标 (用户实测三次反馈: 源码几何缩到预览尺寸就是"胖图标", 看不清)
        // 原则: 不求代码源码画法, 只求一眼分清 14 种样式 — 粗线 + 大头 + 高对比
        //   key: 0直线 12五角 1开口 2实三角大 3实三角小 4实圆 5空圆 6实菱 7空菱 8刻度 9十字 14细尾实 15细尾空 99标注线
        public static void DrawStyleGlyph(Graphics g, RectangleF box, int key, Color ink, bool doubleEnds)
        {
            float cy = box.Y + box.Height / 2f;
            float m = Math.Max(4f, box.Width * 0.07f);
            float x1 = box.X + m, x2 = box.Right - m;
            float sw = Math.Max(2f, Math.Min(2.6f, box.Height * 0.09f)); // 粗线: 小尺寸也看得见
            float sc = Math.Max(0.73f, Math.Min(1f, 22f / Math.Max(1f, box.Height))); // 盒高缩放: 按钮盒(22)=1.0 基准, 下拉行盒(30)缩至 0.73 (行宽同尺寸显大)

            if (key == 99) // 标注线: 横线 + 两端短竖杠
            {
                using (Pen p = new Pen(ink, sw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, x1, cy, x2, cy); }
                float bl = Math.Min(9f, box.Height * 0.32f) * sc;
                using (Pen p = new Pen(ink, sw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLine(p, x1, cy - bl, x1, cy + bl); g.DrawLine(p, x2, cy - bl, x2, cy + bl); }
                return;
            }
            if (key == 14 || key == 15) // 细尾: 粗楔形 (实心填充/空心描边)
            {
                float hl = Math.Max(12f, (x2 - x1) * 0.22f) * sc;
                float hh = hl * 0.5f;
                PointF[] poly = new PointF[] {
                    new PointF(x1, cy),
                    new PointF(x2 - hl * 0.95f, cy - hh * 0.32f),
                    new PointF(x2 - hl, cy - hh),
                    new PointF(x2, cy),
                    new PointF(x2 - hl, cy + hh),
                    new PointF(x2 - hl * 0.95f, cy + hh * 0.32f) };
                if (key == 14) { using (SolidBrush b = new SolidBrush(ink)) g.FillPolygon(b, poly); }
                else using (Pen p = new Pen(ink, sw)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, poly); }
                return;
            }

            float back = GlyphHeadBack(key) * sc;
            float sx1 = doubleEnds ? x1 + back : x1, sx2 = x2 - back;
            using (Pen p = new Pen(ink, sw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, sx1, cy, sx2, cy); }
            if (key != 0)
            {
                DrawGlyphHead(g, key, x2, cy, 1f, ink, sw, sc);
                if (doubleEnds) DrawGlyphHead(g, key, x1, cy, -1f, ink, sw, sc);
            }
        }

        static float GlyphHeadBack(int key)
        {
            switch (key)
            {
                case 1: return 13f; case 2: return 16f; case 3: return 10f;
                case 4: case 5: return 14f; case 6: case 7: return 18f;
                case 8: return 7f; case 9: return 14f; case 12: return 17f;
                default: return 0f;
            }
        }

        // 头型本地坐标: 尖在原点, 头体沿 -x 展开; dir=+1 朝右 / -1 朝左
        static PointF GPt(float tipX, float tipY, float dir, float lx, float ly)
        {
            return new PointF(tipX + dir * lx, tipY + ly);
        }

        static void DrawGlyphHead(Graphics g, int key, float tipX, float tipY, float dir, Color ink, float sw, float sc)
        {
            switch (key)
            {
                case 1: // 开口 V
                    using (Pen p = new Pen(ink, sw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                        g.DrawLine(p, GPt(tipX, tipY, dir, -13 * sc, -8 * sc), GPt(tipX, tipY, dir, 0, 0));
                        g.DrawLine(p, GPt(tipX, tipY, dir, 0, 0), GPt(tipX, tipY, dir, -13 * sc, 8 * sc)); }
                    break;
                case 2: case 3: // 实心三角 (大/小)
                {
                    float hl = (key == 2 ? 16f : 10f) * sc, hh = (key == 2 ? 9f : 6f) * sc;
                    using (SolidBrush b = new SolidBrush(ink))
                        g.FillPolygon(b, new PointF[] { GPt(tipX, tipY, dir, 0, 0), GPt(tipX, tipY, dir, -hl, -hh), GPt(tipX, tipY, dir, -hl, hh) });
                    break;
                }
                case 4: case 5: // 实心圆/空心圆
                {
                    float r = 6.5f * sc;
                    RectangleF rc = new RectangleF(tipX + dir * (-7f * sc) - r, tipY - r, r * 2, r * 2);
                    if (key == 4) { using (SolidBrush b = new SolidBrush(ink)) g.FillEllipse(b, rc); }
                    else using (Pen p = new Pen(ink, Math.Max(1.8f, sw * 0.85f))) g.DrawEllipse(p, rc);
                    break;
                }
                case 6: case 7: // 实心菱形/空心菱形
                {
                    PointF[] poly = new PointF[] { GPt(tipX, tipY, dir, 0, 0), GPt(tipX, tipY, dir, -9 * sc, -7 * sc), GPt(tipX, tipY, dir, -18 * sc, 0), GPt(tipX, tipY, dir, -9 * sc, 7 * sc) };
                    if (key == 6) { using (SolidBrush b = new SolidBrush(ink)) g.FillPolygon(b, poly); }
                    else using (Pen p = new Pen(ink, Math.Max(1.8f, sw * 0.85f))) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, poly); }
                    break;
                }
                case 8: // 刻度线
                    using (Pen p = new Pen(ink, sw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawLine(p, GPt(tipX, tipY, dir, -6 * sc, -7 * sc), GPt(tipX, tipY, dir, -6 * sc, 7 * sc)); }
                    break;
                case 9: // 十字
                    using (Pen p = new Pen(ink, sw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawLine(p, GPt(tipX, tipY, dir, -8 * sc, -8 * sc), GPt(tipX, tipY, dir, -8 * sc, 8 * sc));
                        g.DrawLine(p, GPt(tipX, tipY, dir, -14 * sc, 0), GPt(tipX, tipY, dir, -2 * sc, 0)); }
                    break;
                case 12: // 五角
                {
                    PointF[] star = new PointF[10];
                    for (int i = 0; i < 10; i++)
                    {
                        double ang = -Math.PI / 2 + i * Math.PI / 5;
                        float r = ((i % 2 == 0) ? 8f : 3.4f) * sc;
                        star[i] = new PointF(tipX + dir * (-9f * sc) + (float)Math.Cos(ang) * r, tipY + (float)Math.Sin(ang) * r);
                    }
                    using (Pen p = new Pen(ink, Math.Max(1.8f, sw * 0.85f))) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, star); }
                    break;
                }
            }
        }

        // 箭头预览图 (属性栏按钮 + 菜单项共用): 简化图标, 一眼分清 (画布仍走源码几何, 预览不走)
        public static Bitmap MakeArrowPreviewBmp(int startType, int endType, bool smooth, int dash, bool callout, int shape, bool forMenu)
        {
            int W = forMenu ? 76 : 58, H = forMenu ? 22 : 22;
            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(40, 41, 46));
                Color c = Color.FromArgb(232, 234, 240);
                int key; bool dbl = false;
                if (callout) key = 99;
                else if (shape == Annot.S_THIN) key = 14;
                else if (shape == Annot.S_HOLLOW) key = 15;
                else { key = endType; dbl = (startType != 0); }
                DrawStyleGlyph(g, new RectangleF(0, 0, W, H), key, c, dbl);
            }
            return bmp;
        }

        // 虚线样式预览 (属性栏按钮): 与画布同一 dash 数组
        public static Bitmap MakeDashPreviewBmp(int dash, bool forMenu)
        {
            int W = forMenu ? 76 : 58, H = forMenu ? 22 : 22;
            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(40, 41, 46));
                using (Pen p = new Pen(Color.FromArgb(232, 234, 240), 2.8f))
                {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round; p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    PixPinArrowGeom.ApplyDash(p, dash);
                    g.DrawLine(p, 4, H / 2f, W - 4, H / 2f);
                }
            }
            return bmp;
        }

        // 闭合填充预览 (属性栏按钮): 一个实心不规则多边形, 一眼看出"填充"是啥效果
        public static Bitmap MakeFillPreviewBmp(bool forMenu)
        {
            int W = forMenu ? 76 : 58, H = forMenu ? 22 : 22;
            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(40, 41, 46));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(232, 234, 240)))
                    g.FillPolygon(b, new PointF[] {
                        new PointF(5, H - 5), new PointF(12, 5), new PointF(W * 0.45f, 9),
                        new PointF(W * 0.62f, 4), new PointF(W - 5, H - 6) });
            }
            return bmp;
        }

        // 线宽预览 (竖排菜单每行一条按实际粗细的长预览线)
        public static Bitmap MakeWidthPreviewBmp(float lw, bool forMenu)
        {
            int W = forMenu ? 76 : 58, H = forMenu ? 22 : 22;
            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(40, 41, 46)); // 同菜单底色
                float cy = H / 2f;
                using (Pen p = new Pen(Color.FromArgb(232, 234, 240), lw))
                {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round; p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawLine(p, 3, cy, W - 3, cy);
                }
            }
            return bmp;
        }

        public Btn AddLabel(string str, string[] forTools = null)
        {
            // 分组标签 (PixPin "大小"式): 灰字不可点, OnClick=null 天然无害
            Btn b = new Btn { Icon = "#text", DrawStr = str, Tip = str, OnClick = null, Enabled = false, ForTools = forTools };
            Btns.Add(b); Relayout(); Invalidate(); return b;
        }

        public void AddSep(string[] forTools = null)
        {
            Btns.Add(new Btn { Icon = "|", ForTools = forTools });
            Relayout(); Invalidate();
        }

        // 按工具显隐 (PixPin 式: 属性栏只显示当前工具相关属性); 收敛悬空分隔线
        public void ShowForTool(string tool)
        {
            int visN = 0;
            foreach (Btn b in Btns)
            {
                b.Visible = (tool == null) || b.ForTools == null || Array.IndexOf(b.ForTools, tool) >= 0;
                if (b.Visible && b.Icon != "|") visN++;
            }
            Log("propbar show " + tool + " visible=" + visN);
            for (int pass = 0; pass < 2; pass++) // 两轮收敛相邻 sep
            {
                int lastVis = -1;
                for (int i = 0; i < Btns.Count; i++)
                {
                    Btn b = Btns[i];
                    if (b.Icon == "|")
                    {
                        if (!b.Visible) continue;
                        bool gap = false;
                        for (int k = lastVis + 1; k < i; k++) if (Btns[k].Visible && Btns[k].Icon != "|") { gap = true; break; }
                        if (lastVis < 0 || !gap) b.Visible = false;
                    }
                    else if (b.Visible) lastVis = i;
                }
                bool tailSeen = false;
                for (int i = Btns.Count - 1; i >= 0; i--)
                {
                    Btn b = Btns[i];
                    if (b.Icon == "|") { if (!tailSeen) b.Visible = false; }
                    else if (b.Visible) tailSeen = true;
                }
            }
            Relayout(); Invalidate();
        }

        void Relayout()
        {
            int x = 6;
            Font tf = null;
            foreach (Btn b in Btns)
            {
                if (!b.Visible) { b.Rect = Rectangle.Empty; continue; } // 按工具隐藏: 不占位
                if (b.Icon == "|") { b.Rect = new Rectangle(x, Height > 48 ? 20 : 12, 1, Height > 48 ? 48 : 24); x += 13; }
                else if (b.Icon == "#prev") { int pw = b.Preview != null ? b.Preview.Width + 22 : 64; b.Rect = new Rectangle(x, 4, Math.Max(56, pw), 40); x += Math.Max(56, pw); }
                else if (b.Icon == "#text" && !string.IsNullOrEmpty(b.DrawStr))
                {
                    // 文字按钮按实际文字宽布局, 否则"从12"/"I.II.III"被裁成"从1"/"I.II"
                    if (tf == null) tf = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
                    int w;
                    try { w = Math.Max(40, TextRenderer.MeasureText(b.DrawStr, tf).Width + 16 + (b.IsSpinner ? 26 : 0)); }
                    catch { w = 40; }
                    b.Rect = new Rectangle(x, 4, w, 40); x += w;
                }
                else { b.Rect = new Rectangle(x, 4, 40, 40); x += 40; }
            }
            if (tf != null) try { tf.Dispose(); } catch { }
            bool tall = false;
            foreach (Btn b in Btns) if (b.Visible && b.GridKind != 0) { tall = true; break; }
            Width = x + 6; Height = tall ? 88 : 48;
            foreach (Btn b in Btns) // 普通按钮垂直居中 (面板变高时)
                if (b.Visible && b.GridKind == 0 && b.Icon != "|" && b.Icon != "#text" && !string.IsNullOrEmpty(b.Icon) && b.Icon != "color" && b.Rect.Height == 40)
                    b.Rect = new Rectangle(b.Rect.X, (Height - b.Rect.Height) / 2, b.Rect.Width, b.Rect.Height);
            foreach (Btn b in Btns)
                if (b.Visible && b.GridKind == 0 && b.Icon != "|" && b.Rect.Height == 40 && (b.Icon == "color" || b.Icon == "#text"))
                    b.Rect = new Rectangle(b.Rect.X, (Height - 40) / 2, b.Rect.Width, 40);
            // 宽度变化后钳回父窗内, 防右端按钮(从N/▲▼)被推出屏幕 — 用户实测"10显示不出来"的真凶
            if (Parent != null)
            {
                if (Left + Width > Parent.ClientSize.Width) Left = Parent.ClientSize.Width - Width;
                if (Left < 0) Left = 0;
            }
        }

        public void RelayoutNow() { Relayout(); Invalidate(); }

        public void SetEnabledAll(bool en)
        {
            foreach (Btn b in Btns) b.Enabled = en;
        }

        int Hit(Point p)
        {
            for (int i = 0; i < Btns.Count; i++)
            {
                Btn b = Btns[i];
                if (b.Visible && b.Icon != "|" && b.Rect.Contains(p)) return i;
            }
            return -1;
        }

        // 样式预览 (网格格内, 加长放大): 在 cell 中央画 -16..+16 的线型
        static void DrawStylePreview(Graphics g, Rectangle cell, int style, Color c)
        {
            float cx = cell.X + cell.Width / 2f, cy = cell.Y + cell.Height / 2f;
            float x1 = cx - 15, x2 = cx + 15;
            using (Pen p = new Pen(c, 2.4f))
            {
                switch (style)
                {
                    case Annot.S_ARROW:
                        g.DrawLine(p, x1, cy, x2 - 7, cy);
                        using (SolidBrush b = new SolidBrush(c))
                            g.FillPolygon(b, new PointF[] { new PointF(x2, cy), new PointF(x2 - 9f, cy - 5.5f), new PointF(x2 - 9f, cy + 5.5f) });
                        break;
                    case Annot.S_BOTH:
                        g.DrawLine(p, x1 + 7, cy, x2 - 7, cy);
                        using (SolidBrush b = new SolidBrush(c))
                        {
                            g.FillPolygon(b, new PointF[] { new PointF(x2, cy), new PointF(x2 - 9f, cy - 5.5f), new PointF(x2 - 9f, cy + 5.5f) });
                            g.FillPolygon(b, new PointF[] { new PointF(x1, cy), new PointF(x1 + 9f, cy - 5.5f), new PointF(x1 + 9f, cy + 5.5f) });
                        }
                        break;
                    case Annot.S_LINE:
                        g.DrawLine(p, x1, cy, x2, cy);
                        break;
                    case Annot.S_CALLOUT:
                        g.DrawLine(p, x1 + 4, cy, x2 - 4, cy);
                        g.DrawLine(p, x1 + 4, cy - 7, x1 + 4, cy + 7);
                        g.DrawLine(p, x2 - 4, cy - 7, x2 - 4, cy + 7);
                        break;
                    case Annot.S_THIN: // 细尾锥形
                        using (SolidBrush b = new SolidBrush(c))
                            g.FillPolygon(b, new PointF[] { new PointF(x1, cy - 1.2f), new PointF(x1, cy + 1.2f), new PointF(x2, cy - 6f), new PointF(x2, cy + 6f) });
                        break;
                    case Annot.S_HOLLOW: // 空心: 细杆 + 描边箭头
                        g.DrawLine(p, x1, cy, x2 - 8, cy);
                        using (Pen hp = new Pen(c, 2f))
                            g.DrawPolygon(hp, new PointF[] { new PointF(x2 - 9f, cy - 6.5f), new PointF(x2, cy), new PointF(x2 - 9f, cy + 6.5f) });
                        break;
                }
            }
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
                if (b.Icon == "|" || !b.Visible) continue;
                if ((i == hoverIdx || b.On) && b.GridKind == 0)
                {
                    using (SolidBrush br = new SolidBrush(b.On ? Color.FromArgb(58, 62, 74) : (i == hoverIdx ? Color.FromArgb(64, 68, 80) : Color.Transparent)))
                        RoundFill(g, br, b.Rect, 8);
                    if (b.On && b.Swatch != Color.Empty)
                    { // 色块选中: 白圈描边 (主色高亮底会吃掉色块对比)
                        using (Pen ring = new Pen(Color.FromArgb(235, 238, 244), 2f))
                            g.DrawEllipse(ring, b.Rect.X + 7, b.Rect.Y + 7, 26, 26);
                    }
                }
                if (b.GridKind == 1)
                {
                    // 箭头样式网格 (图2): 格 40x40, 5 列两行, 选中格蓝底, 预览线加长
                    for (int gi = 0; gi < b.Values.Length; gi++)
                    {
                        int col = gi % 5, row = gi / 5;
                        Rectangle cell = new Rectangle(b.Rect.X + col * 40, b.Rect.Y + row * 40, 40, 40);
                        bool sel = b.SelAt != null && b.SelAt(gi);
                        if (sel) { using (SolidBrush hb = new SolidBrush(Color.FromArgb(52, 122, 214))) RoundFill(g, hb, cell, 6); }
                        DrawStylePreview(g, cell, b.Values[gi], sel ? Color.White : Color.FromArgb(222, 226, 232));
                    }
                    continue;
                }
                if (b.GridKind == 2)
                {
                    // 线宽竖排列表 (图1): 3 行, 选中行高亮, 每行一条按实际宽度的长预览线
                    int rows = b.Values.Length, rh = b.Rect.Height / rows;
                    for (int ri = 0; ri < rows; ri++)
                    {
                        Rectangle rowR = new Rectangle(b.Rect.X, b.Rect.Y + ri * rh, b.Rect.Width, rh);
                        float wv = b.Values[ri] / 10f;
                        bool sel = b.SelAt != null && b.SelAt(ri);
                        if (sel) { using (SolidBrush hb = new SolidBrush(Color.FromArgb(52, 122, 214))) RoundFill(g, hb, rowR, 6); }
                        using (Pen lp = new Pen(sel ? Color.White : Color.FromArgb(222, 226, 232), wv))
                        {
                            lp.StartCap = System.Drawing.Drawing2D.LineCap.Round; lp.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                            g.DrawLine(lp, rowR.X + 8, rowR.Y + rh / 2f, rowR.Right - 8, rowR.Y + rh / 2f);
                        }
                    }
                    continue;
                }
                if (b.Icon == "#prev")
                {
                    if (b.Preview != null)
                        g.DrawImage(b.Preview, b.Rect.X + 4, b.Rect.Y + (b.Rect.Height - b.Preview.Height) / 2);
                    using (Pen dp = new Pen(Color.FromArgb(200, 204, 212), 1.6f))
                    {
                        float dx = b.Rect.Right - 11;
                        g.DrawPolygon(dp, new PointF[] { new PointF(dx, b.Rect.Y + 17), new PointF(dx + 7, b.Rect.Y + 17), new PointF(dx + 3.5f, b.Rect.Y + 24) });
                    }
                    continue;
                }
                if (b.Swatch != Color.Empty)
                {
                    using (SolidBrush sb = new SolidBrush(b.Swatch))
                        g.FillEllipse(sb, b.Rect.X + 10, b.Rect.Y + 10, 20, 20);
                    if (!b.Enabled)
                        using (SolidBrush dim = new SolidBrush(Color.FromArgb(140, 40, 41, 46)))
                            g.FillEllipse(dim, b.Rect.X + 10, b.Rect.Y + 10, 20, 20);
                    continue;
                }
                if (b.IsSpinner)
                {
                    // PixPin 同款: 左半当前值文字 + 右半上▲下▼实心三角
                    Color tc = b.Enabled ? Color.FromArgb(232, 234, 240) : Color.FromArgb(110, 114, 120);
                    Rectangle textR = new Rectangle(b.Rect.X + 2, b.Rect.Y, b.Rect.Width - 28, b.Rect.Height);
                    TextRenderer.DrawText(g, b.DrawStr, new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold), textR, tc,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    int cx = b.Rect.Right - 15;
                    using (SolidBrush tb = new SolidBrush(tc))
                    {
                        g.FillPolygon(tb, new Point[] { new Point(cx - 6, b.Rect.Y + 15), new Point(cx + 6, b.Rect.Y + 15), new Point(cx, b.Rect.Y + 7) });
                        g.FillPolygon(tb, new Point[] { new Point(cx - 6, b.Rect.Bottom - 15), new Point(cx + 6, b.Rect.Bottom - 15), new Point(cx, b.Rect.Bottom - 7) });
                    }
                    continue;
                }
                if (b.DrawStr != null)
                {
                    TextRenderer.DrawText(g, b.DrawStr, new Font("Microsoft YaHei UI", 10.5f, b.Enabled ? FontStyle.Bold : FontStyle.Regular), b.Rect,
                                          b.Enabled ? Color.FromArgb(232, 234, 240) : Color.FromArgb(128, 132, 140),
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
                if (b.Enabled && b.GridKind == 1 && b.OnPick != null)
                {
                    int col = (e.X - b.Rect.X) / 40, row = (e.Y - b.Rect.Y) / 40, gi = row * 5 + col;
                    if (gi >= 0 && gi < b.Values.Length) { b.OnPick(b.Values[gi]); Log("style pick " + b.Values[gi]); }
                }
                else if (b.Enabled && b.GridKind == 2 && b.OnPick != null)
                {
                    int ri = (e.Y - b.Rect.Y) / (b.Rect.Height / b.Values.Length);
                    if (ri >= 0 && ri < b.Values.Length) { b.OnPick(ri); Log("width pick " + b.Values[ri]); }
                }
                else if (b.Enabled && b.IsSpinner && b.OnSpin != null)
                    b.OnSpin(e.Y < b.Rect.Top + b.Rect.Height / 2); // 上半=+1 下半=-1
                else if (b.Enabled && b.OnClick != null) b.OnClick();
            }
        }
    }

    // 语言检测: 按 CJK 占比判 zh/en/mixed —— 点翻译自动反向翻译 (中文→英, 英文→中), 混合让用户选
    // 序号徽章矩形: 宽=文字宽+边距, 上限1.25倍高(近圆); 超限长串(罗马)由绘制时水平压缩兜底
    static Rectangle SeqBadgeRect(Annot a)
    {
        string s = SeqText(a.No, a.SeqFmt);
        int h = a.Rect.Height;
        int w = h;
        try
        {
            using (Font f = new Font(a.FontFamily, a.FontPt, FontStyle.Bold))
                w = Math.Max(h, Math.Min(h * 5 / 4, TextRenderer.MeasureText(s, f).Width + 12));
        }
        catch { }
        return new Rectangle(a.Rect.X + (a.Rect.Width - w) / 2, a.Rect.Y, w, h);
    }

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
        // ===== 资源台账 (P0-3, 抄 web-access 的"托管资源有人收"): 每个贴图窗都入账 =====
        // 原则: 只记自己造的垃圾, 绝不按"目录里所有旧文件"扫射 —— 截图目录是你的资产。
        // 默认**只记账不关窗**(不擅自把你正在看的图关掉); 要自动回收需显式配 gc.pin_ttl_ms。
        public static readonly Dictionary<IntPtr, DateTime> Ledger = new Dictionary<IntPtr, DateTime>();
        public static int ClosedCount = 0;   // 自然关闭数(用户双击/菜单关)
        public static int GcClosedCount = 0; // 被回收器关掉的数量

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
            // 句柄到手才入账; 关闭时销账并**主动释放位图**(BackgroundImage 不会被 WinForms 自动 Dispose, 原来会漏 GDI 句柄)
            this.HandleCreated += delegate { lock (Ledger) { Ledger[this.Handle] = DateTime.Now; } };
            this.FormClosed += delegate
            {
                lock (Ledger) { if (Ledger.Remove(this.Handle)) { } ClosedCount++; }
                try { BackgroundImage = null; img.Dispose(); } catch { }
            };

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
        readonly string tsvText;   // 非空 = 底栏多一个「贴 Excel」按钮 (复制 TSV)
        Button tsvBtn;

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // 阴影
        }

        public ResultForm(string title, string text) : this(title, text, null) { }

        public ResultForm(string title, string text, string tsv)
        {
            this.text = text;
            tsvText = tsv;
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
            Label info = new Label(); info.Text = (tsvText != null ? "已自动复制 Markdown · " : "已自动复制到剪贴板 · ") + "Ctrl+C 再复制 · Esc 关闭"; info.Left = 14;
            info.AutoSize = true; info.ForeColor = DarkUI.TextDim; info.Font = new Font("Microsoft YaHei UI", 9f);
            info.Dock = DockStyle.Fill;
            copyBtn = new Button(); copyBtn.Text = "复制"; copyBtn.FlatStyle = FlatStyle.Flat; copyBtn.FlatAppearance.BorderSize = 0;
            copyBtn.BackColor = DarkUI.Accent; copyBtn.ForeColor = DarkUI.Text;
            copyBtn.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            copyBtn.Size = new Size(110, 34); copyBtn.Cursor = Cursors.Hand;
            copyBtn.Dock = DockStyle.Right;
            copyBtn.Click += (s, e) => CopyNow();
            bottom.Controls.Add(info); bottom.Controls.Add(copyBtn);
            if (tsvText != null)
            {
                tsvBtn = new Button(); tsvBtn.Text = "贴 Excel (TSV)"; tsvBtn.FlatStyle = FlatStyle.Flat; tsvBtn.FlatAppearance.BorderSize = 0;
                tsvBtn.BackColor = DarkUI.BgField; tsvBtn.ForeColor = DarkUI.Text;
                tsvBtn.Font = new Font("Microsoft YaHei UI", 9f);
                tsvBtn.Size = new Size(130, 34); tsvBtn.Cursor = Cursors.Hand;
                tsvBtn.Dock = DockStyle.Right;
                tsvBtn.Click += (s, e) =>
                {
                    try { Clipboard.SetText(tsvText); } catch { }
                    tsvBtn.Text = "✓ 已复制 TSV"; tsvBtn.BackColor = Color.FromArgb(40, 130, 80);
                };
                bottom.Controls.Add(tsvBtn);
            }
            Controls.Add(bottom);
            bottom.BringToFront();
            if (tsvBtn != null) tsvBtn.BringToFront();
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
