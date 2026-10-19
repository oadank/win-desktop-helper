<!-- buddy-checkin 专项经验册(由 update_skill 自动建)。写经验请标日期, 拿不准的写"未验证"。 -->
# buddy-checkin 专项经验


## CDP 页面坐标点击能开账号菜单（旧结论作废）+ 加油站卡片状态读法

_记录日期: 2026-10-19 · 应用: buddy-checkin_

【2026-10-19 重大更正】「账号菜单不吃合成事件」的旧结论已作废：WorkBuddy 新版（Chrome/138 壳）的账号菜单是页面内渲染弹层，CDP click 用【页面坐标】点头像按钮 .user-menu-trigger 能正常打开菜单，再点 .fuel-menu-entry「Buddy加油站」也能正常弹出加油站卡片轮播。旧失败案例的真根因是【把屏幕坐标当页面坐标喂 CDP click】（差 dpr 倍且 y 超出 viewport，点到页面外）。正确链路（15:48 实测全通）：① cdp elements selector=.user-menu-trigger 拿 cx/cy（页面坐标）→ ② cdp click cx cy → ③ elements selector=[class*=fuel] 读菜单项 cx/cy → ④ cdp click 菜单项 → ⑤ 截图或 elements 读卡片状态。⚠️ 加油站轮播卡片的「今日已领取/立即领取」按钮在 cdp elements 搜文字会 0 条（在轮播深层/影子根外），用 /shot 截卡片区域（窗口左下 y≈1150-1400）肉眼判状态最稳。mouse 物理点击在该 Electron 窗口不稳定（返回 ok 但菜单不弹，原因未明），CDP 页面坐标点击是首选。
