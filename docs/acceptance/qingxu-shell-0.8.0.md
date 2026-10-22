# 0.8.0 青序全局结构验收

日期：2026-10-21

## 本次范围

以 `docs/design/2026-10-21-independent-qingxu/qingxu-home-compact-340-v2.png` 为选定参考，实现 WPF 桌面全局结构第一版。没有创建网页替代客户端，也没有改变数据层、数据库 schema、Workspace 归属、剪切板捕获或自动粘贴逻辑。

- 默认 340×540 DIP，最小 340×450 DIP。
- 固定标题栏与五页等宽导航：历史、配置、待办、计算、划词。
- 保留暂停、窗口全局置顶、设置、收起及隐藏。窗口置顶继续使用原有持久化设置与失焦逻辑。
- 月白标题/底栏、瓷白工作区、黛色正文、天青选中标记；取消纸纹生成。
- 文本历史增加类型图标，收紧缩略图与预览高度，保留列表虚拟化。
- 五个页面共享主题；保留配置文件夹、导入导出、待办统计及计算、划词的既有行为。
- 版本契约改为 0.8.0，数据库保持 schema 7。

## 自动验证

- Release 构建：通过，0 错误、0 警告。
- 测试：Domain 20/20、Application 80/80、Infrastructure 68/68，共 168 项通过，无跳过。
- WPF 布局检查：5 页 × 3 种尺寸（340×540、340×450、520×700），15 组通过。
- 标题栏收起/还原测量：连续 3 轮通过。
- 追加检查：默认/最小 DIP、两字导航顺序与等宽、全局置顶可见与可聚焦、标题栏不重叠、页脚不重叠、配置搜索宽度。
- 划词页使用真实设置控件及内存设置/凭据替身，证明加载后保存按钮位于最小尺寸内容范围内；未访问用户设置或凭据。
- 单文件 EXE：`artifacts/review/0.8.0/ClipboardManager-0.8.0-win-x64.exe`，106.34 MiB。
- 独立 EXE 启动探针返回 `version=0.8.0`、`databaseSchemaVersion=7`、`runtime=win-x64`；信息保存于同目录 `release-info.json`。
- EXE SHA-256：`8036965CA1F10EBFD5222DC15EB8BBE7A4416336A4CE2F7912DE079FE574A00C`。
- 签名状态：`NotSigned`。未安装、未公开发布，未覆盖旧发行包。

可复现：

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build src/ClipboardManager.Windows -c Release -m:1 -p:NuGetAudit=false -p:UseSharedCompilation=false
dotnet test ClipboardManager.sln -c Release -m:1 -p:NuGetAudit=false -p:UseSharedCompilation=false
dotnet run --project tools/ClipboardManager.LayoutCheck -c Release -p:NuGetAudit=false -- "<项目根目录>"
```

渲染输出：`artifacts/review/0.8.0/layout/`。这些图片使用生产 XAML、模板和 WPF 渲染器，历史内容为合成样例，不能当作真实用户数据或实机点击验收。

## 与概念图的差异

- 保留原品牌图标，不在此次结构调整中更换品牌资产。
- 保留收起和隐藏按钮，避免视觉改版删掉已有能力。
- 历史来源和捕获时间保留现有格式，未新增相对时间刷新计时器。
- 页脚保留当前页面条数，并恢复当前页面的短操作提示。
- 尚未增加新的正文展开或图片/文件详情浮层，也未增加删除撤销；这些提案行为不属于本次已实现范围。
- 尚未增加开关或页面切换动画，优先保持键盘与鼠标事件语义。

## 待实机验收

发布 EXE 的真实页签 mouse-down/mouse-up、全局置顶持久化、窗口失焦、快捷键冲突、第三方应用自动粘贴、多显示器/不同 DPI、长期运行仍需实机点验。此次布局与单元测试不代替这些验收。

当前交付为独立审阅版，不代表已安装、已替换运行版本或已公开发布。
