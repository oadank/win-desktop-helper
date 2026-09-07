using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// 长截图 (滚动拼接) — PixPin 同款交互, 2026-09-07 老大拍板:
//   框选区域 → 点工具条「长截图」→ 自动滚轮逐帧截取 → 底部条带匹配拼接 →
//   侧边实时预览(越拼越长) → 滚到底(相邻帧相似)/手点「完成」/Esc → 复制+保存。
// 第一批: 自动滚动模式。手动滚动+5s 超时判定第二批加(钩子里已有 PICK_WHEEL 时间戳)。
// 防坑(都在 PixPin 踩过): 每次滚动后等 280ms 让惯性停稳再截帧, 否则拼缝对不上;
//   帧间无位移(stall)连续 6 次 ≈ 1.8s 判定"到底了"自动收图。
partial class ShotService
{
    static volatile int lsBusy;   // 防重入: 长截图进行中再点直接提示

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    // 入口: 截图工具条「长截图」按钮。r = 选区(屏幕坐标)
    static void StartLongShot(Rectangle r, Form overlay)
    {
        if (Interlocked.Exchange(ref lsBusy, 1) == 1) { ShowTrayInfo("长截图正在进行中"); return; }
        try { overlay.Close(); } catch { }        // 收掉遮罩工具条, 屏幕还原给目标页面
        Thread t = new Thread((ThreadStart)delegate { LongShotRun(r); });
        t.SetApartmentState(ApartmentState.STA);  // 结束要写剪贴板, 必须 STA
        t.Start();
        t.Join();                                 // 外层本来就是 ThreadPool 工作线程, 等 it 完再清理
        Interlocked.Exchange(ref lsBusy, 0);
    }

    static void LongShotRun(Rectangle r)
    {
        Bitmap result = null;
        string errMsg = null; bool cancelled = false; int screens = 0;
        try
        {
            SetCursorPos(r.X + r.Width / 2, r.Y + r.Height / 2);   // 滚轮发给选区中间的窗口
            Thread.Sleep(250);
            var st = new LongShotStatus(r);
            st.Show();
            Thread.Sleep(200);

            int W = r.Width, H = r.Height;
            Bitmap prev = SnapScreen(r);
            long canvasH = H * 8;
            Bitmap canvas = NewCanvas(W, (int)Math.Min(canvasH, int.MaxValue - 1000));
            using (var g = Graphics.FromImage(canvas)) g.DrawImage(prev, 0, 0);
            int filled = H;
            int stall = 0; screens = 1;
            var gPrev = Gray(prev);
            st.Update(canvas, filled, screens);

            bool finish = false;
            int lastO = 0;                                 // 上次滚动量(匹配先验: 滚轮恒速, 邻域搜索防重复行错配)
            while (!finish)
            {
                Application.DoEvents();                        // 泵状态窗消息(按钮/Esc 响应)
                if (st.Cancel) { cancelled = true; break; }
                if (st.Finish) break;
                if (screens > 200) break;                      // 硬上限: 防异常狂奔
                if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { cancelled = true; break; }
                MouseScroll(-240);                             // 向下滚 2 格: 步子小, 平滑滚动动画停得快
                Thread.Sleep(420);                             // 平滑滚动动画彻底停稳(Win11 记事本实测 280ms 不够)
                Application.DoEvents();
                using (Bitmap cur = SnapScreen(r))
                {
                    byte[] gCur = Gray(cur);
                    int o = MatchBand(gPrev, gCur, W, H, lastO);   // 本帧相对上帧滚动了 o 像素
                    Log("ls frame: o=" + o + " stall=" + stall);
                    if (o <= 0)
                    {
                        stall++;
                        gPrev = gCur;   // 吸收动画中间态: 下帧从最新画面起算, 否则真实滚动量会被丢一帧
                        if (stall >= 6) break;                 // ≈1.8s 没动 = 到底/静止, 自动收
                        continue;
                    }
                    lastO = o;
                    stall = 0; screens++;
                    if (filled + o > canvas.Height)
                    {
                        var big = NewCanvas(W, canvas.Height * 2);
                        using (var g = Graphics.FromImage(big)) g.DrawImage(canvas, 0, 0);
                        canvas.Dispose(); canvas = big;
                    }
                    using (var g = Graphics.FromImage(canvas))
                        g.DrawImage(cur, new Rectangle(0, filled, W, o),
                                    new Rectangle(0, 0, W, o), GraphicsUnit.Pixel);
                    filled += o;
                    gPrev = gCur;
                    st.Update(canvas, filled, screens);
                }
            }
            try { st.Invoke((MethodInvoker)delegate { st.Close(); }); } catch { }
            if (!cancelled)
            {
                result = new Bitmap(W, filled);
                using (var g = Graphics.FromImage(result)) g.DrawImage(canvas, 0, 0);
            }
            canvas.Dispose();
            prev.Dispose();
        }
        catch (Exception ex) { errMsg = ex.Message; }
        if (errMsg != null) { Log("longshot err: " + errMsg); ShowTrayInfo("长截图失败: " + errMsg); return; }
        if (cancelled || result == null) { ShowTrayInfo("长截图已取消" + (screens > 0 ? " (拼了 " + screens + " 屏)" : "")); return; }
        try
        {
            Clipboard.SetImage(result);
            string path = SaveToShotDir(result);
            Log("longshot done: " + result.Width + "x" + result.Height + " " + screens + " screens -> " + path);
            ShowTrayInfo("长截图完成 " + result.Width + "x" + result.Height + " (" + screens + " 屏), 已复制+保存");
        }
        catch (Exception ex) { Log("longshot out err: " + ex.Message); ShowTrayInfo("长截图拼好了但输出失败: " + ex.Message); }
        finally { result.Dispose(); }
    }

    static Bitmap SnapScreen(Rectangle r)
    {
        var b = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(b))
            g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
        return b;
    }
    static Bitmap NewCanvas(int w, int h) { var b = new Bitmap(w, h); using (var g = Graphics.FromImage(b)) g.Clear(Color.White); return b; }

    // 灰度化(步长2采样), 拼接匹配用
    static byte[] Gray(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var outp = new byte[(w / 2) * (h / 2)];
        var rect = new Rectangle(0, 0, w, h);
        var data = b.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                              System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride, ow = w / 2;
            var rowBuf = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                Marshal.Copy(data.Scan0 + (y * 2) * stride, rowBuf, 0, stride);
                int ro = y * ow;
                for (int x = 0; x < ow; x++)
                {
                    int i = (x * 2) * 3;
                    outp[ro + x] = (byte)((rowBuf[i] + rowBuf[i + 1] + rowBuf[i + 2]) / 3);
                }
            }
        }
        finally { b.UnlockBits(data); }
        return outp;
    }

    // 上一帧底带在当前帧中的垂直偏移 o (cur 内容 = prev 内容上滚 o 像素)
    // lastO>0 时优先在其 ±30(半分辨率) 邻域搜索 —— 滚轮恒速滚动, 全范围搜索会在
    // 重复文本(如逐行列表)上锁错行(实测 98 行直接跳到 104 行)。邻域最优仍差时回退全搜。
    // 返回 0 = 没滚动/匹配失败。粗搜步长2 + 精搜±2
    static int MatchBand(byte[] prev, byte[] cur, int w, int h, int lastO)
    {
        int gw = w / 2, gh = h / 2;
        int band = Math.Min(60, gh / 3);          // 底带高(半分辨率): 越宽越抗重复行错配
        int maxOff = gh - band - 2;
        int lo = 0, hi = maxOff;
        if (lastO > 0) { lo = Math.Max(0, lastO - 30); hi = Math.Min(maxOff, lastO + 30); }
        int best = 0; long bestScore = long.MaxValue;
        for (int o = lo; o <= hi; o += 2)
        {
            long s = BandDiff(prev, cur, gw, gh, band, o);
            if (s < bestScore) { bestScore = s; best = o; }
        }
        for (int o = Math.Max(lo, best - 2); o <= Math.Min(hi, best + 2); o++)
        {
            long s = BandDiff(prev, cur, gw, gh, band, o);
            if (s < bestScore) { bestScore = s; best = o; }
        }
        // ★ 邻域搜索的盲区: 两帧完全相同时任意 offset 的 SSD 都≈0, 邻域内永远"匹配成功"
        //   (实测: 滚到底后连拼 314 屏假帧)。所以先单测 o=0: 两帧本来就几乎一样 = 没滚动。
        long sZero = BandDiff(prev, cur, gw, gh, band, 0);
        if (sZero <= bestScore && sZero < (long)band * (gw / 2) * 4) return 0;
        // 匹配质量门槛: 底带自身方差太小说明是纯色区域, 匹配不可信 → 视为没滚动
        long varSum = 0; long tot = 0;
        int n = 0;
        for (int y = 0; y < band; y++)
            for (int x = 0; x < gw; x += 2) { tot += prev[(gh - band + y) * gw + x]; n++; }
        long avg = tot / n;
        for (int y = 0; y < band; y++)
            for (int x = 0; x < gw; x += 2) { long d = prev[(gh - band + y) * gw + x] - avg; varSum += d * d; }
        if (varSum < n * 25) return 0;                 // 近乎纯色的底带: 别瞎拼
        // 邻域最优质量差(平均平方差>30²) → 全范围重搜一次; 仍差 → 判定没滚动
        if (bestScore > (long)n * 900)
        {
            long fullBest = long.MaxValue; int fullO = 0;
            for (int o = 0; o <= maxOff; o += 2)
            {
                long s = BandDiff(prev, cur, gw, gh, band, o);
                if (s < fullBest) { fullBest = s; fullO = o; }
            }
            if (fullBest < bestScore) { bestScore = fullBest; best = fullO; }
            if (bestScore > (long)n * 900) return 0;
        }
        return best;
    }
    // 三带联合 SSD: 顶带+中带+底带一起比 —— 单带会在重复文本上锁错行(差一行 SSD 也低),
    // 三带同时错同一行数的概率极低
    static long BandDiff(byte[] prev, byte[] cur, int gw, int gh, int band, int o)
    {
        long s = 0;
        int mb = band / 2, mTop = gh / 2 - mb;             // 中带
        for (int y = 0; y < band; y++)
        {
            int r1B = (gh - band + y) * gw, r2B = (gh - band - o + y) * gw;
            int r1T = (o + y) * gw, r2T = y * gw;
            int r1M = (mTop + y) * gw, r2M = (mTop - o + y) * gw;
            for (int x = 0; x < gw; x += 2)
            {
                int dB = prev[r1B + x] - cur[r2B + x];
                int dT = r1T >= 0 ? prev[r1T + x] - cur[r2T + x] : 0;
                int dM = r2M >= 0 ? prev[r1M + x] - cur[r2M + x] : 0;
                s += dB * dB + dT * dT + dM * dM;
            }
        }
        return s;
    }

    // ---- 状态窗: 右侧贴着选区, 实时预览拼接结果 + 完成/取消按钮 + 进度文字 ----
    class LongShotStatus : Form
    {
        public volatile bool Finish, Cancel;
        PictureBox pv;
        Label info;
        Bitmap lastImg; int lastFilled;
        const int PW = 220, PH = 560;

        public LongShotStatus(Rectangle r)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(28, 29, 34);
            Size = new Size(PW, PH);
            int x = r.Right + 12, y = r.Top;
            var vs = SystemInformation.VirtualScreen;
            if (x + PW > vs.X + vs.Width) x = Math.Max(vs.X + 4, r.Left - PW - 12);
            y = Math.Max(vs.Y + 4, Math.Min(y, vs.Y + vs.Height - PH - 4));
            Location = new Point(x, y);
            Font = new Font("Microsoft YaHei UI", 9f);

            info = new Label();
            info.Text = "准备中…";
            info.ForeColor = Color.FromArgb(232, 234, 237);
            info.Dock = DockStyle.Top; info.Height = 26;
            info.TextAlign = ContentAlignment.MiddleLeft; info.Padding = new Padding(8, 4, 0, 0);
            Controls.Add(info);

            var btns = new Panel();
            btns.Dock = DockStyle.Bottom; btns.Height = 34; btns.BackColor = Color.FromArgb(28, 29, 34);
            var done = new Button();
            done.Text = "完成"; done.Size = new Size(90, 26); done.Location = new Point(PW / 2 - 96, 4);
            done.FlatStyle = FlatStyle.Flat; done.BackColor = Color.FromArgb(24, 110, 210);
            done.ForeColor = Color.White; done.FlatAppearance.BorderSize = 0;
            done.Click += delegate { Finish = true; };
            btns.Controls.Add(done);
            var cancel = new Button();
            cancel.Text = "取消 (Esc)"; cancel.Size = new Size(90, 26); cancel.Location = new Point(PW / 2 + 6, 4);
            cancel.FlatStyle = FlatStyle.Flat; cancel.BackColor = Color.FromArgb(52, 55, 64);
            cancel.ForeColor = Color.FromArgb(232, 234, 237); cancel.FlatAppearance.BorderSize = 0;
            cancel.Click += delegate { Cancel = true; };
            btns.Controls.Add(cancel);
            Controls.Add(btns);

            pv = new PictureBox();
            pv.Dock = DockStyle.Fill;
            pv.BackColor = Color.FromArgb(20, 21, 25);
            pv.SizeMode = PictureBoxSizeMode.Zoom;
            Controls.Add(pv);
            pv.BringToFront();
        }

        // 拼接画布更新(在采集线程调用; 位图归状态窗所有, 换图时释放旧图)
        public void Update(Bitmap canvas, int filled, int screens)
        {
            if (IsDisposed) return;
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    var crop = (Bitmap)canvas.Clone(new Rectangle(0, 0, canvas.Width, Math.Max(1, filled)), canvas.PixelFormat);
                    var old = lastImg; lastImg = crop; lastFilled = filled;
                    pv.Image = crop;
                    if (old != null) old.Dispose();
                    info.Text = "已拼接 " + screens + " 屏 · 高 " + filled + "px · 滚动中… (Esc 取消)";
                });
            }
            catch { }
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (lastImg != null) lastImg.Dispose();
        }
    }
}
