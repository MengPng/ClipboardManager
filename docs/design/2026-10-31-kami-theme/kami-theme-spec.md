# 「Kami 紙」界面主题方案（第三套，可切换）

来源：设计体系 Kami 紙（暖米纸底、油墨蓝点缀、衬线、扁平）。效果图 `kami-theme-mock.png` 是 HTML 示意（云端无中文衬线字体，中文以无衬线渲染），落地以真实 WPF 渲染为准。

## 原则（取自 Kami）
- 唯一强调色 brand `#1b365d`，面积 ≤5%；灰色一律偏黄褐；平面，不用阴影；层级靠留白、底色和 1px 暖沙细线；不加强调边。
- 布局沿用 0.8.6 已定的紧凑布局（行高 56、类型图标、HH:mm、计数在筛选条右侧、待办统计一行、夹夹在标题栏左上角、导出明细在完成率后）。

## 调色板覆盖层 `Themes/Palette.Kami.xaml`（只覆盖不同的键，作为最后合并字典）
| 键 | 值 | 说明 |
|---|---|---|
| WindowSurface/Surface/SurfaceOpaque/PanelBackground/Popover | `#FFF5F4ED` | canvas |
| WindowChrome/Chrome/WindowVibrancy/WindowFallback | `#FFF5F4ED` | 标题栏、页脚与窗面同色，靠细线分隔 |
| ControlFill（搜索框、卡片） | `#FFFAF9F5` | ivory |
| ControlFillHover/Pressed/Disabled | `#FFE8E6DC` / `#FFDEDBCF` / `#FFF5F4ED` | warm-sand 系 |
| SegmentedTrack | `#FFE8E6DC` | |
| Separator/BorderSubtle/PanelBorder | `#FFE8E6DC` | 细线 |
| BorderStrong | `#FFC9C6B8` | 输入框焦点外的较强线，非文字 |
| Selection | `#FFE4ECF5` | tag-default，选中行实色底，无左缘条 |
| SelectionInactive | `#FFEEF2F7` | tag-quiet |
| AccentSoft（筛选选中底） | `#FFE4ECF5` | |
| Accent/AccentBrand | `#FF1B365D` | 页签下划线、主按钮、监听状态 |
| AccentBrandStrong | `#FF12294A` | 主按钮按下 |
| LabelPrimary/Secondary/Tertiary | `#FF141413` / `#FF504E49` / `#FF6B6A64` | near-black / olive / stone；stone 在 canvas 4.9:1 |
| Danger | `#FFB53333` | Kami error；Success/Warning 沿用标准值 |
| HoverOverlay/PressedOverlay | `#101B365D` / `#201B365D` | |
| ThumbnailBackdrop | `#FFEFEDE4` | |
| ScrollThumb/Hover | `#FFC9C6B8` / `#FF9A978B` | |
| Tooltip | `#FF141413` | |
| RadiusSmall/Medium/Large | 3 / 5 / 8 | 对应 radius-tag / block / screen |
| PopoverShadow | 不透明度降到 0.08 | 仅弹层保留极轻阴影 |

选中行内元信息用 LabelSecondary（olive，在 `#E4ECF5` 上约 7:1）。

## 字体（需要你决定）
Kami 的气质来自衬线，但 Windows 没有可靠的中文衬线（宋体小字号发虚）。建议：
- **推荐**：只把 `AppDisplayFontFamily`（标题栏、页签、空状态标题）覆盖为 `Georgia, Source Han Serif SC, Noto Serif SC, SimSun`，列表正文保持微软雅黑，保证 13px 可读。
- 不随包分发字体；用户装了思源宋体就自动生效，否则回退到宋体/Georgia。
- 全局衬线需要随包带字体（思源宋体 OFL，约 8–10 MB），本版不做。

## 夹夹
标题栏的夹夹保留（用户已定）。赤陶色夹子与油墨蓝并存，会让"唯一强调色"打折；备选：Kami 主题下默认关闭小宠物，或另画一套墨蓝夹夹帧（需新增素材）。待决定。

## 落地改动
1. 新增 `Themes/Palette.Kami.xaml` 与上表。
2. `ThemeService` 由"仅夹夹"改为按主题 id 加载覆盖层（`jiajia`→Palette.Jiajia.xaml，`kami`→Palette.Kami.xaml，standard 不加载）。
3. `AppSettingsValidator` 接受 `"kami"`；设置页下拉增加"Kami（纸）"。
4. LayoutCheck：Kami 覆盖层键须是标准调色板键的子集；三套主题各渲染五页。
5. 因为 Kami 是纯平面，不需要夹夹主题里那些"夹子/折角/偏移阴影"装饰，形状 token 取标准主题的值即可。
