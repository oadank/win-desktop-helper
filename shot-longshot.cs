using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// 长截图 (滚动拼接) — 2026-09-07 第二批, 按老大拍板的 PixPin 交互重做:
//   点「长截图」→ 弹控制条(纵向/横向 + 自动↑↓) + 侧边实时预览。
//   默认手动: 鼠标放选区上滚滚轮, 程序逐帧拼接; 5 秒没动自动完成。
//   点 自动↑/自动↓ 进入自动滚动(再点一次停), 滚到底自动完成。
//   完成后不消失: 工具条变 保存并打开/复制/取消, 用户自己决定。
// 方向实测修正(记事本逐像素 MSE=0 验证): 下滚时内容上移, 新内容在 cur 底部 o 行
//   (第一批错拼了顶部 o 行 = 重复区, 已修正)。横向同理: 右滚内容左移, 新内容在 cur 右部 o 列。
// 防坑: 每次滚动后等 420ms 让平滑滚动动画停稳再截帧, 动画中间帧匹配必失败(实测);
//   两帧相同时任意 offset SSD≈0 → 先单测 o=0 判"没滚"; stall≥6(自动)/5s(手动) 判完成。
// 2026-09-08 两个真 bug 修复 (老大实测 0072→0076 丢 4 行 + 接缝横纹):
//   ① 匹配曾在半分辨率灰度上做, o 是半分辨率单位, 拼接却当物理像素用 → 每条接缝
//     系统性丢一半滚动量(≈3-4 行)。灰度改全分辨率, o 即物理像素。
//   ② 动画中间帧/撕裂帧拼进去 = 横纹。取帧前过"静止门"(SnapStable): 连拍两帧几乎
//     一致才参与拼接, 不稳就 90ms 后重试(最多 ~1.3s, 页面本身在动则放行交给质量门槛)。
partial class ShotService
{
    static volatile int lsBusy;   // 防重入: 长截图进行中再点直接提示

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out LSPoint p);
    [StructLayout(LayoutKind.Sequential)]
    struct LSPoint { public int X, Y; }
    const int MOUSEEVENTF_HWHEEL = 0x1000;
    static void MouseHScroll(int delta) { mouse_event(MOUSEEVENTF_HWHEEL, 0, 0, unchecked((uint)delta), UIntPtr.Zero); }

    // 入口: 截图工具条「长截图」按钮。r = 选区(屏幕坐标)
    static void StartLongShot(Rectangle r, Form overlay)
    {
        if (Interlocked.Exchange(ref lsBusy, 1) == 1) { ShowTrayInfo("长截图正在进行中"); return; }
        try { overlay.Close(); } catch { }        // 收掉遮罩工具条, 屏幕还原给目标页面
        Thread t = new Thread((ThreadStart)delegate { LongShotRun(r); });
        t.SetApartmentState(ApartmentState.STA);  // 结束要写剪贴板, 必须 STA
        t.Start();
        t.Join();
        Interlocked.Exchange(ref lsBusy, 0);
    }

    static void LongShotRun(Rectangle r)
    {
        Bitmap result = null;
        string errMsg = null; bool cancelled = false;
        int screens = 0; bool doSave = false, doCopy = false;
        try
        {
            var st = new LongShotStatus(r);
            st.Show();
            Thread.Sleep(150);

            int W = r.Width, H = r.Height;
            bool vert = st.AxisVert;
            Bitmap canvas = vert ? NewCanvas(W, H * 4) : NewCanvas(W * 4, H);
            int head = 0, filled = vert ? H : W;   // 内容区在画布 [head, head+filled)
            Bitmap f0 = SnapScreen(r);
            using (var g0 = Graphics.FromImage(canvas)) g0.DrawImage(f0, 0, 0);
            byte[] gPrev = Gray(f0); f0.Dispose();
            screens = 1;
            st.Update(canvas, head, filled, screens, vert);

            int lastO = 0, stall = 0, prevAuto = 0;
            DateTime lastProgress = DateTime.Now;
            bool resultMode = false;

            while (true)
            {
                Application.DoEvents();
                if (st.Cancel) { cancelled = true; break; }
                if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { cancelled = true; break; }

                if (!resultMode)
                {
                    if (st.Finish) { resultMode = true; st.ShowResult(screens, filled, vert); continue; }

                    // 切换纵横(只允许还没拼第二屏之前, 否则画布语义就乱了)
                    if (st.AxisChanged)
                    {
                        st.AckAxis();
                        if (screens == 1)
                        {
                            vert = st.AxisVert;
                            canvas.Dispose();
                            canvas = vert ? NewCanvas(W, H * 4) : NewCanvas(W * 4, H);
                            Bitmap f = SnapScreen(r);
                            using (var g2 = Graphics.FromImage(canvas)) g2.DrawImage(f, 0, 0);
                            gPrev = Gray(f); f.Dispose();
                            head = 0; filled = vert ? H : W; lastO = 0; stall = 0;
                            st.ApplyAxis(vert);
                            st.Update(canvas, head, filled, screens, vert);
                        }
                        continue;
                    }

                    int dir = st.AutoDir;
                    if (dir != prevAuto)
                    {
                        prevAuto = dir;
                        stall = 0; lastProgress = DateTime.Now;
                        if (dir != 0) SetCursorPos(r.X + r.Width / 2, r.Y + r.Height / 2); // 滚轮发给选区中间的窗口
                    }
                    if (dir != 0)
                    {
                        // 纵向: 下滚=-240 上滚=+240; 横向: 右滚=+240 左滚=-240
                        if (vert) MouseScroll(dir > 0 ? -240 : 240);
                        else MouseHScroll(dir > 0 ? 240 : -240);
                        Thread.Sleep(420);   // 平滑滚动动画彻底停稳(Win11 实测 280ms 不够)
                    }
                    else Thread.Sleep(120);  // 手动模式: 轮询跟手
                    Application.DoEvents();
                    if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { cancelled = true; break; }

                    byte[] gCur;
                    using (Bitmap cur = SnapStable(r, out gCur))
                    {
                        int o; bool fwd;   // 纵向: fwd=内容上移(新内容在cur底部) / 横向: fwd=内容左移(新内容在cur右部)
                        if (vert) o = MatchMove(gPrev, gCur, W, H, lastO, out fwd);
                        else o = MatchMoveH(gPrev, gCur, W, H, lastO, out fwd);
                        if (o <= 0)
                        {
                            stall++;
                            gPrev = gCur;    // 吸收动画中间态/静止帧, 下帧从最新画面起算
                            if (dir != 0 && stall >= 6) { resultMode = true; st.ShowResult(screens, filled, vert); }
                            else if (dir == 0 && (DateTime.Now - lastProgress).TotalMilliseconds >= 5000)
                            { resultMode = true; st.ShowResult(screens, filled, vert); }   // 老大拍板: 手动停 5s 自动完成
                            continue;
                        }
                        stall = 0; lastProgress = DateTime.Now; lastO = o; screens++;

                        canvas = StitchFrame(canvas, cur, vert, fwd, o, ref head, ref filled);
                        gPrev = gCur;
                        st.Update(canvas, head, filled, screens, vert);
                    }
                }
                else
                {
                    // 结果模式: 等用户选 保存/复制/取消, 不自动消失
                    if (st.Save) { doSave = true; doCopy = true; break; }
                    if (st.Copy) { doCopy = true; break; }
                    Thread.Sleep(60);
                }
            }
            try { st.Invoke((MethodInvoker)delegate { st.Close(); }); } catch { try { st.Close(); } catch { } }
            if (!cancelled && (doSave || doCopy))
            {
                result = vert
                    ? (Bitmap)canvas.Clone(new Rectangle(0, head, W, Math.Max(1, filled)), canvas.PixelFormat)
                    : (Bitmap)canvas.Clone(new Rectangle(head, 0, Math.Max(1, filled), H), canvas.PixelFormat);
            }
            canvas.Dispose();
        }
        catch (Exception ex) { errMsg = ex.Message; }
        if (errMsg != null) { Log("longshot err: " + errMsg); ShowTrayInfo("长截图失败: " + errMsg); return; }
        if (cancelled || result == null) { ShowTrayInfo("长截图已取消" + (screens > 1 ? " (拼了 " + (screens - 1) + " 次滚动)" : "")); return; }
        try
        {
            Clipboard.SetImage(result);
            if (doSave)
            {
                string path = SaveToShotDir(result);
                Log("longshot done: " + result.Width + "x" + result.Height + " " + screens + " screens -> " + path);
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ox) { Log("longshot open err: " + ox.Message); ShowTrayInfo("已保存但打不开: " + path); }
            }
            else
            {
                Log("longshot copied: " + result.Width + "x" + result.Height);
            }
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

    static Bitmap GrowCanvas(Bitmap canvas, int head, int growTop, int growBottom, out int newHead)
    {
        var big = NewCanvas(canvas.Width, canvas.Height + growTop + growBottom);
        using (var g = Graphics.FromImage(big)) g.DrawImage(canvas, 0, growTop);
        canvas.Dispose();
        newHead = head + growTop;
        return big;
    }
    static Bitmap GrowCanvasW(Bitmap canvas, int head, int growLeft, int growRight, out int newHead)
    {
        var big = NewCanvas(canvas.Width + growLeft + growRight, canvas.Height);
        using (var g = Graphics.FromImage(big)) g.DrawImage(canvas, growLeft, 0);
        canvas.Dispose();
        newHead = head + growLeft;
        return big;
    }

    // 把 cur 的新内容按方向接到画布 (必要时自动扩画布)。head/filled 引用更新。
    static Bitmap StitchFrame(Bitmap canvas, Bitmap cur, bool vert, bool fwd, int o, ref int head, ref int filled)
    {
        int cw = cur.Width, ch = cur.Height;
        if (vert && fwd)
        {
            int need = head + filled + o - canvas.Height;
            if (need > 0) canvas = GrowCanvas(canvas, head, 0, Math.Max(need, canvas.Height), out head);
        }
        else if (vert && !fwd && head - o < 0)
        {
            canvas = GrowCanvas(canvas, head, Math.Max(o - head, canvas.Height), 0, out head);
        }
        else if (!vert && fwd)
        {
            int need = head + filled + o - canvas.Width;
            if (need > 0) canvas = GrowCanvasW(canvas, head, 0, Math.Max(need, canvas.Width), out head);
        }
        else if (!vert && !fwd && head - o < 0)
        {
            canvas = GrowCanvasW(canvas, head, Math.Max(o - head, canvas.Width), 0, out head);
        }
        using (var g = Graphics.FromImage(canvas))
        {
            if (vert)
            {
                if (fwd) { g.DrawImage(cur, new Rectangle(0, head + filled, cw, o), new Rectangle(0, ch - o, cw, o), GraphicsUnit.Pixel); filled += o; }
                else { g.DrawImage(cur, new Rectangle(0, head - o, cw, o), new Rectangle(0, 0, cw, o), GraphicsUnit.Pixel); head -= o; filled += o; }
            }
            else
            {
                if (fwd) { g.DrawImage(cur, new Rectangle(head + filled, 0, o, ch), new Rectangle(cw - o, 0, o, ch), GraphicsUnit.Pixel); filled += o; }
                else { g.DrawImage(cur, new Rectangle(head - o, 0, o, ch), new Rectangle(0, 0, o, ch), GraphicsUnit.Pixel); head -= o; filled += o; }
            }
        }
        return canvas;
    }

    // ============ AI 直达: 无 UI 自动滚动长截图 (2026-09-08 老大拍板) ============
    // dir=down/up/left/right (默认 down)。自动滚到头(连续 6 稳定帧没动) / maxScreens / timeout_ms 停。
    // 结果存截图目录并返回 JSON: ok,path,width,height,screens
    static string LongShotAutoJson(int x, int y, int w, int h, string dir, int maxScreens, int timeoutMs)
    {
        if (Interlocked.Exchange(ref lsBusy, 1) == 1) return "{\"ok\":false,\"error\":\"longshot busy\"}";
        Bitmap result = null;
        try
        {
            bool vert = dir != "left" && dir != "right";
            int dirSign = (dir == "up" || dir == "left") ? -1 : 1;
            if (maxScreens < 2) maxScreens = 60;
            if (timeoutMs <= 0) timeoutMs = 120000;
            var r = new Rectangle(x, y, w, h);
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            Bitmap canvas = vert ? NewCanvas(w, h * 4) : NewCanvas(w * 4, h);
            int head = 0, filled = vert ? h : w, screens = 1, lastO = 0, stall = 0;
            byte[] gPrev; Bitmap f0 = SnapStable(r, out gPrev);
            using (var g = Graphics.FromImage(canvas)) g.DrawImage(f0, 0, 0);
            f0.Dispose();
            SetCursorPos(x + w / 2, y + h / 2); Thread.Sleep(150);   // 滚轮发给选区下的窗口

            while (screens < maxScreens && DateTime.Now < deadline)
            {
                if (vert) MouseScroll(dirSign > 0 ? -240 : 240);
                else MouseHScroll(dirSign > 0 ? 240 : -240);
                Thread.Sleep(300);
                byte[] gCur;
                using (Bitmap cur = SnapStable(r, out gCur))
                {
                    bool fwd;
                    int o = vert ? MatchMove(gPrev, gCur, w, h, lastO, out fwd)
                                 : MatchMoveH(gPrev, gCur, w, h, lastO, out fwd);
                    LSPoint cp; GetCursorPos(out cp);
                    Log("[longshot] it o=" + o + " stall=" + stall + " gdiff=" + GrayAbsDiff(gPrev, gCur) + " cur=" + cp.X + "," + cp.Y);
                    if (o <= 0) { gPrev = gCur; if (++stall >= 6) break; continue; }   // 到头/没反应
                    stall = 0; lastO = o; screens++;
                    canvas = StitchFrame(canvas, cur, vert, fwd, o, ref head, ref filled);
                    gPrev = gCur;
                }
            }
            result = vert
                ? (Bitmap)canvas.Clone(new Rectangle(0, head, w, Math.Max(1, filled)), canvas.PixelFormat)
                : (Bitmap)canvas.Clone(new Rectangle(head, 0, Math.Max(1, filled), h), canvas.PixelFormat);
            canvas.Dispose();
            string path = SaveToShotDir(result);
            Log("[longshot] auto done: " + result.Width + "x" + result.Height + " " + screens + " screens -> " + path);
            return "{\"ok\":true,\"path\":\"" + JsonEscape(path) + "\",\"width\":" + result.Width +
                   ",\"height\":" + result.Height + ",\"screens\":" + screens + "}";
        }
        catch (Exception ex) { Log("[longshot] auto err: " + ex.Message); return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}"; }
        finally { if (result != null) result.Dispose(); Interlocked.Exchange(ref lsBusy, 0); }
    }

    // 灰度化: 全分辨率。o 的单位 = 物理像素, 与拼接 DrawImage 一致 (纵横两轴都精确)。
    // (2026-09-08 坑: 之前高宽都减半, o 是半分辨率单位, 拼接却当物理像素 → 每缝系统性丢一半滚动量)
    static byte[] Gray(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var outp = new byte[w * h];
        var rect = new Rectangle(0, 0, w, h);
        var data = b.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                              System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            var rowBuf = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, rowBuf, 0, stride);
                int ro = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = x * 3;
                    outp[ro + x] = (byte)((rowBuf[i] + rowBuf[i + 1] + rowBuf[i + 2]) / 3);
                }
            }
        }
        finally { b.UnlockBits(data); }
        return outp;
    }

    // 两帧平均每像素灰度差 (静止门用)
    static long GrayAbsDiff(byte[] a, byte[] b)
    {
        long s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s;
    }

    // 静止门: 连拍两帧几乎一致才返回 (动画中间帧/撕裂帧拼进去=横纹+错位)。
    // 不稳就 90ms 后重试, 最多 ~1.3s; 页面本身在动(视频/动画网页)则放行, 交给匹配质量门槛兜底。
    static Bitmap SnapStable(Rectangle r, out byte[] g)
    {
        Bitmap prevB = SnapScreen(r); byte[] prevG = Gray(prevB);
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(90);
            Bitmap curB = SnapScreen(r); byte[] curG = Gray(curB);
            long lim = (long)curG.Length / 2;   // 平均每像素 <0.5 灰度 = 静止
            if (GrayAbsDiff(prevG, curG) <= lim) { prevB.Dispose(); g = curG; return curB; }
            prevB.Dispose(); prevB = curB; prevG = curG;
        }
        g = prevG; return prevB;
    }

    // ============ 纵向: 判定两帧间滚动量与方向 ============
    // 返回 o>0 = 滚动了 o 像素; contentUp=true 内容上移(新内容在 cur 底部 o 行, 接到画布下方),
    // false 内容下移(新内容在 cur 顶部 o 行, 接到画布上方)。0 = 没滚动/匹配失败。
    // 两方向都搜, 谁的 SSD 低听谁的 —— 手动模式用户随时可能反滚。
    static int MatchMove(byte[] prev, byte[] cur, int w, int h, int lastO, out bool contentUp)
    {
        contentUp = true;
        int gw = w, gh = h;                       // 全分辨率: o 单位 = 物理像素
        int band = Math.Min(60, gh / 3);
        int maxOff = gh - band - LS_INSET - 2;
        if (maxOff < 6) return 0;
        // ★ 先单测 o=0: 两帧完全相同时任意 offset SSD 都≈0, 邻域/全搜永远"假成功"(实测滚到底连拼 314 屏假帧)
        long sZero = BandDiff(prev, cur, gw, gh, band, 0);
        if (sZero < (long)band * (gw / 4) * 8) { Log("[ls] identical sZero=" + sZero); return 0; }
        int oUp, oDn;
        long sUp = BestOffset(prev, cur, gw, gh, band, maxOff, lastO, true, out oUp);   // 内容上移
        long sDn = BestOffset(cur, prev, gw, gh, band, maxOff, lastO, true, out oDn);   // 内容下移
        long bs; int b; bool up;
        if (sUp <= sDn) { bs = sUp; b = oUp; up = true; } else { bs = sDn; b = oDn; up = false; }
        int n = band * (gw / 4);
        // 邻域质量差(平均平方差>30²) → 两方向各自全范围重搜; 仍差 → 判定没滚动
        if (bs > (long)n * 900)
        {
            sUp = BestOffset(prev, cur, gw, gh, band, maxOff, 0, false, out oUp);
            sDn = BestOffset(cur, prev, gw, gh, band, maxOff, 0, false, out oDn);
            if (sUp <= sDn) { bs = sUp; b = oUp; up = true; } else { bs = sDn; b = oDn; up = false; }
            if (bs > (long)n * 900) { Log("[ls] v fail bs=" + bs + " lim=" + ((long)n * 900) + " b=" + b + " up=" + up); return 0; }
        }
        // 胜出方向对照带纯色检查: 纯色底带匹配不可信 → 别瞎拼
        byte[] owner = up ? prev : cur;
        if (BandVar(owner, gw, gh, band) < (long)n * 25) { Log("[ls] v fail flat band"); return 0; }
        if (b < 2) return 0;
        contentUp = up;
        return b;
    }

    static long BestOffset(byte[] a, byte[] b, int gw, int gh, int band, int maxOff, int lastO, bool neigh, out int best)
    {
        int lo = 0, hi = maxOff;
        if (neigh && lastO > 0) { lo = Math.Max(0, lastO - 30); hi = Math.Min(maxOff, lastO + 30); }
        best = 0; long bs = long.MaxValue;
        for (int o = lo; o <= hi; o += 2) { long s = BandDiff(a, b, gw, gh, band, o); if (s < bs) { bs = s; best = o; } }
        for (int o = Math.Max(lo, best - 2); o <= Math.Min(hi, best + 2); o++)
        { long s = BandDiff(a, b, gw, gh, band, o); if (s < bs) { bs = s; best = o; } }
        return bs;
    }

    // 三带联合 SSD: 底带+中带+顶带一起比 —— 单带会在重复文本上锁错行
    // BandDiff(a,b,o): a 的底带出现在 b 中上移 o 的位置 (a 内容 = b 内容下移 o)
    // LS_INSET=32: Win11 记事本等应用在视口上下边缘有渐隐遮罩 —— 实测边缘 ~20 行即使完美
    //   对齐也永远差一大截(对齐 SSD 3500 万 > 门槛 1155 万, 正确匹配直接被判死,
    //   2026-09-08 00:39 日志: 每轮 b=144(真位移) 却全数 v fail)。三带整体向内缩避开。
    const int LS_INSET = 32;
    static long BandDiff(byte[] a, byte[] b, int gw, int gh, int band, int o)
    {
        long s = 0;
        int mb = band / 2, mTop = gh / 2 - mb;
        int bTop = gh - band - LS_INSET;
        for (int y = 0; y < band; y++)
        {
            int r1B = (bTop + y) * gw, r2B = (bTop - o + y) * gw;
            int r1T = (o + LS_INSET + y) * gw, r2T = (LS_INSET + y) * gw;
            int r1M = (mTop + y) * gw, r2M = (mTop - o + y) * gw;
            for (int x = 0; x < gw; x += 4)
            {
                int dB = a[r1B + x] - b[r2B + x];
                int dT = a[r1T + x] - b[r2T + x];
                int dM = r2M >= 0 ? a[r1M + x] - b[r2M + x] : 0;
                s += dB * dB + dT * dT + dM * dM;
            }
        }
        return s;
    }

    static long BandVar(byte[] a, int gw, int gh, int band)
    {
        long tot = 0; int n = 0;
        for (int y = gh - band; y < gh; y++)
            for (int x = 0; x < gw; x += 4) { tot += a[y * gw + x]; n++; }
        long avg = tot / n, v = 0;
        for (int y = gh - band; y < gh; y++)
            for (int x = 0; x < gw; x += 4) { long d = a[y * gw + x] - avg; v += d * d; }
        return v;
    }

    // ============ 横向: 同纵向, 沿列匹配 ============
    // 返回 o>0; contentLeft=true 内容左移(右滚, 新内容在 cur 右部 o 列, 接到画布右方),
    // false 内容右移(左滚, 新内容在 cur 左部 o 列, 接到画布左方)。
    static int MatchMoveH(byte[] prev, byte[] cur, int w, int h, int lastO, out bool contentLeft)
    {
        contentLeft = true;
        int gw = w, gh = h;                       // 全分辨率: o 单位 = 物理像素
        int band = Math.Min(60, gw / 3);
        int maxOff = gw - band - LS_INSET - 2;
        if (maxOff < 6) return 0;
        long sZero = BandDiffH(prev, cur, gw, gh, band, 0);
        if (sZero < (long)band * (gh / 4) * 8) return 0;
        int oL, oR;
        long sL = BestOffsetH(prev, cur, gw, gh, band, maxOff, lastO, true, out oL);   // 内容左移
        long sR = BestOffsetH(cur, prev, gw, gh, band, maxOff, lastO, true, out oR);   // 内容右移
        long bs; int b; bool left;
        if (sL <= sR) { bs = sL; b = oL; left = true; } else { bs = sR; b = oR; left = false; }
        int n = band * (gh / 4);
        if (bs > (long)n * 900)
        {
            sL = BestOffsetH(prev, cur, gw, gh, band, maxOff, 0, false, out oL);
            sR = BestOffsetH(cur, prev, gw, gh, band, maxOff, 0, false, out oR);
            if (sL <= sR) { bs = sL; b = oL; left = true; } else { bs = sR; b = oR; left = false; }
            if (bs > (long)n * 900) return 0;
        }
        byte[] owner = left ? prev : cur;
        if (BandVarH(owner, gw, gh, band) < (long)n * 25) return 0;
        if (b < 2) return 0;
        contentLeft = left;
        return b;
    }

    static long BestOffsetH(byte[] a, byte[] b, int gw, int gh, int band, int maxOff, int lastO, bool neigh, out int best)
    {
        int lo = 0, hi = maxOff;
        if (neigh && lastO > 0) { lo = Math.Max(0, lastO - 30); hi = Math.Min(maxOff, lastO + 30); }
        best = 0; long bs = long.MaxValue;
        for (int o = lo; o <= hi; o += 2) { long s = BandDiffH(a, b, gw, gh, band, o); if (s < bs) { bs = s; best = o; } }
        for (int o = Math.Max(lo, best - 2); o <= Math.Min(hi, best + 2); o++)
        { long s = BandDiffH(a, b, gw, gh, band, o); if (s < bs) { bs = s; best = o; } }
        return bs;
    }

    // BandDiffH(a,b,o): a 的右带出现在 b 中左移 o 的位置 (列方向同样内缩避边缘遮罩)
    static long BandDiffH(byte[] a, byte[] b, int gw, int gh, int band, int o)
    {
        long s = 0;
        int mb = band / 2, mLeft = gw / 2 - mb;
        int bRight = gw - band - LS_INSET;
        for (int x = 0; x < band; x++)
        {
            int c1R = bRight + x, c2R = bRight - o + x;
            int c1L = o + LS_INSET + x, c2L = LS_INSET + x;
            int c1M = mLeft + x, c2M = mLeft - o + x;
            for (int y = 0; y < gh; y += 4)
            {
                int dR = a[y * gw + c1R] - b[y * gw + c2R];
                int dL = a[y * gw + c1L] - b[y * gw + c2L];
                int dM = c2M >= 0 ? a[y * gw + c1M] - b[y * gw + c2M] : 0;
                s += dR * dR + dL * dL + dM * dM;
            }
        }
        return s;
    }

    static long BandVarH(byte[] a, int gw, int gh, int band)
    {
        long tot = 0; int n = 0;
        for (int x = gw - band; x < gw; x++)
            for (int y = 0; y < gh; y += 4) { tot += a[y * gw + x]; n++; }
        long avg = tot / n, v = 0;
        for (int x = gw - band; x < gw; x++)
            for (int y = 0; y < gh; y += 4) { long d = a[y * gw + x] - avg; v += d * d; }
        return v;
    }

    // ---- 控制条+预览窗: 贴着选区, PixPin 同款 ----
    // 模式行: [纵向|横向] 切轴(只允许没开滚之前) + [自动↑|自动↓] (再点一次停)
    // 完成后: 模式行隐藏, 变 [保存并打开][复制][取消], 不自动消失
    class LongShotStatus : Form
    {
        public volatile bool Finish, Cancel, Save, Copy, AxisChanged;
        public volatile int AutoDir;             // 0=手动(跟用户滚轮) 1=自动下/右 -1=自动上/左
        public volatile bool AxisVert = true;
        volatile bool resultMode;
        Label info; PictureBox pv; Panel modeRow;
        Button btnV, btnH, btnUp, btnDown, btnDone, btnCopy, btnCancel;
        Bitmap lastImg;
        readonly Rectangle regRect;
        int pw = 250, ph = 620;

        public LongShotStatus(Rectangle r)
        {
            regRect = r;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(28, 29, 34);
            Font = new Font("Microsoft YaHei UI", 9f);
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Cancel = true; };
            Size = new Size(pw, ph);
            Place();

            info = new Label();
            info.Dock = DockStyle.Top; info.Height = 46;
            info.ForeColor = Color.FromArgb(232, 234, 237);
            info.TextAlign = ContentAlignment.MiddleLeft; info.Padding = new Padding(8, 4, 4, 0);
            info.Text = "手动: 鼠标放选区上滚滚轮即拼 (5秒不动自动完成)\n或点下面按钮自动滚动";
            Controls.Add(info);

            modeRow = new Panel();
            modeRow.Dock = DockStyle.Top; modeRow.Height = 36; modeRow.BackColor = Color.FromArgb(28, 29, 34);
            btnV = MkBtn("纵向", 6, 5, 50);
            btnV.Click += delegate { if (!AxisVert) { AxisVert = true; AxisChanged = true; SyncMode(); } };
            btnH = MkBtn("横向", 58, 5, 50);
            btnH.Click += delegate { if (AxisVert) { AxisVert = false; AxisChanged = true; SyncMode(); } };
            btnUp = MkBtn("自动↑", 122, 5, 58);
            btnUp.Click += delegate { AutoDir = (AutoDir == -1) ? 0 : -1; SyncMode(); };
            btnDown = MkBtn("自动↓", 182, 5, 58);
            btnDown.Click += delegate { AutoDir = (AutoDir == 1) ? 0 : 1; SyncMode(); };
            modeRow.Controls.AddRange(new Control[] { btnV, btnH, btnUp, btnDown });
            Controls.Add(modeRow);

            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom; bottom.Height = 36; bottom.BackColor = Color.FromArgb(28, 29, 34);
            btnCancel = MkBtn("取消 (Esc)", 6, 5, 92);
            btnCancel.Click += delegate { Cancel = true; };
            btnCopy = MkBtn("复制", pw - 198, 5, 84);
            btnCopy.Visible = false;
            btnCopy.Click += delegate { Copy = true; };
            btnDone = MkBtn("完成", pw - 108, 5, 100);
            btnDone.BackColor = Color.FromArgb(24, 110, 210); btnDone.ForeColor = Color.White;
            btnDone.Click += delegate { if (resultMode) Save = true; else Finish = true; };
            bottom.Controls.AddRange(new Control[] { btnCancel, btnCopy, btnDone });
            Controls.Add(bottom);

            pv = new PictureBox();
            pv.Dock = DockStyle.Fill;
            pv.BackColor = Color.FromArgb(20, 21, 25);
            pv.SizeMode = PictureBoxSizeMode.Zoom;
            Controls.Add(pv);
            pv.BringToFront();
            SyncMode();
        }

        Button MkBtn(string text, int x, int y, int w)
        {
            var b = new Button();
            b.Text = text; b.Bounds = new Rectangle(x, y, w, 26);
            b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.FromArgb(52, 55, 64);
            b.ForeColor = Color.FromArgb(232, 234, 237); b.FlatAppearance.BorderSize = 0;
            b.TabStop = false;
            return b;
        }

        void SyncMode()
        {
            Color acc = Color.FromArgb(24, 110, 210), btn = Color.FromArgb(52, 55, 64);
            btnV.BackColor = AxisVert ? acc : btn;
            btnH.BackColor = AxisVert ? btn : acc;
            btnUp.Text = AxisVert ? "自动↑" : "自动←";
            btnDown.Text = AxisVert ? "自动↓" : "自动→";
            btnUp.BackColor = AutoDir < 0 ? acc : btn;
            btnDown.BackColor = AutoDir > 0 ? acc : btn;
        }

        void Place()
        {
            int x = regRect.Right + 12, y = regRect.Top;
            var vs = SystemInformation.VirtualScreen;
            if (x + pw > vs.X + vs.Width) x = Math.Max(vs.X + 4, regRect.Left - pw - 12);
            y = Math.Max(vs.Y + 4, Math.Min(y, vs.Y + vs.Height - ph - 4));
            Location = new Point(x, y);
        }

        // 切轴后改窗口形状 (纵向=窄高, 横向=宽矮), worker 线程调用
        public void ApplyAxis(bool vertAxis)
        {
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    pw = vertAxis ? 250 : 640; ph = vertAxis ? 620 : 320;
                    Size = new Size(pw, ph);
                    Place();
                });
            }
            catch { }
        }

        public void AckAxis() { AxisChanged = false; }

        // 拼接画布更新(采集线程调用; 位图归状态窗所有, 换图时释放旧图)
        public void Update(Bitmap canvas, int head, int filled, int screensN, bool vert)
        {
            if (IsDisposed) return;
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    Rectangle rect = vert
                        ? new Rectangle(0, head, regRect.Width, Math.Max(1, filled))
                        : new Rectangle(head, 0, Math.Max(1, filled), regRect.Height);
                    var crop = (Bitmap)canvas.Clone(rect, canvas.PixelFormat);
                    var old = lastImg; lastImg = crop;
                    pv.Image = crop;
                    if (old != null) old.Dispose();
                    info.Text = "已拼接 " + screensN + " 屏 · " + (vert ? "高 " : "宽 ") + filled + "px · " +
                                (AutoDir == 0 ? "手动滚轮拼接中…" : "自动滚动中… (再点一次停)");
                });
            }
            catch { }
        }

        // 进入结果模式: 停自动滚, 隐藏模式行, 变 保存并打开/复制/取消
        public void ShowResult(int screensN, int filled, bool vert)
        {
            if (IsDisposed) return;
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    resultMode = true; AutoDir = 0; SyncMode();
                    modeRow.Visible = false;
                    btnCopy.Visible = true;
                    btnDone.Text = "保存并打开";
                    info.Text = "拼接完成: " + screensN + " 屏 · " + (vert ? "高 " : "宽 ") + filled + "px\n保存并打开 / 复制 / 取消";
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
