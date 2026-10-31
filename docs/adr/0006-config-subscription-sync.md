# ADR 0006：局域网配置订阅同步

日期：2026-10-31　状态：客户端已实现（0.9.0），未经两台真实机器验收

## 背景

「配置」（收藏库文件夹及其文本/链接/图片/文件）需要在同一局域网的同事之间分发：一人维护一份配置，其他人只读订阅并在对方发布新版本时更新。需求来自 `docs/design/2026-10-31-config-sync/config-sync-spec.md`。

约束：Local-first，不依赖账号与云端；不得阻塞或侵入剪切板监听主链路；普通剪切板历史永不同步；与历史页「发送到局域网」（LocalSend，端口 53317）互不影响。

## 决策

1. **传输层自实现**：`TcpListener + SslStream` 上的最小 HTTP/1.1，默认端口 **53318**，不引入 Kestrel/ASP.NET 运行时。
   - 两端使用**自签 ECDSA P-256 证书**并做**双向 TLS**；不信任系统根证书，只比对证书 DER 的 SHA-256 指纹。
   - 证书公开部分存 `sync/identity.cer`，**私钥存入 Windows 凭据管理器**（`ConfigSync/IdentityKey`），不落明文文件。凭据或证书文件缺失时重新生成，已配对设备需要重新配对。
   - 服务端首次需要时才监听（惰性启动），没有端口设置项。
2. **配对**：发布方展示 6 位码，5 分钟有效、单次使用、连续错 5 次作废。订阅方发起 `/v1/pair/begin`、`/v1/pair/finish`，双方用 `HMAC-SHA256(code, role ‖ 双方指纹 ‖ 双方 nonce)` 互证（role 字节 `C`/`S` 防反射），配对码不在网络上传输。成功后各自保存对方指纹（pin），此后只接受该指纹；指纹不符返回 `PinMismatch`，未配对设备返回 `NotPaired`。
   - 已知限制：6 位码熵低，若局域网存在**主动中间人**并记录握手，可离线暴力枚举。5 次限制只约束在线猜测。威胁模型是"同一办公室局域网的好意同事"，不防主动攻击者；需要更强保证时应改用带外指纹核对或更长的码。
3. **端点**：`/v1/hello`、`/v1/pair/begin|finish`、`GET /v1/manifest`（≤ 4 MB；无 `id` 返回已发布配置索引，带 `id` 返回清单）、`GET /v1/package?id=&version=`（≤ 200 MB，仅网络接收路径；本地导入保持 20 GB）。`?id=` 是对规格的最小扩展：一台设备可发布多份配置。
4. **发现**：UDP 组播 `239.255.53.18:53318`，报文只含协议版本、设备名、发布名称与版本号、指纹前缀，不含任何内容，单报文有大小上限；解析端忽略异常报文。手动输入 `IP:端口` 作为备用。
5. **包格式复用** `PersonalConfigTransferService`：zip + `manifest.json` + `assets/…`，逐资源 SHA-256，清单数量与总大小一致性校验，条目名安全校验（拒绝绝对路径、`..`、盘符/`:`）。网络路径额外限制：单文件 ≤ 50 MB、1–200 个文件夹、≤ 10,000 条、文本 ≤ 2 MB、图片必须是 PNG/JPEG 头、链接仅 http/https、清单声明与条目一一对应。
6. **订阅为只读、物理隔离**：受管文件夹 `CollectionFolder.ManagedSubscriptionId` 非空；所有用户写入路径抛 `InvalidOperationException`，个人导出跳过，不可再发布。更新时先写快照（含当前最多 3 个）、再下载并校验、最后原子替换；任一步失败保留原版本。**版本单调**：拒收 ≤ 当前版本的包。回退后自动跳过被回退的版本（`skipped_version`），用户可手动取消跳过。
7. **风险文件**（`.exe .bat .cmd .ps1 .js .lnk .msi .dll .iso .url` 等）标记高风险：导入需要单独确认，永不自动打开；自动更新遇到含风险文件的版本时不自动应用，退回"提示后更新"。
8. **数据库 schema 7 → 8**：新增 `published_config`、`paired_peer`、`subscription`、`config_snapshot`，并为 `collection_folders` 增加 `managed_subscription_id` 与 `managed_remote_id`，`published_config` 增加 `folder_scope`（后两列由每次启动的幂等"补列"步骤保证，schema 号仍为 8）。一个包可含多个文件夹（默认发布全部个人配置），订阅方按发布方文件夹 ID 对应受管文件夹。迁移幂等，升级前生成 `clipboard-v7-before-v8-*.db`。

## Schannel 与证书密钥

Windows 的 Schannel 作为 TLS 服务端时**拒绝纯内存（Ephemeral）密钥**的证书（握手报 `0x8009030E`）。`ConfigSyncIdentity` 因此将证书经 PFX 往返并以 `X509KeyStorageFlags.DefaultKeySet` 导入，使私钥由 CNG 容器承载；不带 `PersistKeySet`，证书释放后容器随之删除。进程异常退出时可能遗留一个当前用户的临时 CNG 密钥，权威副本始终是凭据管理器中的那份。该问题由环回集成测试（真实 TLS）发现。

## 不做的事

- 不同步剪切板历史；不上传到云端；不做团队权限（团队共享仍按 ADR 0002/0003 的预留）。
- 本期不改安装包：MSI 仍为 perUser，**不写防火墙规则**。运行时在监听失败或连接超时时提示放行 TCP/UDP 53318，并提供可复制的 `netsh` 命令。"安装程序添加防火墙规则"是待办，不得宣称已完成。
- 不使用 mDNS/Bonjour，不引入第三方依赖。

## 后果

- 新增攻击面：一个 LAN 上的 TLS 监听。缓解：双向证书固定、未配对一律拒绝、配对码限次限时、所有输入有大小与格式上限、网络路径所有失败都被隔离，不影响剪切板主链路。
- 备份范围增加 `imports/sync` 的包与快照。
- 身份证书丢失会使既有配对失效（需要重新配对），这是有意的 fail-closed 行为。
