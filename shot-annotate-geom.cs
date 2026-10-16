// shot-annotate-geom.cs
// ============================================================================
// 证据级 箭头/折线 几何渲染模块 (逆向自 PixPin 的 PixUtils.dll)
//
// 实证来源 (REA 反编译, 全部为 shipped-artifact 实证, 非手搓):
//   - GenerateBrokenLinePath  @0x81f70 : 折线主体 + 起止箭头装配 (2026-10-16 重扒逐行核对)
//   - GenArrowHeadContour     @0x80700 : 13 型箭头头部精确几何 (case1 本地几何逐字核对一致)
//   - FUN_180083630(type,w)            : 箭头沿线内缩长度 (重扒核对: 逐行一致)
//   - FUN_180082b70(path,pts,t,sa,ea)  : 平滑样条 (长度加权 CR, t=0.5; 2026-10-16 重扒全文复刻)
//
// 2026-10-16 重扒源码修正记录 (修正项均为反编译实证, 非手搓):
//   ① 起点头型角度改为朝外 atan2(p0-p1) (旧版朝内, 双向箭头起点头镜像 180°)
//   ② dashType=5 数组改为 [4,2,1,2,1,2] (旧版少一组 [1,2])
//   ③ 直线模式不再内缩裁杆 (PixPin 仅曲线模式裁; 直线杆画全程由头型覆盖)
//   ④ 平滑由 DrawCurve 近似换成 FUN_180082b70 实证级采样复刻
//
// 与 Qt QPainterPathStroker 的等价关系:
//   Qt 用 createStroke 把 path 描边成实心区域  ==  GDI+ 用 Pen 描边 (DrawPath)
//   Qt 用 addEllipse/addPolygon + setFillRule ==  GDI+ 用 Brush 填充 (FillPath)
// 所有尺寸都是"线宽 w 的倍数", 与屏幕缩放无关, 复刻时按 w 换算即可.
//
// 本模块为独立静态类, 零依赖 ShotService 状态, 可单独编译进测试 exe 出图验证.
// 接入主流程 (替换 shot-capture.cs 现有手搓 DrawArrowEx) 需另行点头.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

static class PixPinArrowGeom
{
    const double RAD = Math.PI / 180.0;

    // ------------------------------------------------------------------
    // FUN_180083630: 箭头沿线内缩长度 (单位 = 线宽 w 的倍数)
    // 用于把折线端点沿反方向内缩, 使箭头尖端正好落在折线顶点, 短折线防重叠.
    // ------------------------------------------------------------------
    public static float ArrowInsetLength(int type, float w)
    {
        switch (type)
        {
            case 1: case 6: case 10: return w * 2.5f;
            case 2: case 3:          return w * 3.0f;
            case 5:                  return w * 1.5f;
            case 7:                  return w * 3.5f;   // w*4.0 - w*0.5
            case 9:                  return w * 4.0f;
            case 11:                 return w * 3.5f;   // 0xb
            case 12:                 return w * 2.2f;   // 0xc
            default:                 return 0f;
        }
    }

    // ------------------------------------------------------------------
    // 本地坐标 -> 全局坐标: 绕 tip 旋转 angDeg 度, 再平移到 (x,y)
    // 与 GenArrowHeadContour 的 (a*c - b*s, a*s + b*c) 公式完全一致.
    // angle=0 时箭头尖端在原点(tip), 箭体指向 -x (即箭头朝 +x / 右), 与
    // GenerateBrokenLinePath 算出的 atan2 切线方向一致.
    // ------------------------------------------------------------------
    static PointF RT(float ax, float ay, float angDeg, float x, float y)
    {
        double r = angDeg * RAD;
        double c = Math.Cos(r), s = Math.Sin(r);
        return new PointF(x + ax * (float)c - ay * (float)s,
                          y + ax * (float)s + ay * (float)c);
    }

    // ------------------------------------------------------------------
    // 构建单个箭头头部的 GraphicsPath (本地几何, 已旋转平移到 tip/angle).
    //   fill 型 -> 返回闭合多边形 (用 Brush 填充)
    //   stroke 型 -> 返回折线 (用 Pen 描边)
    // type 取值 0-12 (与 GenArrowHeadContour 的 switch 一一对应):
    //   0=None(空), 1..12 各型 (case 10 即 0x0a, 11=0xb, 12=0xc)
    // ------------------------------------------------------------------
    public static GraphicsPath BuildArrowHeadPath(int type, PointF tip, float w, float angDeg)
    {
        var p = new GraphicsPath();
        float W = w;
        switch (type)
        {
            case 0: // 无箭头 (GenArrowHeadContour default: 空 QPainterPath)
                break;

            case 1: // 开口箭头, 描边 width=W, RoundCap+RoundJoin
            case 10: // 开口箭头, 描边 width=0.5W, RoundCap+MiterJoin
            {
                var P1 = RT(-2.5f * W, -1.5f * W, angDeg, tip.X, tip.Y);
                var P3 = RT(-2.5f * W,  1.5f * W, angDeg, tip.X, tip.Y);
                var P4 = RT(-2.5f * W,  0.0f,  angDeg, tip.X, tip.Y);
                p.StartFigure(); p.AddLine(P1, tip); p.AddLine(tip, P3);
                p.StartFigure(); p.AddLine(tip, P4);
                break;
            }

            case 2: // 实心三角 (createStroke 0.5W 后 united 原三角 => 实心)
            case 3: // 实心三角 (createStroke W 后 united => 实心)
            {
                var P1 = RT(-3f * W, -1.5f * W, angDeg, tip.X, tip.Y);
                var P3 = RT(-3f * W,  1.5f * W, angDeg, tip.X, tip.Y);
                p.AddLine(P1, tip); p.AddLine(tip, P3); p.CloseFigure();
                break;
            }

            case 4: // 实心椭圆, 直径 1.5W
                p.AddEllipse(tip.X - 0.75f * W, tip.Y - 0.75f * W, 1.5f * W, 1.5f * W);
                break;

            case 5: // 椭圆环, 描边 width=W
                p.AddEllipse(tip.X - 0.75f * W, tip.Y - 0.75f * W, 1.5f * W, 1.5f * W);
                break;

            case 6: // 实心菱形, 长轴 5W 短轴 3.5W
            {
                var P0 = RT(0, 0, angDeg, tip.X, tip.Y);
                var P1 = RT(-2.5f * W, -1.75f * W, angDeg, tip.X, tip.Y);
                var P2 = RT(-5f * W, 0, angDeg, tip.X, tip.Y);
                var P3 = RT(-2.5f * W,  1.75f * W, angDeg, tip.X, tip.Y);
                p.AddLine(P0, P1); p.AddLine(P1, P2); p.AddLine(P2, P3); p.CloseFigure();
                break;
            }

            case 7: // 菱形轮廓, 长轴 4W 短轴 2.5W, 描边 width=W, MiterJoin
            {
                var P0 = RT(0, 0, angDeg, tip.X, tip.Y);
                var P1 = RT(-2f * W, -1.25f * W, angDeg, tip.X, tip.Y);
                var P2 = RT(-4f * W, 0, angDeg, tip.X, tip.Y);
                var P3 = RT(-2f * W,  1.25f * W, angDeg, tip.X, tip.Y);
                p.AddLine(P0, P1); p.AddLine(P1, P2); p.AddLine(P2, P3); p.CloseFigure();
                break;
            }

            case 8: // 建筑刻度: 垂直于箭线的刻度短线 (decompile 中 +45° 与 (c-s,s+c) 混合等效方向角 θ+90°),
                    // 长 3w·√2 ≈ 4.24w, 描边 width=W, RoundCap (公式与 GenArrowHeadContour.c L211-237 逐字一致)
            {
                double r = (angDeg + 45f) * RAD;
                double c2 = Math.Cos(r), s2 = Math.Sin(r);
                float a = 1.5f * W;
                var P1 = new PointF(tip.X - a * (float)c2 + a * (float)s2,
                                    tip.Y - a * (float)s2 - a * (float)c2);
                var P2 = new PointF(tip.X + a * (float)c2 - a * (float)s2,
                                    tip.Y + a * (float)s2 + a * (float)c2);
                p.StartFigure(); p.AddLine(P1, P2);
                break;
            }

            case 9: // Cross 头部 — FUN_180083370(param_1, tip, w, angle) 实证:
                    //   主线: tip -> 沿 angle 回缩 4w; 垂直线: 长 4w, 中心在回缩 2w 处; 描边 w + RoundCap
            {
                double r = angDeg * RAD;
                double c = Math.Cos(r), s = Math.Sin(r);
                float w2 = 2f * W;
                var mainA = new PointF(tip.X, tip.Y);
                var mainB = new PointF(tip.X - 4f * W * (float)c, tip.Y - 4f * W * (float)s);
                var Pc = new PointF(tip.X - w2 * (float)c + w2 * (float)s, tip.Y - w2 * (float)s - w2 * (float)c);
                var Pd = new PointF(tip.X - w2 * (float)c - w2 * (float)s, tip.Y - w2 * (float)s + w2 * (float)c);
                p.StartFigure(); p.AddLine(mainA, mainB);
                p.StartFigure(); p.AddLine(Pc, Pd);
                break;
            }

            case 11: // 0xb 闭合带尾三角
            {
                var P0 = RT(-3f * W, -1.5f * W, angDeg, tip.X, tip.Y);
                var P2 = RT(-3f * W,  1.5f * W, angDeg, tip.X, tip.Y);
                var P3 = RT(-4f * W,  0, angDeg, tip.X, tip.Y);
                p.AddLine(P0, tip); p.AddLine(tip, P2); p.AddLine(P2, P3); p.CloseFigure();
                break;
            }

            case 12: // 0xc 自定义 5 顶点多边形, 变换 = translate(tip)·rotate(angle-90)·scale(1.1W)
            {
                // 本地顶点 (未变换): (0,0)(-1.5,-3)(0,-2)(1.5,-3)(0,0)
                PointF[] v = { new PointF(0, 0), new PointF(-1.5f, -3f),
                               new PointF(0, -2f), new PointF(1.5f, -3f), new PointF(0, 0) };
                float sc = 1.1f * W;
                double r = (angDeg - 90f) * RAD;
                double c = Math.Cos(r), s = Math.Sin(r);
                PointF[] pts = new PointF[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    float ax = v[i].X * sc, ay = v[i].Y * sc;
                    pts[i] = new PointF(tip.X + ax * (float)c - ay * (float)s,
                                        tip.Y + ax * (float)s + ay * (float)c);
                }
                p.AddPolygon(pts);
                break;
            }
        }
        return p;
    }

    // ------------------------------------------------------------------
    // 实际绘制箭头头部: 按型选 stroke / fill.
    //   fill 型: 4,6,11,12
    //   stroke 型: 1,2,3,5,7,8,9,10 (各自 pen 参数见下, 与反编译 setWidth/setCap/setJoin 对应)
    //     Qt 0x20=RoundCap, 0x80=RoundJoin, 0x00=MiterJoin
    // ------------------------------------------------------------------
    public static void DrawArrowHead(Graphics g, int type, PointF tip, float w, float angDeg, Color color)
    {
        using (var path = BuildArrowHeadPath(type, tip, w, angDeg))
        {
            if (path.PointCount == 0) return;
            bool fill = (type == 4 || type == 6 || type == 11 || type == 12);
            if (fill) { using (var b = new SolidBrush(color)) g.FillPath(b, path); return; }

            Pen pen = null;
            switch (type)
            {
                case 1:  pen = new Pen(color, w)    { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }; break;
                case 2:  // 实心三角: 填充本体即可 (0.5W 描边 union 后视觉等同实心)
                case 3:  using (var b = new SolidBrush(color)) g.FillPath(b, path);
                          pen = new Pen(color, type == 2 ? w * 0.5f : w) { LineJoin = LineJoin.Round }; break;
                case 5:  pen = new Pen(color, w)    { LineJoin = LineJoin.Round }; break;
                case 7:  pen = new Pen(color, w)    { LineJoin = LineJoin.Miter }; break;
                case 8:  pen = new Pen(color, w)    { StartCap = LineCap.Round, EndCap = LineCap.Round }; break;
                case 9:  pen = new Pen(color, w)    { StartCap = LineCap.Round, EndCap = LineCap.Round }; break;
                case 10: pen = new Pen(color, w * 0.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Miter }; break;
            }
            if (pen != null) { g.DrawPath(pen, path); pen.Dispose(); }
        }
    }

    // ----- 折线辅助 -----
    static float AngleDeg(PointF a, PointF b)
    {
        return (float)(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI);
    }

    static float Dist(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    static PointF Lerp(PointF a, PointF b, float t)
    {
        return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    static float PolylineLen(PointF[] p)
    {
        float s = 0;
        for (int i = 1; i < p.Length; i++) s += Dist(p[i - 1], p[i]);
        return s;
    }

    // 内缩折线端点: 起点沿首段内缩 startLen, 终点沿末段内缩 endLen (防箭头重叠)
    static PointF[] TrimPolyline(PointF[] pts, float startLen, float endLen)
    {
        if (pts.Length < 2) return pts;
        var list = new List<PointF>(pts);
        if (startLen > 0 && list.Count >= 2)
        {
            float seg = Dist(list[0], list[1]);
            if (seg <= startLen) list.RemoveAt(0);
            else list[0] = Lerp(list[0], list[1], startLen / seg);
        }
        if (endLen > 0 && list.Count >= 2)
        {
            int n = list.Count;
            float seg = Dist(list[n - 2], list[n - 1]);
            if (seg <= endLen) list.RemoveAt(n - 1);
            else list[n - 1] = Lerp(list[n - 1], list[n - 2], endLen / seg);
        }
        return list.ToArray();
    }

    static void SetDash(Pen pen, int dashType)
    {
        // GenerateBrokenLinePath @0x81f70 实证: lineStyle(flag@0x10) 2/3/4/5 的 dash 数组
        //   2=[4,2]  3=[1,2]  4=[4,2,1,2]  5=[4,2,1,2,1,2]  (0x4010=4.0, 0x400=2.0, 0x3ff=1.0)
        switch (dashType)
        {
            case 2: pen.DashPattern = new float[] { 4f, 2f }; break;
            case 3: pen.DashPattern = new float[] { 1f, 2f }; break;
            case 4: pen.DashPattern = new float[] { 4f, 2f, 1f, 2f }; break;
            case 5: pen.DashPattern = new float[] { 4f, 2f, 1f, 2f, 1f, 2f }; break;
        }
    }

    // 虚线样式对外入口 (属性栏下拉预览用, 与画布同一 dash 数组 = 预览与画布同源)
    public static void ApplyDash(Pen pen, int dashType) { SetDash(pen, dashType); }

    // ---- 细尾渐变箭头 (老版保留样式: S_THIN 实心 / S_HOLLOW 空心, 几何自旧 DrawArrowEx 原样恢复) ----
    static PointF[] RotateLocal(PointF[] local, float ox, float oy, float ang)
    {
        float cs = (float)Math.Cos(ang), sn = (float)Math.Sin(ang);
        PointF[] outp = new PointF[local.Length];
        for (int i = 0; i < local.Length; i++)
            outp[i] = new PointF(ox + local[i].X * cs - local[i].Y * sn, oy + local[i].X * sn + local[i].Y * cs);
        return outp;
    }

    // 细尾锥形箭头: 尾收尖点 → 杆细到粗 → 大后掠翼; hollow=false 填充, true 描边
    public static void DrawTaperArrow(Graphics g, PointF p1, PointF p2, float w, Color color, bool hollow)
    {
        float x1 = p1.X, y1 = p1.Y, x2 = p2.X, y2 = p2.Y;
        float L = (float)Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        if (L < 4) return;
        float ang = (float)Math.Atan2(y2 - y1, x2 - x1);
        if (!hollow)
        {
            float hhl = Math.Max(18f, L * 0.22f);           // 头长
            if (hhl > L * 0.6f) hhl = L * 0.6f;
            float hs2 = L - hhl;                            // 头底 x
            float hhw = Math.Max(2.5f, w * 2.2f);           // 头底处杆半宽
            float chw = hhw * 2.75f;                        // 翼半宽 = 杆端 2.75 倍
            float sweep = hhl * 0.45f;                      // 倒钩后掠
            PointF[] local = new PointF[] {
                new PointF(0, 0),                           // 尖尾
                new PointF(hs2, -hhw),
                new PointF(hs2 - sweep, -chw),
                new PointF(L, 0),
                new PointF(hs2 - sweep, chw),
                new PointF(hs2, hhw),
            };
            PointF[] screen = RotateLocal(local, x1, y1, ang);
            using (SolidBrush b = new SolidBrush(color)) g.FillPolygon(b, screen);
        }
        else
        {
            float lw2 = Math.Max(2f, w * 1.2f);             // 描边稍细: 太粗把锥度吃成"平行感"
            float hhl = Math.Max(22f, L * 0.22f);
            if (hhl > L * 0.6f) hhl = L * 0.6f;
            float hs2 = L - hhl;
            float hhw = Math.Max(5f, w * 3.1f);
            float chw = hhw * 2.75f, sweep = hhl * 0.45f;
            PointF[] poly = RotateLocal(new PointF[] {
                new PointF(0, 0), new PointF(hs2, -hhw), new PointF(hs2 - sweep, -chw),
                new PointF(L, 0), new PointF(hs2 - sweep, chw), new PointF(hs2, hhw) }, x1, y1, ang);
            using (Pen fp = new Pen(color, lw2))
            {
                fp.LineJoin = LineJoin.Round;
                g.DrawPolygon(fp, poly);
            }
        }
    }

    // ------------------------------------------------------------------
    // PixPin FUN_180082b70 实证级复刻 (平滑样条):
    //   控制点(长度加权 Catmull-Rom, tension=0.5):
    //     cp1 = p1 - (p2-p0)*t*len01/(len01+len12)
    //     cp2 = p1 + (p2-p0)*t*len12/(len01+len12)
    //   路径: moveTo(p0) → quadTo(cp1_0, p1) → cubicTo(cp[2i-3],cp[2i-2],p_i)… → quadTo(cp2_last, pn)
    //   起止角(朝外): start = atan2(2*(cp1_0-p0))-180°, end = atan2(2*(cp2_last-pn-1))
    // GDI+ 无 quadTo: 首/尾 quad 按 c1=p0+2/3(c-p0), c2=p1+2/3(c-p1) 转 cubic 后等参采样
    // ------------------------------------------------------------------
    public static PointF[] SampleSmooth(PointF[] pts, float tension, out float startAng, out float endAng)
    {
        int n = pts.Length;
        var cps = new List<PointF>(); // 2*(n-2) 个控制点
        for (int i = 0; i <= n - 3; i++)
        {
            PointF p0 = pts[i], p1 = pts[i + 1], p2 = pts[i + 2];
            float l01 = Dist(p1, p0), l12 = Dist(p2, p1);
            float sum = l01 + l12; if (sum <= 0f) sum = 1f;
            float dx = (p2.X - p0.X) * tension, dy = (p2.Y - p0.Y) * tension;
            cps.Add(new PointF(p1.X - dx * l01 / sum, p1.Y - dy * l01 / sum));
            cps.Add(new PointF(p1.X + dx * l12 / sum, p1.Y + dy * l12 / sum));
        }
        var outp = new List<PointF>();
        outp.Add(pts[0]);
        const int ST = 20; // 每段采样数 (PixPin 按路径 1% 步进采样, 视觉等效)
        // 首段 quad(cps[0], pts[1]) → 转 cubic
        SampleCubic(outp, pts[0],
            new PointF(pts[0].X + 2f / 3f * (cps[0].X - pts[0].X), pts[0].Y + 2f / 3f * (cps[0].Y - pts[0].Y)),
            new PointF(pts[1].X + 2f / 3f * (cps[0].X - pts[1].X), pts[1].Y + 2f / 3f * (cps[0].Y - pts[1].Y)),
            pts[1], ST);
        // 中段 cubic(ctrl[2i-3], ctrl[2i-2], pts[i]), i=2..n-2
        for (int i = 2; i <= n - 2; i++)
            SampleCubic(outp, pts[i - 1], cps[2 * i - 3], cps[2 * i - 2], pts[i], ST);
        // 尾段 quad(cps[2n-5], pts[n-1]) → 转 cubic
        int last = 2 * n - 5;
        SampleCubic(outp, pts[n - 2],
            new PointF(pts[n - 2].X + 2f / 3f * (cps[last].X - pts[n - 2].X), pts[n - 2].Y + 2f / 3f * (cps[last].Y - pts[n - 2].Y)),
            new PointF(pts[n - 1].X + 2f / 3f * (cps[last].X - pts[n - 1].X), pts[n - 1].Y + 2f / 3f * (cps[last].Y - pts[n - 1].Y)),
            pts[n - 1], ST);
        startAng = (float)(Math.Atan2(2.0 * (cps[0].Y - pts[0].Y), 2.0 * (cps[0].X - pts[0].X)) * 180.0 / Math.PI - 180.0);
        endAng = (float)(Math.Atan2(2.0 * (cps[last].Y - pts[n - 2].Y), 2.0 * (cps[last].X - pts[n - 2].X)) * 180.0 / Math.PI);
        return outp.ToArray();
    }

    // 三次贝塞尔等参采样 (直接展开贝塞尔多项式)
    static void SampleCubic(List<PointF> outp, PointF p0, PointF c1, PointF c2, PointF p1, int steps)
    {
        for (int k = 1; k <= steps; k++)
        {
            float t = (float)k / steps, u = 1f - t;
            float x = u * u * u * p0.X + 3f * u * u * t * c1.X + 3f * u * t * t * c2.X + t * t * t * p1.X;
            float y = u * u * u * p0.Y + 3f * u * u * t * c1.Y + 3f * u * t * t * c2.Y + t * t * t * p1.Y;
            outp.Add(new PointF(x, y));
        }
    }

    // ------------------------------------------------------------------
    // 完整折线 + 起止箭头 (证据级复刻 GenerateBrokenLinePath @0x81f70)
    //   pts        : 折线顶点 (屏坐标)
    //   w          : 线宽
    //   startType  : 起点箭头型 (0-12), endType: 终点箭头型 (0-12)
    //   smooth     : true => 平滑曲线 (PixPin FUN_180082b70 实证级复刻, 非 DrawCurve 近似)
    //   dashType   : 0=实线, 2/3/4/5 = 虚线 (见 SetDash, 数组与反编译一致)
    //
    // 与 PixPin 行为对齐的两处关键差异 (2026-10-16 重扒源码修正):
    //   1) 起点头型角度 = atan2(p0-p1) 朝外 (旧版误用 p0→p1 朝内, 双向箭头起点头镜像 180°)
    //   2) 直线模式不内缩裁杆 (PixPin 仅曲线模式 percentAtLength 裁头尾; 直线杆画全程, 头型轮廓覆盖其上)
    // ------------------------------------------------------------------
    public static void DrawBrokenLine(Graphics g, PointF[] pts, float w, Color color,
        int startType, int endType, bool smooth, int dashType)
    {
        if (pts.Length < 2) return;
        int n = pts.Length;

        float startAng, endAng;
        PointF[] shaft;

        if (smooth)
        {
            // 曲线模式 (StraightCurveMode=1): 样条 + 内缩裁头尾 (PixPin percentAtLength 等效)
            float sIn = ArrowInsetLength(startType, w);
            float eIn = ArrowInsetLength(endType, w);
            if (n > 2) shaft = SampleSmooth(pts, 0.5f, out startAng, out endAng);
            else { shaft = pts; startAng = AngleDeg(pts[1], pts[0]); endAng = AngleDeg(pts[0], pts[1]); }
            float total = PolylineLen(shaft);
            if (total - sIn - eIn < 2 * w && total > 0)
            {
                float f = (total - 2 * w) / (sIn + eIn);
                if (f > 0) { sIn *= f; eIn *= f; }
            }
            shaft = TrimPolyline(shaft, sIn, eIn);
        }
        else
        {
            // 直线模式: 角度取起止段朝外方向 (与 GenerateBrokenLinePath 的 atan2(p0-p1) 一致)
            startAng = AngleDeg(pts[1], pts[0]);
            endAng = AngleDeg(pts[n - 2], pts[n - 1]);
            shaft = pts; // 不内缩: 杆画全程, 头型轮廓 united 覆盖 (PixPin 同款)
        }

        // 防崩: 裁后不足 2 点不画杆 (头型仍画, 与 PixPin isEmpty 跳过 stroker 但仍加头的分支一致)
        if (shaft != null && shaft.Length >= 2)
        {
            using (var pen = new Pen(color, w))
            {
                SetDash(pen, dashType);
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, shaft);
            }
        }

        if (startType != 0) DrawArrowHead(g, startType, pts[0], w, startAng, color);
        if (endType != 0) DrawArrowHead(g, endType, pts[n - 1], w, endAng, color);
    }
}
