# 「夹夹」界面主题方案（0.8.6，可切换）

目标：在现有「标准（暖纸赤陶）」之外新增一套克制的二次元主题「夹夹」，可在设置里切换，默认仍为标准。效果图见同目录 `jiajia-theme-mock.png`：① 现有历史页（按 0.8.4 真实渲染还原，作对照）；②③④ 为夹夹主题 + 调整后布局的历史页、待办页、配置页。效果图是 HTML 示意，不是最终像素稿，落地以真实 WPF 渲染为准。

## 范围与红线

- 主题本身只改视觉：配色、圆角、边框、选中样式、空状态插画与文案。**不改**窗口尺寸（340×540，最小 340×450）、五页导航、数据与捕获逻辑。
- 布局：用户已授权调整不合理的布局（见下文「布局调整」，分 P1/P2）。布局调整属于体验修复，**两套主题共用**，不随主题切换；不得改动功能行为、快捷键与数据。
- 默认主题 = 标准。主题切换不改变功能；P1 布局调整会让默认外观有小幅变化，需在 CHANGELOG 写明。
- 宠物沿用现有 `PetCompanion` 帧图（56×40，整数倍显示），不新增帧、不做非整数缩放。夹夹形象只映射真实状态：监听=待机、暂停=睡觉、新捕获=跳。
- 不做深色主题、不换字体、不改图标集、不改安装包图标。
- 文字对比度：正文与元信息 ≥ 4.5:1（见下表，已按奶油底 `#FFF9F2` 与白卡片核算）。赤陶 `#D97757` 在浅底仅 ≈3:1，**只用于非文字装饰**（夹子、指示条、徽标），不得用于小字。

## 已确认的现状（实现前提）

1. `Themes/Palette.xaml` 已把全部颜色集中为约 45 个 Brush 键，`Themes/Controls.xaml` 与 `MainWindow.xaml` 中**没有硬编码颜色**，所以换肤主要是换调色板。
2. **但这两个文件对资源的引用全部是 `StaticResource`**（Controls.xaml 158 处、MainWindow.xaml 87 处，DynamicResource 为 0），且 `App.xaml` 注释写明依赖加载顺序。静态引用在加载时就固定了，**运行时替换调色板不会更新已显示的窗口**。这是本任务的核心技术点。
3. 历史页顶部筛选条为 `FilterChipStyle`（全部/文本/图片/文件；置顶与快速收藏当前隐藏），**待办页的状态筛选也复用同一个样式**，改它会同时影响两处，两处都要在效果图风格下验收。
4. 控件模板里选中标记（左缘赤陶条）、页签选中态（沙色圆角块 + 赤陶下划线）、筛选条（墨色下划线）等**形状**是写死在模板里的，只换颜色无法得到效果图里的胶囊页签和描边卡片。
5. 以 0.8.4 的 LayoutCheck 真实渲染（`artifacts/review/0.8.4/layout/*-340x540.png`）为基准：标题栏图标是**蓝色**剪贴板，与赤陶配色冲突；历史行每条 72px，340×540 下只能完整看到约 4.5 条；所有历史行（文本/文件/链接）都用同一个文档图标，无法按类型区分；待办页有与页签重复的"待办"标题和较重的虚线统计格；配置页左侧文件夹栏在无内容时仍占约 1/3 宽度。
6. 注意：`MainWindow.xaml` 中置顶与快速收藏筛选当前为隐藏，实现时保持隐藏。

## 实现方案（建议）

### A. 主题资源拆分
- 新建 `Themes/Palette.Standard.xaml`（由现 `Palette.xaml` 转写：Brush 直接写字面颜色，不再用 `Color="{StaticResource ...}"` 互相引用）与 `Themes/Palette.Jiajia.xaml`，**两者键集合必须完全一致**。
- 间距、字号等与主题无关的键留在公共字典；圆角、边框粗细等随主题变化的键放进调色板字典。
- 新增主题相关 token（两套都要定义）：`BorderWidthStrong`（标准 1 / 夹夹 1.5）、`RowCardBrush`（标准=透明 / 夹夹=`#FFFFFF`），其余形状差异见 C。

### B. 引用改为 DynamicResource
- 把 `Controls.xaml`、`MainWindow.xaml` 及其它 XAML 里**指向调色板键的** `StaticResource` 改为 `DynamicResource`（Brush、CornerRadius、Thickness 等主题键）。`Style` 键、间距、字号保持 `StaticResource`。
- 用脚本按"调色板键名表"机械替换，避免误改 Style 引用；替换后全量构建。
- 代码里直接用画笔的地方（如 `CollapsedPetWindow`）逐一检查是否需要跟随主题。

### C. 形状差异
先读 `Controls.xaml` 里页签、`FilterChipStyle`、历史行、搜索框、按钮的模板，再决定：
- 能用 token 表达的（圆角、边框粗细、边框色）一律走 token；
- 必须改结构的（页签下划线→胶囊、选中左缘条→描边卡片+偏移阴影+夹子+折角）用主题专属样式字典 `Themes/Controls.Jiajia.xaml` 覆盖，并用 DynamicResource 引用 Style。
- 若 Style 动态切换在个别模板里不稳定，**降级方案**：切换主题后提示"重启生效"，启动时按设置加载对应字典。两种方式都必须保证不崩溃。

### D. 主题服务与设置
- 新增 `ThemeService`：`Apply(themeId)` 替换 `Application.Current.Resources.MergedDictionaries` 中的调色板与专属样式字典；启动时在创建任何窗口前应用已保存的主题。
- 设置存储新增 `Theme`（`standard` | `jiajia`，缺省 `standard`；未知值回退 `standard`，只在读取设置这一处校验）。
- `SettingsWindow` 增加"界面主题"选项（标准 / 夹夹），切换即生效并保存。

### E. 夹夹专属元素（均随主题开关）
| 元素 | 做法 |
|---|---|
| 标题栏左上角 | **已按用户要求定稿并已实现**：夹夹（`PetCompanion`，28×20）位于标题栏最左侧，替换蓝色图标；页脚不再放宠物（避免两只）；关闭"显示小宠物"时恢复原图标。应用图标、托盘与 MSI 图标本版不动 |
| 空状态 | 复用 `PetCompanion` 以 112×80（2 倍整数）显示：暂停=睡觉，监听且无内容=待机；配文见下表 |
| 选中行 | 描边 + 偏移阴影 + 赤陶夹子 + 右上折角（见效果图） |
| 页脚 | 保留现有宠物；"夹夹"小徽标为可选装饰，可省略 |
| 复制气泡"已复制，夹好啦" | **新增 UI，可选**，本版可不做 |

### F. 文案（由主题提供，不得散落硬编码）
| 场景 | 标准 | 夹夹 |
|---|---|---|
| 历史为空 | 现有文案 | 夹夹在等你复制点什么 |
| 已暂停 | 现有文案 | 已暂停监听，夹夹在打盹 |
| 搜索无结果 | 现有文案 | 夹夹没找到，换个关键词试试？ |

## 布局调整（用户已授权；效果图 ②③④ 为目标）

**P1：本版（0.8.6）完成**
1. **历史行更紧凑**：行高 72→56，图标 32→30；每行只保留一种分隔（夹夹为卡片描边，标准为一条发线），去掉"卡片 + 分隔线"双重线。340×540 下应能完整看到约 5.5 条。必须保留列表虚拟化。
2. **类型图标区分**（仅显示层判断，不改数据模型、不改筛选分类）：文本 `Aa`；以 `http(s)://` 开头且无空白的文本显示链接图标并标"链接"；图片用缩略图；文件按扩展名显示小徽标（XLS/DOC/PDF/ZIP…，其余用通用文件图标）。"文本"筛选仍包含链接。
3. **元信息**：当天条目时间只显示 `HH:mm`（省略"今天"），其余保持原格式。保留"类型 · 来源应用 · 时间"三段。
4. **计数上移**：把页脚的"N 条历史"移到筛选条右侧（"N 条"），页脚高度 34→30，仅保留"Enter 使用"（宠物已在标题栏）。其它页的页脚计数（"N 项待办/配置"）保持。
5. **待办页**：去掉与页签重复的"待办"标题；状态筛选（全部/进行中/已完成）与范围筛选（本月/今年）合并为一行；四个统计（完成/待完成/新增/完成率）改为一行小胶囊，**保留点击下钻与再次点击取消筛选的行为**；"导出明细"按钮放在统计行里"完成率"之后（**已实现**，效果图里放页脚是旧稿，以此条为准）。
6. **标题栏品牌标**：已实现，见 E 表（夹夹在左上角）。

**P2：可延期到 0.8.7（先读代码再定，风险较高）**
7. **配置页**：左侧文件夹栏改为顶部分类条（全部/各文件夹/＋新建），搜索与「导入/导出/添加」并为一行。必须保留文件夹的新建、重命名、删除、右键菜单与现有键盘行为；若这些交互与左栏耦合过深，则只做"无文件夹内容时收窄左栏"的小改动，并在 CHANGELOG 注明。

**不改**：计算页、划词页、窗口尺寸、导航顺序、快捷键。

**布局验收**：340×450 最小尺寸下无裁切；Tab 键顺序不变；控件的 AutomationProperties 名称不变；LayoutCheck 的测量阈值如需更新，要在变更说明里写明原因（不要为了通过而放宽）。

## 夹夹调色板（目标值，按语义映射到现有键）

| 语义 / 现有键 | 标准 | 夹夹 |
|---|---|---|
| LabelPrimary | `#1F1E1C` | `#2B2622` |
| LabelSecondary | `#6E6B63` | `#6F6258`（奶油底 5.6:1） |
| LabelTertiary（元信息） | `#A39F94`（仅 2.5:1，沿用不动） | `#78695E`（奶油底 5.1:1、白底 5.3:1） |
| Accent / AccentBrand | `#D97757` | `#D97757` |
| AccentBrandStrong | `#B85C38` | `#B85C38` |
| Success / Warning / Danger | `#788C5D` / `#C4653F` / `#9C3D28` | `#6E8A5A` / `#C4653F` / `#A8402B` |
| WindowSurface / Vibrancy / Fallback / PanelBackground / Popover | `#FAF9F5`/`#F5F4EE` | `#FFF9F2` |
| WindowChrome / Chrome（标题栏、页脚） | `#F5F4EE` | `#FFF1E6` |
| 历史行卡片（RowCardBrush） | 透明 | `#FFFFFF` |
| SegmentedTrack | `#EDEAE0` | `#F6E6D8` |
| Separator / BorderSubtle | `#E8E6DC` | `#EAD9CC` |
| BorderStrong | `#D0CDC2` | `#2B2622`（墨线描边） |
| ControlFill / Hover / Pressed / Disabled | `#F1EFE7`/`#E9E6DC`/`#E0DDD2`/`#F5F4EE` | `#FFF1E6`/`#FFE7D6`/`#FBDCCF`/`#FFF9F2` |
| HoverOverlay / PressedOverlay | `#101F1E1C` / `#201F1E1C` | `#102B2622` / `#202B2622` |
| Selection / SelectionInactive | `#F0E9DE` / `#EDEAE0` | `#FDE7DD` / `#FBEFE6` |
| AccentSoft（页签/筛选选中底） | `#F7EDE4` | `#FBDCCF` |
| ThumbnailBackdrop | `#EFECE4` | `#FFF1E6` |
| ScrollThumb / Hover | `#B9B5AA` / `#8A8578` | `#D9C3B3` / `#B89F8D` |
| Tooltip | `#1F1E1C` | `#2B2622` |
| RadiusSmall / Medium / Large | 4 / 6 / 10 | 8 / 12 / 16 |
| RadiusPill | 3 | 999（胶囊，**先确认该键现用于何处**再改） |

注：选中行底 `#FDE7DD` 上的元信息对比度仅约 4.4:1，选中行内元信息改用 LabelSecondary。表中夹夹值是目标，落地时以效果图为准做目视核对；个别键语义不明时先读模板再定。

## 验收

1. 构建与全部现有测试通过；`dotnet test` 无回归。
2. LayoutCheck 增加"两套主题各渲染一遍"：五页 × 三种尺寸，输出到 `artifacts/review/0.8.6/layout/{standard,jiajia}/`；并增加断言：两套调色板键集合完全一致、无缺键。
3. 手工点验（真实桌面）：标准↔夹夹来回切换 ≥10 次不崩溃、无闪烁残留、已打开的设置窗口也随之变色；100%/125%/150% 缩放；收起为宠物再展开后主题保持；待办页筛选条两套主题下都正常。
4. 默认主题为标准：全新安装与从 0.8.5 升级后配色与 0.8.5 一致；布局以 P1 调整为准（行高、类型图标、计数位置、待办页、标题栏图标），其余不变。
5. 未实际在 Windows 上跑过的项，不得在 CHANGELOG/README 里写成"已验证"。

## 版本与文档

- 版本升至 0.8.6：`Directory.Build.targets`（0.8.6 / `0.8.6-jiajia-theme`）、`release/release-contract.json`、`scripts/build-release.ps1` / `build-msi.ps1` / `publish-single-exe.ps1` 默认值、`installer/ClipboardManager.Package.wixproj` 默认值、LayoutCheck 输出目录。
- 更新 CHANGELOG（Unreleased 新增 0.8.6）、README 顶部状态段、AGENTS.md（新增"主题"约束：仅视觉；两套调色板键必须一致；新增控件必须引用主题键并使用 DynamicResource；禁止硬编码颜色）。
- 打包：`.\scripts\build-release.ps1 -Version 0.8.6`。

## 风险

- StaticResource→DynamicResource 改动面大（约 245 处引用），按键名表机械替换并全量验证；个别 Freezable/Storyboard 里的动态引用可能需要保持静态或改写。
- 形状覆盖若与现有模板耦合紧，退回"重启生效"方案，不要为运行时切换牺牲稳定性。

## 0.8.6 当前实现进度（云端未编译版本）

已做：调色板引用改 DynamicResource；`Palette.Jiajia.xaml` 覆盖层（仅颜色+圆角）；`ThemeService`；`Theme` 设置与设置页下拉；夹夹移到标题栏；导出明细移到完成率后；去掉重复"待办"标题。
**未做（效果图差距的来源）**：C 形状差异（胶囊页签、历史行描边卡片、选中行偏移阴影+夹子+折角、筛选胶囊、搜索框/按钮圆润描边）；E 空状态宠物与文案、F 主题文案；P1 的 1、2、3、4、5（统计小胶囊、筛选合并）；LayoutCheck 双主题渲染。
实现思路：把形状差异做成调色板层里的 token（如 `RowCornerRadius`、`RowBorderThickness`、`RowBorderBrush`、`RowCardBrush`、`NavPillRadius`、`SelectionDecorVisibility`），标准主题取值保持现状，夹夹覆盖；控件模板一律引用这些 token（DynamicResource）。装饰元素（夹子、折角）用 `SelectionDecorVisibility`（标准 Collapsed / 夹夹 Visible）控制。
