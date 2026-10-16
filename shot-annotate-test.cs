// shot-annotate-test.cs
// 独立测试入口: 用证据级几何模块出一张对比图 (不含进主 exe).
// 跑法: build-test.cmd 编出 annotate-geom-test.exe 后运行, 生成 annotate-geom-chart.png
// 该图直接对标 PixPin 箭头样式下拉框里的 13 个箭头头部 (同一套 GenerateBrokenLinePath/GenArrowHeadContour 几何).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;

class AnnotateGeomTest
{
    [STAThread]
    static void Main()
    {
        int cols = 4, rows = 4;
        int cellW = 240, cellH = 150;
        int w = 6;                       // 线宽 = 6px, 所有尺寸按 w 倍数换算
        Bitmap bmp = new Bitmap(cols * cellW, rows * cellH);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            using (var title = new Font("Consolas", 13, FontStyle.Bold))
                g.DrawString("PixPin 箭头几何 (REA 证据级复刻)  w=" + w, title, Brushes.Black, 12, 8);

            int idx = 0;
            // ---- 0-12: 13 型箭头头部 (angle=0 指向右) ----
            for (int t = 0; t <= 12; t++)
            {
                int col = idx % cols, row = idx / cols;
                int cx = col * cellW + cellW / 2;
                int cy = row * cellH + cellH / 2 + 14;
                PointF tip = new PointF(cx, cy);
                // 画一条短引线把箭头接出来, 更贴近实际观感
                PointF[] lead = { new PointF(cx - 90, cy), tip };
                PixPinArrowGeom.DrawBrokenLine(g, lead, w, Color.Red, 0, t, false, 0);
                g.DrawString("type " + t, new Font("Consolas", 11), Brushes.Black, col * cellW + 10, row * cellH + cellH - 24);
                idx++;
            }
            // ---- 13-15: 完整折线装配样例 (起/止型组合) ----
            DrawSample(g, 13, cols, cellW, cellH, w, Color.Blue, 1, 1, false, 0);   // 双箭头直线
            DrawSample(g, 14, cols, cellW, cellH, w, Color.Green, 0, 2, false, 0);  // 末端实心三角
            DrawSample(g, 15, cols, cellW, cellH, w, Color.Purple, 6, 12, true, 0); // 平滑: 菱形头+自定义多边形头
        }
        bmp.Save("annotate-geom-chart.png", System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("saved annotate-geom-chart.png");
        DrawDropdownMap();
    }

    // ==================================================================
    // 按 populateArrowTypes 的真实顺序 (REA 实证: ItemData 依次 0,12,1,2,3,4,5,6,7,8,9)
    // 出一张 "PixPin 箭头样式下拉框 11 行" 对照图. 每行 = 水平线 + 终点头型
    // (对应 paintEvent 的 direction=0 分支: startType=0, endType=选中值).
    // 行预览的单/双头画法由 ArrowHeadDelegate 决定 (类已实锤, paint 未逐行拆).
    // ==================================================================
    static void DrawDropdownMap()
    {
        int[] order = { 0, 12, 1, 2, 3, 4, 5, 6, 7, 8, 9 };   // populateArrowTypes 实证顺序
        string[] names = { "0 无头(纯线)", "12 燕尾多边形", "1 空心开口", "2 实心三角(细描边)",
                           "3 实心三角(粗描边)", "4 实心圆点", "5 空心圆环", "6 实心菱形",
                           "7 空心菱形轮廓", "8 I杆刻度", "9 十字" };
        int rowH = 56, margin = 300, w = 5;
        Bitmap bmp = new Bitmap(560, order.Length * rowH + 60);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            using (var title = new Font("Consolas", 12, FontStyle.Bold))
                g.DrawString("PixPin 箭头下拉 11 行 (populateArrowTypes 顺序)", title, Brushes.Black, 12, 8);
            for (int i = 0; i < order.Length; i++)
            {
                int y = 44 + i * rowH + rowH / 2;
                PointF[] line = { new PointF(320, y), new PointF(545, y) };
                PixPinArrowGeom.DrawBrokenLine(g, line, w, Color.Black, 0, order[i], false, 0);
                g.DrawString("第" + (i + 1) + "行  " + names[i],
                    new Font("Consolas", 11), Brushes.Black, 12, y - 9);
            }
        }
        bmp.Save("annotate-dropdown-map.png", System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("saved annotate-dropdown-map.png");
    }

    static void DrawSample(Graphics g, int idx, int cols, int cellW, int cellH, float w, Color c,
        int startType, int endType, bool smooth, int dash)
    {
        int col = idx % cols, row = idx / cols;
        int x0 = col * cellW + 20, x1 = col * cellW + cellW - 20;
        int y = row * cellH + cellH / 2 + 14;
        PointF[] pts = { new PointF(x0, y), new PointF((x0 + x1) / 2, y - 30), new PointF(x1, y) };
        PixPinArrowGeom.DrawBrokenLine(g, pts, w, c, startType, endType, smooth, dash);
        g.DrawString("line " + startType + "->" + endType + (smooth ? " smooth" : ""),
            new Font("Consolas", 10), Brushes.Black, col * cellW + 10, row * cellH + cellH - 24);
    }
}
