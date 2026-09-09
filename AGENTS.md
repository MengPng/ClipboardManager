# AGENTS.md

## 项目目标

本项目建设一款 Windows 本地剪切板管理工具。首期聚焦稳定、快速、隐私友好的本地剪切板历史能力，同时为账号系统、个人多设备云同步和团队共享剪切板保留正式扩展接口。

产品采用 Local-first（本地优先）原则：

- 未登录、离线或云端不可用时，本地剪切板功能必须完整可用。
- 捕获、搜索、收藏、置顶和重新使用剪切板内容优先在本机完成。
- 新捕获内容默认进入本机空间，不得自动进入团队空间。
- 云同步与团队共享是独立能力，不得侵入或阻塞剪切板监听主链路。
- 团队分享应创建共享副本，不得直接移动或改变原始本地记录的归属。

## 当前范围

### 首期实现

- Windows 剪切板更新监听。
- 文本、图片和文件列表历史。
- 全局快捷键呼出和隐藏窗口。
- 搜索、类型筛选、收藏、置顶和删除。
- 将历史内容重新写入系统剪切板。
- 可选的自动粘贴。
- 托盘运行、单实例、开机启动和本地设置。
- SQLite 持久化、图片缓存、自动清理。
- 暂停监听、应用排除和本地数据清除。

### 只预留接口，首期不实现

- 真实账号注册和登录。
- 云端服务、跨设备同步和附件上传。
- 团队创建、成员管理、权限校验和团队内容分发。
- OCR、AI 处理、移动端和 macOS/Linux 客户端。

不得为了展示进度而提供不可用的假入口。未实现的账号、同步和团队界面默认隐藏。

## 当前实施阶段：Phase 2 文本剪切板 MVP

本阶段必须交付可日常使用的本地文本历史闭环：

- 使用 `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE` 监听 Unicode 文本。
- Win32 消息钩子只采集轻量上下文并投递有界队列，不直接访问数据库。
- 剪切板被占用时按短间隔有限重试；单次失败不得终止监听循环。
- 原始正文原样保存；预览折叠空白；哈希仅统一 CRLF/CR 为 LF。
- 空白文本和超过 `MaxTextBytes` 的文本不落盘。
- 同一 Workspace 内相同文本按哈希事务合并，更新捕获时间和来源，保留收藏、置顶状态。
- 本工具写回剪切板时使用“预期哈希一次性标记”防循环，不得按固定时间盲目忽略所有事件。
- 历史查询、搜索、收藏、置顶、使用计数和软删除只能通过 Application 服务及仓储完成。
- `Enter` 将选中正文写回剪切板并隐藏窗口；自动模拟粘贴不作为本阶段完成条件。
- 提供暂停/继续监听、来源进程排除、保留天数和最大条数清理。
- 本阶段只启用全部、置顶、文本、收藏筛选；图片和文件入口保持隐藏。

Phase 2 验收至少覆盖：文本规范化哈希、重复合并、Workspace 隔离、软删除、状态保持、清理保护、自身写回标记，以及 Windows 进程启动和基础剪切板捕获链路。

## 当前实施阶段：Phase 3 收藏库、图片与文件

本阶段将 `is_favorite` 快捷状态升级为独立收藏库，同时扩展非文本剪切板格式：

- 收藏库使用独立的 `collection_folders`、`collection_items` 和附件关系，不依赖历史记录生命周期。
- Local Workspace 初始化时必须创建唯一的“未分类”系统文件夹；系统文件夹不可删除。
- 当前 UI 支持一级文件夹；数据模型预留 `parent_folder_id`，但本阶段不开放无限嵌套。
- 文件夹和文件夹内混合内容均持久化 `sort_key`；拖动排序只改动必要记录，并作为未来同步字段。
- 收藏项支持 `Text`、`Link`、`Image`、`File`，同一文件夹可以混排。
- 从历史收藏时创建内容快照；删除或清理历史不得使收藏失效。
- 支持手动添加、编辑文本与链接；链接不依赖联网抓取元数据。
- 图片收藏复制到应用管理的 blobs 目录并生成缩略图；外部原文件删除后仍可用。
- 文件收藏默认保存路径引用，只有明确选择时才允许托管文件副本。
- 图片与文件列表加入 Windows 剪切板捕获和恢复；捕获队列仍不得阻塞消息线程。
- 历史与收藏视图分离；收藏视图支持新建、重命名、删除文件夹，移动内容和拖动排序。
- 删除非空文件夹默认将内容移动到“未分类”；彻底删除必须是明确操作。
- 所有文件夹、收藏项、历史及附件操作必须限定 Workspace，保留软删除、版本与 Local-only 同步状态。

Phase 3 验收至少覆盖：未分类唯一性、收藏快照独立性、文件夹/内容排序持久化、跨 Workspace 隔离、非空文件夹安全删除、图片托管与缩略图、文件路径失效，以及文本/链接/图片/文件复制恢复。

## 当前实施阶段：Phase 4 桌面体验、隐私与交付

Phase 2/3 上述条款作为历史基线保留；当前功能范围以 Phase 4 为准，不再隐藏图片和文件入口。本阶段不接入真实账号、云端或团队。

实施顺序及约束：

1. **4A 收尾**：补齐收藏文本/链接编辑、跨文件夹移动、必要记录排序、图片另存为和文件位置操作；审查 Workspace 写入隔离、系统目录保护、收藏附件引用和异常处理。
2. **4B 桌面交互**：集中快捷键定义，输入框编辑不得触发记录删除/使用；支持当前显示器工作区定位、不同 DPI、失焦隐藏及快捷键冲突提示。
3. **4C 自动粘贴**：默认关闭；保留调用前目标窗口，写回成功后恢复目标并尝试粘贴。失败保留剪切板，提示手动粘贴，不声称目标应用已接收。呼出面板前取得的目标窗口必须锁定到本次使用动作结束，不能被面板显示后的 Shell、通知或剪切板事件覆盖。
4. **4D 隐私**：暂停与应用排除在读取载荷之前生效；支持定时暂停。普通历史清理不得删除收藏或其托管附件，危险操作先展示影响并确认。
5. **4E 设置**：正式设置入口，配置校验、原子保存，覆盖通用、快捷键、捕获、隐私与存储；未实现设置不得提供假开关。
6. **4F 存储**：维护引用完整性，清理仅针对无引用托管文件；备份包含一致性数据库、托管资源、设置和版本；恢复先验证再备份当前数据，失败可恢复。不得删除用户原始路径文件。
7. **4G 可靠性**：搜索取消/版本隔离、虚拟化、缩略图资源释放、有限重试及单项错误隔离；不记录剪切板正文。
8. **4H 发布**：Windows x64 自包含发布；评估 MSIX 与现有托盘/启动要求，必要变更记录 ADR；版本升级迁移前备份，保留用户数据及设备 ID。安装、升级、卸载与签名状态分别验收。

验收：文本/图片/多文件真实读写、收藏编辑/跨目录持久化、跨 Workspace 写入拒绝、清理保护、输入焦点快捷键、粘贴失败回退、设置重启保留、备份恢复、损坏资源异常隔离，以及安装升级。多显示器/DPI、长期运行和真实系统剪切板未验证时必须列为待验收；不得由单元测试代替或提前勾选整个阶段完成。

## 技术基线

除非经过明确的架构决策修改，使用以下技术：

- .NET 8。
- WPF 桌面界面。
- Windows 原生 Clipboard/Win32 API。
- SQLite 本地数据库。
- Microsoft.Extensions.DependencyInjection 负责依赖注入。
- Microsoft.Extensions.Logging 负责结构化日志。
- 数据库变更必须采用可追踪的迁移机制。

优先使用成熟、轻量、可维护的依赖。引入第三方库前，应确认其必要性、许可证、维护状态和是否显著降低实现复杂度。

## 建议的解决方案结构

实际创建项目时，优先采用职责清晰的分层结构：

```text
src/
  ClipboardManager.Domain/
  ClipboardManager.Application/
  ClipboardManager.Infrastructure/
  ClipboardManager.Windows/
tests/
  ClipboardManager.Domain.Tests/
  ClipboardManager.Application.Tests/
  ClipboardManager.Infrastructure.Tests/
```

职责边界：

- `Domain`：实体、值对象、枚举、领域规则和抽象契约；不依赖 WPF、SQLite 或网络 SDK。
- `Application`：用例、查询、命令、同步编排和业务服务；依赖领域抽象，不依赖具体基础设施。
- `Infrastructure`：SQLite、文件缓存、仓储实现、设置存储，以及未来的远端 Provider 实现。
- `Windows`：WPF、托盘、Win32 消息、全局快捷键、窗口定位和 Windows 剪切板格式转换。

如果在初期为了交付速度减少项目数量，仍须保持命名空间和依赖方向等价，避免 UI、SQLite 和剪切板监听逻辑混杂。

## 依赖方向

只允许以下主要依赖方向：

```text
Windows -> Application -> Domain
Infrastructure -> Application/Domain
```

组合根可以同时引用各层用于依赖注入。禁止：

- UI 直接执行 SQL。
- ViewModel 直接创建数据库连接。
- Domain 引用 WPF、SQLite、HTTP 或具体认证 SDK。
- 剪切板监听器直接上传远端数据。
- 云同步失败阻塞本地写入或剪切板消息处理。

## 核心领域规则

### 标识

- 可同步实体使用 UUID，不使用仅在单机有效的自增 ID 作为业务主键。
- 优先使用 UUID v7；如果目标框架或存储层不便使用，必须保证跨设备唯一且保留统一生成接口。
- 每次安装生成稳定的 `device_id`，不得在每次启动时重新生成。
- 每条剪切板记录必须具有明确的 `workspace_id` 和 `device_id`。

### Workspace

统一使用 Workspace 表达数据归属：

- `Local`：本机空间，首期默认且必须可用。
- `Personal`：未来个人云空间。
- `Team`：未来团队共享空间。

业务查询必须显式带 Workspace 上下文，避免未来接入团队空间时发生跨空间数据泄漏。

### 捕获与去重

- Clipboard 更新处理必须快速返回；数据库、图片编码等耗时工作不得长时间占用窗口消息线程。
- 剪切板被占用时进行有上限的短暂重试，禁止无限循环。
- 使用内容哈希、类型、来源和时间窗口识别重复事件。
- 相同内容再次复制时，可更新其时间与排序，但必须保留收藏、置顶等用户状态。
- 工具自身写回剪切板时必须避免产生重复捕获循环。
- Windows 文本写回使用 Win32 路径时，`OpenClipboard` 必须传入有效应用窗口句柄作为 owner；不得使用 `IntPtr.Zero` 后调用 `EmptyClipboard`/`SetClipboardData`。剪切板竞争应有限重试，并保留 `0x800401D0` 等原始错误证据。

### 删除

- 可同步实体采用软删除，至少保留 `deleted_at` 或等价墓碑信息。
- 用户界面删除后应立即隐藏记录。
- 物理清理必须由独立清理流程执行，并考虑附件、收藏状态和未来同步确认。
- 清空普通历史默认不得删除收藏内容，除非用户明确选择清空全部数据。

### 团队分享

- 本机捕获内容不得默认上传或共享。
- 分享到团队必须是显式用户操作。
- 分享应创建独立团队记录，并保存 `source_item_id`、`shared_by`、`shared_at` 等来源信息。
- 删除、清理或修改本地原记录不得静默破坏已发布的团队副本。
- 团队权限最终由服务端验证，客户端隐藏按钮不能代替授权。

## 扩展契约

首期需要定义稳定边界，但不要实现虚假的远端行为。接口名称可根据代码规范调整，职责不得被省略。

### 仓储

```csharp
public interface IClipboardItemRepository
{
    Task<ClipboardItem?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClipboardItem>> SearchAsync(
        ClipboardQuery query,
        CancellationToken cancellationToken);
    Task UpsertAsync(ClipboardItem item, CancellationToken cancellationToken);
    Task SoftDeleteAsync(Guid workspaceId, Guid id, DateTimeOffset deletedAt, CancellationToken cancellationToken);
}
```

### 身份

```csharp
public interface IIdentityProvider
{
    Task<IdentitySession?> GetSessionAsync(CancellationToken cancellationToken);
    Task<IdentitySession> SignInAsync(SignInRequest request, CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);
}
```

首期仅提供本地匿名身份实现。令牌等秘密未来应存入 Windows Credential Manager 或使用 DPAPI 保护，不得以明文写入 SQLite、日志或普通配置文件。

### 同步

```csharp
public interface ISyncEngine
{
    SyncStatus GetStatus(Guid workspaceId);
    Task<SyncResult> SynchronizeAsync(
        Guid workspaceId,
        SyncReason reason,
        CancellationToken cancellationToken);
}

public interface IRemoteSyncProvider
{
    Task<PushResult> PushAsync(
        IReadOnlyList<SyncOperation> operations,
        CancellationToken cancellationToken);
    Task<PullResult> PullAsync(
        string? cursor,
        CancellationToken cancellationToken);
}
```

同步采用增量模型，预留 Outbox、远端游标、版本、重试和冲突状态。首期可以使用 `LocalOnlySyncEngine`，明确返回“仅本地”，不得假装同步成功。

### Workspace

```csharp
public interface IWorkspaceService
{
    Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken cancellationToken);
    Task<Workspace> GetCurrentAsync(CancellationToken cancellationToken);
    Task SwitchAsync(Guid workspaceId, CancellationToken cancellationToken);
}
```

### 附件

```csharp
public interface IBlobStorageProvider
{
    Task<UploadTicket> CreateUploadAsync(
        BlobDescriptor descriptor,
        CancellationToken cancellationToken);
    Task CompleteUploadAsync(string uploadId, CancellationToken cancellationToken);
    Task<Stream> DownloadAsync(string blobId, CancellationToken cancellationToken);
    Task DeleteAsync(string blobId, CancellationToken cancellationToken);
}
```

本地图片路径、缩略图路径与未来远端 `blob_id` 必须分离。复制的文件默认只记录路径；未得到用户明确操作时不得自动上传文件副本。

## 数据模型最低要求

`clipboard_items` 或等价实体至少覆盖：

```text
id
workspace_id
owner_id (nullable)
device_id
item_type
content_hash
text_content / preview_text
source_app / source_process
created_at / updated_at / captured_at / last_used_at
deleted_at (nullable)
is_favorite / is_pinned / use_count
visibility
sync_status
local_version / server_version (nullable)
file_manifest
image_path / thumbnail_path
content_size
```

同时预留或建立：

- `identities`
- `devices`
- `workspaces`
- `workspace_members`
- `sync_operations`
- `sync_state`
- 附件元数据表

字段可以规范化到子表，但不得丢失数据归属、版本、软删除和附件远端标识等能力。

## UI 与交互规则

主面板基本结构：

```text
标题与窗口操作
搜索框
类型/状态筛选
虚拟化记录列表
状态与快捷操作栏
```

第一版筛选项：全部、置顶、文本、图片、文件、收藏。

默认快捷键建议：

- `Alt+V`：显示或隐藏面板。
- `Up/Down`：切换记录。
- `Enter`：写回剪切板。
- `Ctrl+Enter`：写回并尝试自动粘贴。
- `Ctrl+F`：聚焦搜索。
- `Delete`：删除。
- `Ctrl+D`：收藏。
- `Ctrl+P`：置顶。
- `Esc`：清空搜索或关闭面板。

所有快捷键应支持配置或集中定义，不得散落为难以修改的魔法常量。

自动粘贴必须遵守以下 Win32 交互契约：

- `INPUT`/联合体必须完整匹配目标架构的原生布局；Windows x64 下 `Marshal.SizeOf<INPUT>()` 必须为 40，禁止仅声明 `KEYBDINPUT` 导致 32 字节错误布局。
- 恢复目标窗口时不得对顶层窗口调用 `SetFocus`，以免覆盖对话框原有输入控件焦点。
- 不以 `SetForegroundWindow` 的单次返回值判定成功；调用后以 `GetForegroundWindow` 核对实际结果。
- 必要时短暂附加当前线程、目标线程与当前前台线程的输入队列，且必须在 `finally` 中解除。
- 只有 `SendInput` 返回值等于请求事件总数时才能报告按键已发送；不得用无法验证结果的 `keybd_event` 兜底后声称成功。
- 不同完整性级别受 Windows UIPI 限制。普通权限进程不能承诺向管理员权限目标自动粘贴，失败时必须保留剪切板并提示手动操作。

界面需考虑：

- 多显示器与不同 DPI。
- 长文本、空数据、损坏图片和失效文件。
- 窗口失焦自动隐藏。
- 键盘完整操作。
- 大量记录使用列表虚拟化。
- 图片列表只加载缩略图，离开视口后释放资源。

## 隐私与安全

- 默认数据只保存在本机。
- 提供暂停监听、应用排除、退出清理和一键清空能力。
- 优先通过来源应用排除密码管理器，不对内容进行不可靠的“密码猜测”。
- 日志不得包含完整剪切板正文、图片字节、访问令牌或文件内容。
- 错误日志可以记录实体 ID、格式、大小、来源进程和异常类型，但应避免敏感载荷。
- 团队内容、成员资格和权限不能只相信客户端状态。
- 引入云端后，传输必须使用 HTTPS，并支持会话和设备撤销。

## 性能与可靠性

- UI 线程不得执行长时间数据库、文件或网络操作。
- 剪切板读取、图片编码、数据库写入和清理应支持取消与异常隔离。
- 图片尽快编码落盘，列表使用缩略图，禁止长期持有大量原始位图。
- 文件历史保存路径而非默认复制文件本体；文件不存在时显示失效状态，不应导致崩溃。
- SQLite 需要合理索引，至少覆盖 Workspace、删除状态、时间、类型、收藏和置顶查询。
- 应用必须单实例运行，第二实例负责唤醒已有窗口。
- 自动粘贴失败时保留已写回的剪切板内容，并提示用户手动粘贴。
- 自动粘贴的成功验收必须读取目标输入控件的最终文本；仅验证剪切板内容、窗口句柄、API 返回值或已发送按键均不算端到端完成。

## 开发流程

1. 在改动前阅读本文件和相关代码。
2. 明确本次工作属于本地功能、扩展契约还是未来云端实现。
3. 先做最小必要设计，再开始修改。
4. 保持依赖方向和 Workspace 数据隔离。
5. 实现后运行与改动范围相称的构建和测试。
6. 报告已验证内容、未验证内容和剩余风险。

新增较大功能时，应同步更新 `README.md` 的路线图或功能状态。任何实质性架构变化都应写入 ADR 或等价决策记录；不要只留在聊天或代码注释中。

## 测试要求

优先覆盖高风险规则：

- 内容哈希与重复合并。
- 自身写回防循环。
- Workspace 隔离。
- 收藏、置顶与软删除状态保持。
- 普通清理不删除收藏。
- 团队分享创建副本而非移动原记录。
- 同步状态机与冲突策略（开始实现同步后）。
- 文件失效和图片缓存清理。

桌面集成测试至少验证：

- 全局快捷键注册与冲突处理。
- 文本、图片、文件列表读写。
- 剪切板被占用时的有限重试。
- 多显示器/DPI 下的窗口定位。
- 自动粘贴失败回退。
- 自动粘贴到独立聚焦输入框，断言正文实际出现；Windows x64 同时断言 `INPUT` 原生尺寸及 `SendInput` 完整事件数。测试结束恢复进入测试前的系统剪切板。

不得通过降低断言、跳过失败测试或吞掉异常来让验证通过。

## 完成标准

一个功能只有同时满足以下条件才算完成：

- 行为符合当前范围和领域规则。
- 没有破坏未登录、离线的本地可用性。
- 没有绕过 Workspace、仓储或扩展契约。
- 必要的异常、空状态和资源释放已处理。
- 相称的构建、测试或手动检查已经通过。
- README 或相关设计文档已在必要时更新。

如果无法运行验证，只能报告“已实现、未验证”，不能声称完整完成。
