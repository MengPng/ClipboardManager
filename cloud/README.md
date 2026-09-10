# ClipboardManager Cloud

本目录保存 Phase 5 腾讯云服务端的可部署契约，不包含任何账号、密钥、连接串或真实环境标识。

推荐拓扑：Windows 客户端 → HTTPS ASP.NET Core API → TencentDB for PostgreSQL；API 通过服务角色访问私有 COS 并签发最小权限、短有效期的上传下载凭据。

## 环境变量

- `ConnectionStrings__CloudDatabase`：PostgreSQL TLS 连接串。
- `CloudAuth__SigningKey`：至少 32 字节，来自 Secret Manager/部署环境。
- `TencentCos__Region`、`TencentCos__Bucket`：私有 COS 定位信息。
- `TencentCos__RoleArn`：供 API 换取 STS 临时凭据的角色。
- `FeatureFlags__CloudEnabled`：紧急关闭云端能力，不影响客户端本地模式。

## 部署门禁

1. 执行 `migrations/001_initial.sql`，再以非表所有者的运行账号连接。
2. API 只允许 HTTPS；数据库和 COS 不公开。
3. 运行 RLS 越权、邀请码并发消费、幂等重放、配额并发扣减和附件隔离测试。
4. 两台 Windows 设备完成离线积压、冲突副本、撤销设备与全量校准验收后，才可发布正式 `0.7.0`。

当前状态：客户端契约、凭据保险库、本地镜像表和服务端核心已落地；资源部署、附件链路和双机端到端验收尚未完成，因此暂不发布正式版本。
