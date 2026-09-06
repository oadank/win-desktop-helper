# -*- coding: utf-8 -*-
"""win-desktop-helper 截图 UI 像素验收 (WorkBuddy 2026-09-06)

用法: python verify_popup_pixels.py <PixPin截图.png> [x0 y0 x1 y1]

检查项 (全自动, 不靠肉眼/不靠模型幻觉):
  1. 主工具栏 / 属性栏 / 弹层 底色是否统一为 (40,41,46)
  2. 是否存在近黑残留 (26,27,31)  —— 老版本工具栏底色, 与弹层深灰叠出"白黑套色"
  3. 是否存在纯白块 (>=245,>=245,>=245) 大面积 —— 缩略图容器白底
  4. 蓝高亮块 (51,133,255) 是否存在且与弹层边缘不接触 (PixPin 内缩风格)
  5. 可选: 传入弹层矩形 (物理像素) 做定点检查

注意:
  - /shot 与 PrintScreen 都拍不到自家遮罩/工具栏 (分层窗被排除), 必须用 PixPin 抓图
  - PixPin: powershell -File .\\pixpin-shot.ps1  ->  %USERPROFILE%\\Pictures\\Screenshots\\PixPin_*.png
  - 探针坐标是物理像素 (服务声明 PerMonitorV2); python/PIL 读图无需 DPI 处理
"""
import sys
import numpy as np
from PIL import Image
from collections import Counter

BG_OK = (40, 41, 46)      # 统一后的面板底色
BG_OLD = (26, 27, 31)     # 旧工具栏近黑 (bug 源)
BLUE = (51, 133, 255)     # 选中蓝块


def near(a, c, tol):
    return (np.abs(a - np.array(c)).sum(axis=2) < tol)


def top_colors(region, n=5):
    cnt = Counter(map(tuple, region.reshape(-1, 3)))
    tot = region.shape[0] * region.shape[1]
    return [(tuple(int(v) for v in c), round(n2 / tot * 100, 1)) for c, n2 in cnt.most_common(n)]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    a = np.array(Image.open(path).convert('RGB')).astype(int)
    H, W, _ = a.shape
    print('image %dx%d' % (W, H))

    rect = None
    if len(sys.argv) >= 6:
        rect = tuple(int(v) for v in sys.argv[2:6])

    ok = True

    # 1+2+3: 全局
    white = ((a >= 245).all(axis=2)).sum()
    old = near(a, BG_OLD, 6).sum()
    cur = near(a, BG_OK, 6).sum()
    print('\n[GLOBAL]')
    print('  统一底色 (40,41,46) px : %d' % cur)
    print('  近黑残留 (26,27,31) px : %d   %s' % (old, 'OK' if old < 20000 else '!! 仍有近黑块'))
    print('  纯白 px (>=245)        : %d   %s' % (white, 'OK' if white < 5000 else '!! 疑似白底块'))
    if old >= 20000 or white >= 5000:
        ok = False

    # 4: 蓝块
    blue = near(a, BLUE, 40)
    print('\n[BLUE 选中块]')
    if blue.sum() == 0:
        print('  !! 未找到蓝块 (弹层可能没弹出)')
        ok = False
    else:
        ys, xs = np.where(blue)
        print('  bbox x %d..%d y %d..%d (w=%d h=%d) px=%d'
              % (xs.min(), xs.max(), ys.min(), ys.max(),
                 xs.max() - xs.min() + 1, ys.max() - ys.min() + 1, blue.sum()))

    # 5: 定点弹层检查
    if rect:
        x0, y0, x1, y1 = rect
        sub = a[y0:y1, x0:x1]
        print('\n[POPUP rect %d,%d-%d,%d] top colors:' % (x0, y0, x1, y1))
        for c, p in top_colors(sub, 4):
            print('   %s  %.1f%%' % (c, p))
        r_old = near(sub, BG_OLD, 6).sum()
        r_cur = near(sub, BG_OK, 6).sum()
        if r_old > sub[:, :, 0].size * 0.05:
            print('  !! 弹层内含近黑块 %d px' % r_old)
            ok = False
        if r_cur < sub[:, :, 0].size * 0.25:
            print('  !! 弹层内统一底色占比过低 (%.1f%%) — 矩形可能框偏'
                  % (r_cur / sub[:, :, 0].size * 100))
            ok = False

    print('\n=== %s ===' % ('PASS' if ok else 'FAIL'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
