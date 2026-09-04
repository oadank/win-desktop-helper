using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

// 区域截图覆盖层 + 入口 (M1: 吸收 PixPin 截图能力第一步)
// 与 shot-service.cs 同属 ShotService 类(partial), 共享 Log/ShotDir/hkForm/TrayIcon 等成员
partial class ShotService
{
    const int SHOT_HOTKEY_ID = 0x5713; // 与 HOTKEY_ID(0x5712) 区分
    static string shotHotkeyName = "";
    static bool captureBusy = false;

    // 区域截图入口: 始终在 hk(STA) 线程显示覆盖层, 与热键窗同消息循环, 避开跨线程建窗体崩溃
    static void ShowCaptureOverlay()
    {
        if (captureBusy) return;
        Form hk = hkForm;
        if (hk == null || !hk.IsHandleCreated) { Log("capture: hotkey form not ready"); return; }
        if (hk.InvokeRequired) hk.Invoke(new MethodInvoker(ShowCaptureOverlayCore));
        else ShowCaptureOverlayCore();
    }

    static void ShowCaptureOverlayCore()
    {
        captureBusy = true;
        try { using (var ov = new CaptureOverlay()) ov.ShowDialog(hkForm); }
        catch (Exception ex) { Log("capture overlay err: " + ex.Message); }
        finally { captureBusy = false; }
    }

    static void ShowTrayInfo(string msg)
    {
        try { if (TrayIcon != null) TrayIcon.ShowBalloonTip(1500, "Win Desktop Helper", msg, ToolTipIcon.Info); }
        catch { }
    }

    // 全屏半透明框选窗: 拖框选区域, 松开自动存盘+复制图片; Ctrl+S 另存为; C 仅复制; Esc/右键取消
    class CaptureOverlay : Form
    {
        Point start;
        Rectangle sel;
        bool dragging = false;
        bool hasSel = false;

        public CaptureOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            Bounds = SystemInformation.VirtualScreen; // 覆盖全部显示器(含负坐标副屏)
            TopMost = true;
            Opacity = 0.35;
            BackColor = Color.DarkGray;
            TransparencyKey = Color.DarkGray; // 自身暗色层不参与截图
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                start = PointToScreen(e.Location);
                sel = new Rectangle(start, Size.Empty);
                dragging = true;
                hasSel = false;
                Invalidate();
            }
            else { DialogResult = DialogResult.Cancel; Close(); } // 右键取消
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                sel = Normalize(start, PointToScreen(e.Location));
                hasSel = sel.Width > 2 && sel.Height > 2;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging && hasSel) { dragging = false; FinishCapture(sel); }
            else { dragging = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (hasSel)
            {
                Rectangle d = RectangleToClient(sel); // 屏绝对坐标 → 客户区坐标(因 Bounds=VirtualScreen)
                using (Pen p = new Pen(Color.Red, 2)) e.Graphics.DrawRectangle(p, d);
                string txt = sel.Width + " x " + sel.Height;
                using (Font f = new Font("Consolas", 12))
                using (Brush b = new SolidBrush(Color.Yellow))
                    e.Graphics.DrawString(txt, f, b, d.Right + 4, d.Bottom + 4);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (keyData == (Keys.Control | Keys.S)) { if (hasSel) SaveAs(sel); return true; }
            if (keyData == Keys.C) { if (hasSel) CopyImage(sel); return true; }
            if (keyData == Keys.Enter) { if (hasSel) FinishCapture(sel); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        static Rectangle Normalize(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        static Bitmap CaptureRect(Rectangle r)
        {
            Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
            return bmp;
        }

        static string SaveToShotDir(Bitmap bmp)
        {
            try { if (!System.IO.Directory.Exists(ShotDir)) System.IO.Directory.CreateDirectory(ShotDir); } catch { }
            string name = "shot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".png";
            string path = System.IO.Path.Combine(ShotDir, name);
            bmp.Save(path, ImageFormat.Png);
            return path;
        }

        void FinishCapture(Rectangle r)
        {
            try
            {
                using (Bitmap bmp = CaptureRect(r))
                {
                    string path = SaveToShotDir(bmp);
                    Clipboard.SetImage(bmp); // 复制图片到剪贴板(后续 M2 OCR 可在此拦截回填文本)
                    Log("capture saved: " + path + " (image copied to clipboard)");
                    ShowTrayInfo("已截图: " + path);
                }
            }
            catch (Exception ex) { Log("capture finish err: " + ex.Message); }
            DialogResult = DialogResult.OK;
            Close();
        }

        void CopyImage(Rectangle r)
        {
            try
            {
                using (Bitmap bmp = CaptureRect(r))
                {
                    Clipboard.SetImage(bmp);
                    Log("capture copied to clipboard");
                    ShowTrayInfo("已复制图片到剪贴板");
                }
            }
            catch (Exception ex) { Log("capture copy err: " + ex.Message); }
        }

        void SaveAs(Rectangle r)
        {
            try
            {
                using (Bitmap bmp = CaptureRect(r))
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
                        bmp.Save(dlg.FileName, fmt);
                        Log("capture saved as: " + dlg.FileName);
                        ShowTrayInfo("已保存: " + dlg.FileName);
                    }
                }
            }
            catch (Exception ex) { Log("capture saveas err: " + ex.Message); }
        }
    }
}
