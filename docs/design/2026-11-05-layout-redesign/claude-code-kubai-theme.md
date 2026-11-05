# 任务：新增「Kubai」主题（视觉取自 prototype.html）

> 给本机 Claude Code 的执行说明。设计稿：同目录的 `prototype.html`（看「展开 880 · 浅色 · 彩色」这一组）。
> 原则：**只加一套主题，不改结构。**窗口尺寸、导航顺序与位置、页面布局、功能、快捷键、数据都不动；原型里的三栏、详情栏、停靠形态、七套色板、深色模式**都不在本次范围**。

## 直接粘贴给 Claude Code 的指令

```
读 AGENTS.md 的「界面主题」约束和 docs/design/2026-11-05-layout-redesign/claude-code-kubai-theme.md，
按文档新增第 4 套主题 kubai（显示名 Kubai，说明「清透」）。只改视觉，不改窗口尺寸、导航、布局和功能。
按文档第 1–6 步做完，跑构建、全部测试和 LayoutCheck，截 kubai 主题下 5 个页面的图放到
artifacts/screenshots/kubai-*.png，最后对照 prototype.html 报告差异。不要发布版本。
```

## 1. 主题注册（4 处，和 jiajia / kami 同一套路）

| 位置 | 改动 |
|---|---|
| `Services/ThemeService.cs` | `Layers` 加 `["kubai"] = pack://application:,,,/Themes/Palette.Kubai.xaml` |
| `Domain/Models/AppSettingsValidator.cs` | 允许 `"kubai"`；提示改为「界面主题只能是标准、夹夹、Kami 或 Kubai。」并补测试 |
| `SettingsWindow.cs` | `themes` 数组加 `("kubai", "Kubai", "清透")`；`ThemePalette` 加 kubai 的 Merge |
| `Themes/Palette.Kubai.xaml` | 新建覆盖层，见第 2 步 |

## 2. `Palette.Kubai.xaml` 取值

覆盖层的键必须是 `Palette.xaml` 键的子集（LayoutCheck 会断言）。下表里若有键在 `Palette.xaml` 中不存在，**跳过并在报告里列出**，不要为此新增键（第 3 步的图标键除外）。

| 键 | 值 | 原型里的来源 |
|---|---|---|
| WindowSurfaceBrush, SurfaceBrush, SurfaceOpaqueBrush, PanelBackgroundBrush, PopoverBrush | `#FFFFFF` | 窗口底 |
| WindowChromeBrush, ChromeBrush, WindowVibrancyBrush, WindowFallbackBrush, ControlFillDisabledBrush | `#F7F7F8` | 导航轨 |
| SegmentedTrackBrush, ControlFillBrush, ThumbnailBackdropBrush, SearchFillBrush, SelectionInactiveBrush | `#F2F3F5` | 搜索框、图标块 |
| ControlFillHoverBrush / ControlFillPressedBrush | `#EBECEF` / `#E3E5E9` | |
| SeparatorBrush, BorderSubtleBrush, PanelBorderBrush | `#EDEEF0` | 细分隔线 |
| BorderStrongBrush | `#DCDEE2` | |
| LabelPrimaryBrush / LabelSecondaryBrush / LabelTertiaryBrush | `#1F2329` / `#6B707A` / `#A2A6AE` | 三级文字 |
| AccentBrush, AccentBrandBrush / AccentBrandStrongBrush | `#5393FF` / `#3F7BE6` | 主色 |
| SuccessBrush / WarningBrush / DangerBrush | `#2F9E6E` / `#D9822B` / `#E5484D` | |
| HoverOverlayBrush, RowHoverBrush / PressedOverlayBrush | `#09141820` / `#12141820` | |
| SelectionBrush, AccentSoftBrush | `#ECF3FF` | 主色 11% 叠白 |
| RowSelectedMetaBrush | `#6B707A` | |
| ScrollThumbBrush / ScrollThumbHoverBrush | `#D5D7DB` / `#A2A6AE` | |
| TooltipBrush | `#1F2329` | |
| RadiusSmall / RadiusMedium / RadiusLarge / RadiusPill | `8` / `11` / `16` / `8` | |
| RowCornerRadius | `13` | 列表行 |
| RowBorderThickness, RowCardInset, SelectionEdgeThickness | `0` | 无左缘条、无卡片边 |
| RowCardBrush, RowBorderBrush, RowSelectedBorderBrush, SelectionEdgeBrush, NavMarkBrush, ChipMarkBrush, NavPillBorderBrush | `#00FFFFFF` | |
| RowSeparatorVisibility, SelectionDecorVisibility | `Collapsed` | 去掉行分隔线和夹子装饰 |
| NavPillRadius / NavPillBorderThickness / NavBottomBorderThickness | `9` / `0` / `0` | |
| NavSelectedBackgroundBrush / NavSelectedForegroundBrush | `#F2F3F5` / `#1F2329` | 选中页签是浅灰底、深色字 |
| ChipCornerRadius / ChipCheckedBackgroundBrush | `8` / `#F2F3F5` | |
| SearchCornerRadius | `11` | |
| PopoverShadow | BlurRadius 24，ShadowDepth 4，Opacity 0.12 | |
| CopyEmptyHistory / CopyPausedHistory / CopyNoResults | 复制点什么，它会出现在这里 / 监听已暂停 / 没有匹配的记录 | |

字体不换（Windows 上没有 Geist，不随包分发字体）。选中态必须在白底上清晰可辨：`#ECF3FF` 底 + 深色字，若 LayoutCheck 的对比度断言不过，把 SelectionBrush 加深到 `#E1ECFF` 再试。

## 3. Kubai 图标（只在 kubai 主题显示）

### 3.1 素材
- 在仓库**外**的临时目录克隆 `https://github.com/kubai087/kubai-icons`，用它的 `scripts/export.mjs` 导出静态图标，每个导出静止和悬停两态：
  ```
  node scripts/export.mjs --only 时钟 --accent #5393FF --size 96 --out <tmp>/rest
  node scripts/export.mjs --only 时钟 --accent #5393FF --size 96 --state hover --out <tmp>/hover
  ```
- WPF 不能直接渲染这些 SVG（含遮罩和滤镜），用无头 Chrome 把每个 SVG 栅格化为 96×96 PNG，放到 `src/ClipboardManager.Windows/Assets/Icons/Kubai/<键名>.png` 与 `<键名>-hover.png`，Build Action = Resource。
- 许可：示例图标可在产品内使用、无需署名，但**不得单独作为图标包分发**；不要把 kubai-icons 的 `PROMPT.md`、`SKILL.md` 拷进仓库。在该目录放一个 `NOTICE.txt` 写明来源与许可名称。

| 用途 | Kubai 图标名 | 主色 | 资源键 |
|---|---|---|---|
| 页签：历史 | 时钟 | `#5393FF` | IconNavHistory |
| 页签：配置 | 文件夹 | `#F59E0B` | IconNavConfig |
| 页签：待办 | 便签 | `#EC4899` | IconNavTodo |
| 页签：计算 | 计算器 | `#22C55E` | IconNavCalc |
| 页签：共享 | 同步 | `#06B6D4` | IconNavShare |
| 历史行：文本 | 文档 | `#6366F1` | IconRowText |
| 历史行：链接 | 链接 | `#14B8A6` | IconRowLink |
| 历史行：文件 | 文档 | `#F97316` | IconRowFile |
| 配置页文件夹栏 | 文件夹 | `#F59E0B` | IconFolder |

每个键另有 `…Hover` 版本（如 `IconNavHistoryHover`）。

### 3.2 新增键（同时加进 `Palette.xaml`，保证子集规则成立）
- `ThemeIconVisibility`：`Palette.xaml` = `Collapsed`，`Palette.Kubai.xaml` = `Visible`。
- 上表全部 `Icon*` / `Icon*Hover`（`ImageSource`）：`Palette.xaml` 定义为 `{x:Null}`，`Palette.Kubai.xaml` 指向 PNG。

### 3.3 模板（视觉改动，三套旧主题下必须与现在像素一致）
1. **页签** `SegmentedTabItemStyle`：Header 左侧加 20×20 `Image`，间距 6，`Visibility` 绑 `ThemeIconVisibility`。图标源用一个附加属性（如 `ThemeIcon.Rest` / `ThemeIcon.Hover`，类型 `ImageSource`），在 `MainWindow.xaml` 各 TabItem 上用 `DynamicResource` 设置。`IsMouseOver` 或 `IsSelected` 时显示悬停图，两图叠放，用 160ms 不透明度渐变切换。页签 `MinHeight` 34 不变，团队页签保持隐藏。
2. **历史行图标块**：现有 `Border` 内加 `Image`（20×20，居中）。`HistoryItemViewModel` 新增 `IconKind`（Text / Link / File / Image），模板里用 DataTrigger 把 `Source` 设为对应的 `DynamicResource`。kubai 主题下隐藏 `IconText` 文本，图片项仍显示缩略图。选中行显示悬停图。
3. **配置页文件夹栏**：名称左侧加 18×18 的 IconFolder，选中项用 IconFolderHover（打开的文件夹）。
4. 所有图片设 `RenderOptions.BitmapScalingMode="HighQuality"`；不得硬编码颜色或文案。

## 4. 不做
窗口尺寸 340×540、导航在顶部、页脚、配置页两栏、计算页布局、夹夹宠物形象与位置、深色模式、色板切换、原型里的详情栏与停靠形态。

## 5. 文档
- `AGENTS.md`「界面主题」：主题数改为四套，写明 `ThemeIconVisibility` 与 `Icon*` 键及「其他主题为 Collapsed / x:Null」。
- `CHANGELOG.md` 的 Unreleased 加一条「新增 Kubai 主题（清透配色 + 彩色分层图标）」。不改版本号，不发布。

## 6. 验收
- Release 构建 0 警告 0 错误，全部单元测试通过（含 validator 新用例）。
- LayoutCheck：主题循环扩到 4 套 × 全部页面 + 设置页，无横向溢出；覆盖层子集断言包含 kubai；新增断言「非 kubai 主题下所有主题图标不可见，kubai 下可见且 Source 非空」。
- 三套旧主题与改动前截图逐页对比无变化。
- `artifacts/screenshots/kubai-{history,config,todo,calc,share}.png`，并列出与 prototype.html 的已知差异（结构类差异属预期）。
