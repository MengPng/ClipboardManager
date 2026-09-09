# ADR 0002：团队配置暂为仅本机的第二个 Workspace

- 状态：已接受
- 日期：2026-09-09

## 背景

顶部导航从「历史 / 收藏」改为「历史 / 个人配置 / 团队配置」。用户希望现在就能把配置分成两组分别整理，但 Phase 4 明确不实现账号、云端和团队成员管理，也不应提供不可用的假入口。

同时存在一条不能破坏的规则：`AGENTS.md` 要求「新捕获内容默认进入本机空间，不得自动进入团队空间」。原实现里 `CollectionService` 与 `ClipboardCaptureService`、`ClipboardPayloadCaptureService`、`ClipboardHistoryService` 共用同一个 `IWorkspaceService.GetCurrentAsync()`。如果让界面切换「当前 Workspace」，用户选中团队配置之后新复制的内容就会落进团队空间，直接违反上述规则。

## 决策

1. 团队配置是**仅本机**的第二个 Workspace，使用已有的 `WorkspaceType.Team`，名称写明「团队配置（仅本机）」。没有账号、没有服务端、没有同步，界面上以常驻提示说明这一点。
2. `IWorkspaceService.GetCurrentAsync()` 继续返回 Local 空间，并且只服务于剪切板捕获与历史。捕获链路完全不变。
3. `GetAvailableAsync()` 返回 Local 与 Team 两个空间。两个 id 都由 `device_id` 派生，各用一个稳定且不同的盐：Local 使用 `0x4C, 0x57`，Team 使用 `0x54, 0x4D`。id 不落库、不随启动变化，因此不需要数据迁移。
4. `SwitchAsync` 只校验目标 Workspace 是否存在，不改变捕获锚点。界面的空间切换是查询上下文的切换，不是捕获归属的切换。
5. `CollectionService` 的所有方法显式接收目标 `workspaceId`，由 `MainWindow` 传入当前选中的配置空间。这同时满足 `AGENTS.md` 的「业务查询必须显式带 Workspace 上下文」。
6. 快速收藏（`Ctrl+D`）走独立的 `QuickCollectAsync`，永远写入 Local 的「未分类」，不受界面当前选中空间影响。放入团队配置只能通过「存入配置」目录选择器这一显式操作，选择器按空间分组并标注空间名。
7. 从历史生成快照时，历史记录按其**来源空间**（Local）校验，快照建在**目标空间**，并保留 `source_clipboard_item_id` 与来源 Workspace。原历史记录不被搬走或修改。

## 为什么不需要数据库迁移

`collection_folders` 和 `collection_items` 本来就带 `workspace_id`，仓储的每个查询都按 Workspace 过滤，`EnsureUncategorizedAsync(workspaceId)` 会为任意 Workspace 惰性创建唯一的「未分类」系统文件夹。已有收藏数据的 `workspace_id` 就是 Local，因此它们原样成为个人配置的内容，不需要搬迁，也不会丢失。

## 结果与边界

- 团队配置目前只是「同一台机器上的第二个整理空间」。它不提供任何共享语义，README 与界面都不得把它描述成可用的团队协作。
- 两个配置空间之间没有可见性和写入通路，靠仓储的 Workspace 过滤保证，已有跨空间隔离测试覆盖。
- 快照跨空间是被允许的**例外**，仅限「来源空间被显式指名」这一条路径；仓储仍拒绝无来源声明的跨空间引用。

## 接入真实后端时必须改什么

1. Workspace 不能再只由 `device_id` 派生。真实团队空间的 id 由服务端下发，必须落库并与成员关系绑定；届时需要一次迁移，把本机团队空间的内容要么升级为真实团队空间，要么保留为本地空间并与之区分。
2. 需要引入 `IIdentityProvider` 的真实实现、`workspace_members`、服务端权限校验。客户端隐藏按钮不能代替授权。
3. 分享必须继续是显式操作，并继续创建副本；`ISyncEngine` 从 `LocalOnlySyncEngine` 换成真实实现时，捕获链路仍然不得被同步失败阻塞。
4. 捕获锚点仍应保持在 Local。个人云空间上线后，是否把某台设备的捕获默认落到 Personal，需要另开 ADR，不在本决策范围内。
