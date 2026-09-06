# -*- coding: utf-8 -*-
# ①预览 bmp 背景填菜单深色 ②菜单项去文字只留图 ③S_HOLLOW 改为柳叶轮廓描边版 (与实心同几何, 全程一个指向)
p = 'shot-capture.cs'
s = open(p, 'rb').read().decode('utf-8')

# ---- 1) S_HOLLOW: 与 THIN 同多边形, 描边不填充 ----
old = '''            if (style == Annot.S_HOLLOW)
            {
                // 样式② 空心线框箭头: 圆头封闭尾帽 + 上下平行杆线(均匀) + V 形开口双倒钩翼; 一条连续描边路径
                float L = (float)Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                if (L < 4) return;
                float lw2 = Math.Max(2.5f, w * 1.5f);           // 全程均匀线宽 = 粗细×1.5 (用户规格)
                float bw = Math.Max(5.5f, w * 2.4f);            // 杆半宽
                float hhl = Math.Max(20f, L * 0.2f);
                if (hhl > L * 0.6f) hhl = L * 0.6f;
                float hs2 = L - hhl;
                float sw = hhl * 0.5f, fl = bw * 1.3f;          // 倒钩后掠/外展
                float taper = Math.Max(10f, L * 0.12f);         // 尾部尖角张开区段
                using (GraphicsPath gp = new GraphicsPath())
                {
                    // 尾尖 V 收口 (用户: 左侧尖尖不要圆头) → 平行杆线 → V 形开口大倒钩 → 头尖, 一圈闭合线框
                    gp.AddPolygon(new PointF[] {
                        new PointF(0, 0),
                        new PointF(taper, -bw),
                        new PointF(hs2, -bw),
                        new PointF(hs2 - sw, -(bw + fl)),
                        new PointF(L, 0),
                        new PointF(hs2 - sw, bw + fl),
                        new PointF(hs2, bw),
                        new PointF(taper, bw),
                    });
                    using (Matrix mx = new Matrix())
                    {
                        mx.Rotate((float)(ang * 57.29578), MatrixOrder.Append);
                        mx.Translate(x1, y1, MatrixOrder.Append);
                        gp.Transform(mx);
                    }
                    using (Pen fp = new Pen(p.Color, lw2))
                    {
                        fp.StartCap = LineCap.Round; fp.EndCap = LineCap.Round; fp.LineJoin = LineJoin.Round;
                        g.DrawPath(fp, gp);
                    }
                }
                return;
            }'''
new = '''            if (style == Annot.S_HOLLOW)
            {
                // 样式② 空心线框 = 柳叶轮廓描边版 (用户: 与实心同一几何, 尖头到尾全程一个指向, 不要平行杆段)
                float L = (float)Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                if (L < 4) return;
                float lw2 = Math.Max(2.5f, w * 1.5f);           // 均匀描边宽 = 粗细×1.5
                float hhl = Math.Max(22f, L * 0.22f);
                if (hhl > L * 0.6f) hhl = L * 0.6f;
                float hs2 = L - hhl;
                float hhw = Math.Max(4f, w * 2.2f);             // 与实心版同杆宽参数
                float chw = hhw * 2.75f, sweep = hhl * 0.45f;   // 同实心版翼参数
                PointF[] poly = LocalArrow(new PointF[] {
                    new PointF(0, 0), new PointF(hs2, -hhw), new PointF(hs2 - sweep, -chw),
                    new PointF(L, 0), new PointF(hs2 - sweep, chw), new PointF(hs2, hhw) }, x1, y1, (float)ang);
                using (Pen fp = new Pen(p.Color, lw2))
                {
                    fp.LineJoin = LineJoin.Round;
                    g.DrawPolygon(fp, poly);                    // 闭合描边, 内部透明
                }
                return;
            }'''
assert old in s, 'hollow body'
s = s.replace(old, new, 1)

# ---- 2) 预览工厂: 背景填菜单深色 + HOLLOW 同步新几何 + THIN 保持 ----
old2 = '''            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float cy = H / 2f, x1 = 3, x2 = W - 3;'''
new2 = '''            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(40, 41, 46)); // 填菜单底色: 透明底会被 ToolStrip 图片缩放合成纯白 (用户实测预览看不见)
                float cy = H / 2f, x1 = 3, x2 = W - 3;'''
assert old2 in s, 'style factory bg'
s = s.replace(old2, new2, 1)

old3 = '''                        float LL = x2 - x1, hhl2 = LL * 0.34f, hs3 = LL - hhl2;
                        float hhw2 = 2.6f, chw2 = hhw2 * 2.6f, sw3 = hhl2 * 0.45f;
                        using (SolidBrush b = new SolidBrush(c))
                            g.FillPolygon(b, new PointF[] {
                                new PointF(x1, cy), new PointF(x1 + hs3, cy - hhw2), new PointF(x1 + hs3 - sw3, cy - chw2),
                                new PointF(x2, cy), new PointF(x1 + hs3 - sw3, cy + chw2), new PointF(x1 + hs3, cy + hhw2) });
                    }
                    else if (style == Annot.S_HOLLOW)
                    {
                        float bw2 = 3.4f, hs3 = (x2 - x1) * 0.68f, sw3 = 5.5f, fl2 = 4.2f, tp2 = (x2 - x1) * 0.13f;
                        using (GraphicsPath gp = new GraphicsPath())
                        {
                            gp.AddPolygon(new PointF[] {
                                new PointF(x1, cy), new PointF(x1 + tp2, cy - bw2), new PointF(x1 + hs3, cy - bw2),
                                new PointF(x1 + hs3 - sw3, cy - bw2 - fl2), new PointF(x2, cy),
                                new PointF(x1 + hs3 - sw3, cy + bw2 + fl2), new PointF(x1 + hs3, cy + bw2), new PointF(x1 + tp2, cy + bw2) });
                            using (Pen hp = new Pen(c, 1.9f)) { hp.LineJoin = LineJoin.Round; g.DrawPath(hp, gp); }
                        }
                    }'''
new3 = '''                        float LL = x2 - x1, hhl2 = LL * 0.34f, hs3 = LL - hhl2;
                        float hhw2 = 2.6f, chw2 = hhw2 * 2.6f, sw3 = hhl2 * 0.45f;
                        using (SolidBrush b = new SolidBrush(c))
                            g.FillPolygon(b, new PointF[] {
                                new PointF(x1, cy), new PointF(x1 + hs3, cy - hhw2), new PointF(x1 + hs3 - sw3, cy - chw2),
                                new PointF(x2, cy), new PointF(x1 + hs3 - sw3, cy + chw2), new PointF(x1 + hs3, cy + hhw2) });
                    }
                    else if (style == Annot.S_HOLLOW)
                    {
                        float LL = x2 - x1, hhl2 = LL * 0.34f, hs3 = LL - hhl2;
                        float hhw2 = 2.6f, chw2 = hhw2 * 2.6f, sw3 = hhl2 * 0.45f;
                        using (Pen hp = new Pen(c, 1.9f))
                        {
                            hp.LineJoin = LineJoin.Round;
                            g.DrawPolygon(hp, new PointF[] {
                                new PointF(x1, cy), new PointF(x1 + hs3, cy - hhw2), new PointF(x1 + hs3 - sw3, cy - chw2),
                                new PointF(x2, cy), new PointF(x1 + hs3 - sw3, cy + chw2), new PointF(x1 + hs3, cy + hhw2) });
                        }
                    }'''
assert old3 in s, 'style previews'
s = s.replace(old3, new3, 1)

old4 = '''            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float cy = H / 2f;
                using (Pen p = new Pen(Color.FromArgb(232, 234, 240), lw))'''
new4 = '''            Bitmap bmp = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(40, 41, 46)); // 同菜单底色, 防透明底合成纯白
                float cy = H / 2f;
                using (Pen p = new Pen(Color.FromArgb(232, 234, 240), lw))'''
# 粗细预览工厂背景 (注意与 style 工厂区分: 粗细版没有 SmoothingMode 行)
idx = s.find('MakeWidthPreviewBmp')
assert idx > 0
seg = s[idx:idx+900]
assert old4 in seg, 'width factory bg'
s = s[:idx] + seg.replace(old4, new4, 1) + s[idx+900:]

open(p, 'wb').write(s.encode('utf-8'))
print('previews fixed')
