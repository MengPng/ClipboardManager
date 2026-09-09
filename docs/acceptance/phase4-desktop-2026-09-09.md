# Phase 4 桌面实机验收记录（2026-09-09）

## 结论

本次在 Windows 实机上确认 `ClipboardManager 0.4.0` 的文本恢复错误 `0x800401D0` 存在无效 Win32 兜底，并形成文本恢复修复。后续针对“只复制、不填入目标输入框”形成 `0.4.3`：补齐 x64 `INPUT` 原生结构尺寸、避免把焦点从编辑控件切到顶层窗口、锁定呼出前目标，并附加当前前台线程后恢复目标。独立聚焦输入框端到端连续 3 轮确认文本实际填入。

当前桌面控制后端未配置，无法自动枚举 WPF 窗口；本机仅有一个 1920×1080、96 DPI 显示器。因此收藏编辑/跨目录拖动、输入框快捷键保护、失焦隐藏、真实目标应用自动粘贴、多显示器及非 100% DPI 仍未完成可观察的鼠标/焦点实测，不得标记为通过。

## 环境与安装证据

- 操作系统环境：Windows，`DISPLAY1`，工作区 1920×1032，`AppliedDPI=96`。
- 升级后程序：`C:\Users\666\AppData\Local\ClipboardManager.App\ClipboardManager.exe`。
- 程序版本：`0.4.3-phase4`。
- 最终安装 EXE 与 MSI 发布源 SHA-256 一致：`D6FE33E3A1FA8434AAFCFBA98DCEA92CE1872B7864025F95486793559BD6E4EA`。
- MSI：`installer/bin/x64/Release/ClipboardManager-0.4.3-win-x64.msi`，SHA-256 `30470A823B7ED5277CB52C831379494B2D08C4171A76371B7A77803911A5D845`。
- 升级日志：`artifacts/msi-0.4.3-upgrade.log`，Windows Installer 返回 0。
- 升级前后 `clipboard.db`、`device.json`、`settings.json` 的大小和 SHA-256 完全一致；证据为 `artifacts/phase4-userdata-before-0.4.1.json` 与 `artifacts/phase4-userdata-after-0.4.1.json`。
- 安装后进程来自上述安装目录且 `Responding=True`。

## 验收矩阵

| 项目 | 结果 | 可复查证据 | 边界 |
|---|---|---|---|
| 文本恢复 | 通过 | `ClipboardManager.DesktopProbe`: `TEXT_RESTORE=PASS` | 真实 Unicode、中文、Emoji、多行写入与读回 |
| 图片恢复 | 通过 | `IMAGE_RESTORE=PASS`；探针目录保留 `probe.png` | 真实 2×2 PNG 写入与像素尺寸读回 |
| 文件列表恢复 | 通过 | `FILES_RESTORE=PASS`；探针目录保留两个测试文件 | 真实双文件列表、中文路径写入与顺序读回 |
| `0x800401D0` Win32 兜底 | 通过 | `WIN32_FALLBACK_WITH_OWNER=PASS` | 直接执行修复后的私有兜底并读回；不声称制造了完全相同的第三方占锁时序 |
| 自动粘贴到输入框 | 通过（受目标应用策略限制） | 连续 3 轮 `AUTO_PASTE_SEND_RESULT=True`、`AUTO_PASTE_TO_FOCUSED_TEXTBOX=PASS` | 独立 WPF 聚焦输入框实际收到文本；高权限或主动阻止模拟输入的第三方应用仍可能回退 |
| 收藏编辑与跨目录移动 | 未完成桌面验收 | `CollectionServiceTests`、`SqliteCollectionRepositoryTests` 自动化通过 | 服务/仓储规则不是 WPF 拖放与对话框实测的替代品 |
| 输入框快捷键保护 | 未完成桌面验收 | 代码在焦点为 `TextBoxBase`/`PasswordBox` 时跳过 Delete/Enter/Ctrl+D/Ctrl+P | 仍需在搜索框和收藏编辑框逐键操作并观察记录未被改变 |
| 失焦隐藏 | 未完成桌面验收 | 代码含 80 ms 延迟、常驻和模态窗口保护 | 当前桌面控制后端不能枚举 WPF 窗口，未保留可观察证据 |
| 多显示器/不同 DPI | 未验证 | 本机枚举只有 `DISPLAY1`、96 DPI | 缺少第二显示器和非 100% 缩放环境 |

## 缺陷与最小修复

1. `WindowsSystemClipboard` 的 Win32 兜底曾调用 `OpenClipboard(IntPtr.Zero)`。修复为使用 WPF 主窗口句柄作为 clipboard owner，避免 `EmptyClipboard` 后 owner 为空导致 `SetClipboardData` 失败。
2. `AutomaticPasteService` 曾忽略前台切换和 `SendInput` 的失败并继续返回成功。修复为：拒绝把本进程窗口记作目标；目标未成为真实前台时返回失败；只有四个键盘输入全部发送才返回成功。失败路径不改动已写回的剪切板内容。
3. 同版本 `0.4.0` MSI 修复安装返回 0 但未替换同文件版本 EXE；后续修复均提升补丁版本并执行 MajorUpgrade，避免把安装返回码误当作文件已替换的证据。
4. 自动粘贴的 `INPUT` 联合体缺少鼠标成员，导致 x64 下 `Marshal.SizeOf<INPUT>()` 为 32 而系统要求 40，`SendInput` 因此返回 0/4。补齐完整联合体后恢复为 4/4，并验证输入框正文变化。

## 自动化验证

- `dotnet test ClipboardManager.sln -c Release --no-restore --nologo`：51/51 通过（Domain 7、Application 14、Infrastructure 30）。
- `ClipboardManager.DesktopProbe.exe` 连续运行 10 次：每次文本、图片、文件列表、Win32 owner 写入、自动粘贴失败保持剪切板及原剪切板恢复全部通过。
