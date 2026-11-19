# 夹夹主题 · 活页版（2026-11-19）

> 给本机 Claude Code 的执行说明。设计稿：同目录 `prototype.html`（4 个 300×640 窗口 + 动效与色板）。
> 方向：品牌青 RGB(16,115,107) = `#10736B`；组件与动效参照 Looseleaf（https://kevintngo.github.io/looseleaf/）——青灰桌面、活页纸、铅笔描边、荧光笔标记选中、错位墨影、「一次回弹后静止」的动效。
> 原则：**替换现有「夹夹」主题的视觉，不改结构。**主题 id 仍为 `jiajia`，显示名「夹夹」不变；窗口尺寸、导航、布局、功能、快捷键、数据、宠物帧图都不动。

## 直接粘贴给 Claude Code 的指令

```
读 AGENTS.md 的「界面主题」约束和 docs/design/2026-11-19-jiajia-looseleaf/jiajia-looseleaf-spec.md，
按文档把夹夹主题换成「活页版」。只改 Palette.Jiajia.xaml 的取值和文档第 3 步列出的模板，
标准 / Kami / Kubai 三套主题像素与行为必须不变。跑构建、全部测试和 LayoutCheck，
截夹夹主题下历史（含选中/空/暂停）、待办、配置、计算、共享的图放到 artifacts/screenshots/jiajia-v2-*.png，
对照 prototype.html 报告差异。不要发布版本。
```

## 1. 设计要点

| 元素 | 活页版 | 旧夹夹 |
|---|---|---|
| 标题栏、页签带、页脚 | 青灰桌面 `#D3E5DF` | 奶油 `#FFF1E6` |
| 内容区 | 活页纸 `#FBF8F1`，可选纸纹 | 奶油 `#FFF9F2` |
| 当前页签 | 荧光笔块 `#F8DC4C`，不规则圆角 `6,3,7,3`，无描边 | 墨线胶囊 + 粉底 |
| 筛选 / 文件夹选中 | 淡荧光 `#FCEFA8` | 粉底胶囊 |
| 历史行、配置卡 | 卡片 `#FFFDF8` + 1.25 铅笔线 `#D9D0C1`，圆角 4 | 白卡 + 1.5 驼色线，圆角 12 |
| 选中行 | 淡荧光底 `#FFF3BF` + 1.5 墨线 + 3px 墨影 + **品牌青夹子** + 折角 | 粉底 + 墨影 + 赤陶夹子 |
| 主按钮 / 焦点 / 勾选 | 品牌青 `#10736B` | 赤陶 |
| 空状态 | 夹夹 + 淡荧光便签（贴一段纸胶带），文案不变 | 夹夹 + 文字 |

规则：**荧光笔 = 选中，青 = 品牌与主操作，墨 = 文字与描边**。不再用赤陶（宠物帧图自带的赤陶帽子除外，帧图不改）。字体不换。

## 2. `Palette.Jiajia.xaml` 取值（整体替换现有取值）

键必须仍是 `Palette.xaml` 的子集。表里若有键在 `Palette.xaml` 不存在，跳过并在报告里列出（第 3 步新增的键除外）。

| 键 | 值 |
|---|---|
| LabelPrimaryBrush / LabelSecondaryBrush / LabelTertiaryBrush | `#2A2420` / `#4A423B` / `#625C55` |
| AccentBrush, AccentBrandBrush / AccentBrandStrongBrush | `#10736B` / `#0B5A54` |
| SuccessBrush / WarningBrush / DangerBrush | `#2F6B45` / `#97481F` / `#B33A26` |
| WindowChromeBrush, ChromeBrush, WindowVibrancyBrush, WindowFallbackBrush | `#D3E5DF` |
| WindowSurfaceBrush, SurfaceBrush, SurfaceOpaqueBrush, PanelBackgroundBrush, PopoverBrush | `#FBF8F1` |
| ControlFillBrush, SearchFillBrush, RowCardBrush | `#FFFDF8` |
| ControlFillHoverBrush / ControlFillPressedBrush / ControlFillDisabledBrush | `#F5F0E4` / `#EFE9DC` / `#F5F2EA` |
| SegmentedTrackBrush, ThumbnailBackdropBrush, SelectionInactiveBrush | `#EFE9DC` |
| SeparatorBrush, BorderSubtleBrush, RowBorderBrush | `#D9D0C1` |
| BorderStrongBrush | `#8E887F` |
| PanelBorderBrush | `#A9C9BE`（桌面与纸的交界线） |
| HoverOverlayBrush / PressedOverlayBrush | `#0A3A2C1E` / `#143A2C1E` |
| RowHoverBrush | `#FFFDF8`（底色不变，悬停靠上浮和墨影表达，见第 3 步） |
| SelectionBrush | `#FFF3BF` |
| AccentSoftBrush, ChipCheckedBackgroundBrush | `#FCEFA8` |
| NavSelectedBackgroundBrush / NavSelectedForegroundBrush | `#F8DC4C` / `#2A2420` |
| RowSelectedBorderBrush, SelectionDecorBrush, SelectionEdgeBrush, NavPillBorderBrush | `#2A2420` |
| RowSelectedMetaBrush | `#4A423B` |
| NavMarkBrush, ChipMarkBrush | `#00FFFFFF` |
| ScrollThumbBrush / ScrollThumbHoverBrush | `#C9C0B1` / `#8E887F` |
| TooltipBrush | `#2A2420` |
| RadiusSmall / RadiusMedium / RadiusLarge / RadiusPill | `3` / `4` / `6` / `4` |
| RowCornerRadius / SearchCornerRadius | `4` / `4` |
| NavPillRadius / ChipCornerRadius | `6,3,7,3` / `5,3,6,3` |
| RowBorderThickness / NavPillBorderThickness / NavBottomBorderThickness | `1.25` / `0` / `0` |
| RowCardInset | `0,4,0,4`（不变） |
| SelectionEdgeThickness | `0` |
| RowSeparatorVisibility / SelectionDecorVisibility / EmptyPetVisibility | `Collapsed` / `Visible` / `Visible` |
| PopoverShadow | `BlurRadius 0, ShadowDepth 3, Direction 315, Opacity 0.18, Color #3A2C1E`（错位实影，不模糊） |
| CopyEmptyHistory / CopyPausedHistory / CopyNoResults | 不变 |

对比度（已核算）：正文 14.4、次要 9.3、元信息 6.2（纸底）；元信息在桌面 5.0、在选中底 5.9；品牌青在纸底 5.4、白字在品牌青 5.6。**桌面上的青色文字一律用 `#0B5A54`**（`#10736B` 在桌面只有 4.35）。

## 3. 模板改动（只在夹夹主题生效）

先读 `Controls.xaml` 相关模板再动手。新增键一律同时加进 `Palette.xaml`，取「无效果」的值，保证其他主题像素与行为不变（LayoutCheck 子集断言 + 截图对比）。

1. **选中行的墨影**：`CardRowItemStyle` 的 `Shadow` 现在偏移 2,2；改为读新键 `SelectionShadowOffset`（`Palette.xaml` = 2，夹夹 = 3）。夹子已用 `AccentBrandBrush`，换色后自动变青，不用改。
2. **悬停上浮 / 按下下沉**（新键 `RowHoverLift`、`RowPressSink`，`Palette.xaml` 都 = 0；夹夹 = −1 / 1）：Root 加 `TranslateTransform`；悬停时 Y = RowHoverLift 并显示 2,2 偏移、`#383A2C1E` 的悬停影（新键 `RowHoverShadowBrush`，`Palette.xaml` = 透明）；按下时 Y = RowPressSink、悬停影隐藏。不旋转（WPF 旋转后文字发虚）。
3. **页签荧光笔划过**：选中页签的背景块从左向右 ScaleX 0→1。仅当新键 `NavSelectWipe`（Visibility 或 bool，`Palette.xaml` 关）开启时播放；关闭时保持现状的瞬时切换。
4. **空状态便签**：`EmptyPetHost` 下方的文案外包一层便签（新键 `EmptyNoteBrush` = `#FCEFA8`、`EmptyNoteVisibility`；`Palette.xaml` 为透明 / Collapsed），便签 `RotateTransform −1.5°`、2,3 偏移淡影、顶部一条半透明纸胶带。暂停时夹夹睡觉，文案用现有 `CopyPausedHistory`。
5. **纸纹（可选）**：内容区用现有 `Themes/PaperTexture.cs` 生成的平铺纸纹，振幅不超过它当前的默认值；若会影响列表滚动性能则不做，并在报告里说明。
6. **页脚 Enter 键帽（可选，P2）**：「Enter 使用」里的 Enter 画成键帽（卡片底、1.25 铅笔线、下沿 2.5）。

## 4. 动效（Looseleaf：短、像纸、一次回弹后静止）

| 场景 | 时长 | 曲线 | 效果 |
|---|---|---|---|
| 悬停 | 120 ms | ease-out | 上浮 1px，墨影出现 |
| 按下 | 90 ms | ease-out | 下沉 1px，墨影收回 |
| 选中 | 300 ms | settle | 底色、墨线、墨影到位 |
| 夹子 | 180 ms | pop | 缩放 0.4→1、旋转 −14°→0（夹子是图形，可旋转） |
| 页签 | 180 ms | ease-out | 荧光笔块 ScaleX 0→1，原点在左 |
| 新记录 | 340 ms | settle | 从上方 10px 落下、透明度 0→1；标题栏夹夹播放现有 hop 帧 |

曲线：settle = `cubic-bezier(.2,.9,.25,1.12)`，pop = `cubic-bezier(.2,.9,.3,1.15)`。WPF 没有越界的贝塞尔缓动，用 `BackEase { Amplitude = 0.3, EasingMode = EaseOut }` 近似 settle，`Amplitude = 0.45` 近似 pop。

约束：
- 系统关闭动画（`SystemParameters.ClientAreaAnimation == false`）时全部时长为 0，直接到终态。
- 列表行保持虚拟化；动画只用 RenderTransform 和 Opacity，不动布局属性。
- 新记录落下只对插入到列表顶部的那一条播放，滚动和筛选不触发。
- Storyboard 里不能用 DynamicResource；若按主题开关动画在模板里做不干净，改用一个附加行为在代码里读主题键启动动画，且其他主题下不创建任何 Storyboard。

## 5. 不做

窗口尺寸 300×640 与最小尺寸、导航顺序、页面布局、计算页与共享页的结构、宠物帧图与位置、深色模式、换字体、其他三套主题、新增主题选项。

## 6. 验收

- Release 构建 0 警告 0 错误，全部单元测试通过。
- LayoutCheck：4 套主题 × 全部页面 + 设置页通过；覆盖层子集断言通过；新增键在其他主题下为「无效果」值。
- 标准、Kami、Kubai 与改动前截图逐页对比无变化。
- 夹夹主题截图：`artifacts/screenshots/jiajia-v2-{history,history-empty,history-paused,todo,config,calc,share}.png`。
- 列出与 `prototype.html` 的差异（字体、纸纹与 WPF 渲染造成的差异属预期）。
- 动画只能在真实桌面看，未实机点验的不得写成「已验证」。

## 7. 文档

- `AGENTS.md`「界面主题」：夹夹改为活页版，写明新增键及其在其他主题的默认值。
- `CHANGELOG.md` Unreleased：「夹夹主题换成活页版（品牌青、荧光笔选中、纸感动效）」。不改版本号，不发布。
