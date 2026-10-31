# ADR 0005：下载页与应用内检查更新

日期：2026-10-31　状态：下载页与发布脚本已落地；客户端检查更新待实现

## 背景

安装包要发给同事使用。仓库 `MengPng/ClipboardManager` 的 GitHub Releases 需要登录/在国内下载慢，不适合作为同事的下载入口和客户端的更新源。已有服务器 `clipboard.netkaize.com`（nginx → Cloud API）。

## 决策

在同一域名下用 nginx 直接托管静态目录 `/var/www/download`：

| URL | 内容 | 缓存 |
|---|---|---|
| `/download/` | 产品介绍与下载页（`site/download/index.html`） | no-cache |
| `/download/latest.json` | 最新版本清单（客户端与页面共用） | no-cache |
| `/download/releases.json` | 全部版本，最新在前 | no-cache |
| `/download/files/<version>/<file>` | 安装包，发布后不再修改 | 1 年 immutable |

发布：正式版本用 `scripts/release-all.ps1` 一条命令完成构建、GitHub（推送、标签、Release）与下载服务器同步，两处使用同一批产物；`.github/workflows/release.yml` 改为仅手动触发。下载服务器部分由 `scripts/publish-download.ps1`（PowerShell 7）完成。校验 `SHA256SUMS.txt` → 生成清单 → 依次上传安装包、页面、`releases.json`，最后用 `mv` 原子切换 `latest.json`。更新说明优先取 `release/notes/<version>.md`（面向用户），否则取 CHANGELOG 对应小节。默认只发布 EXE 与便携 ZIP，`-IncludeMsi` 才带 MSI（与 README「MSI 暂不进入正式发布」一致）。

## latest.json 契约（schema 1）

```json
{
  "schema": 1,
  "product": "ClipboardManager",
  "pageUrl": "https://clipboard.netkaize.com/download/",
  "version": "0.8.7",
  "releasedAt": "2026-10-31T09:30:00Z",
  "title": "Kami 主题与选中态统一",
  "notes": ["……"],
  "files": [
    { "kind": "exe", "name": "ClipboardManager-0.8.7-win-x64.exe",
      "url": "https://clipboard.netkaize.com/download/files/0.8.7/ClipboardManager-0.8.7-win-x64.exe",
      "size": 112112280, "sha256": "<小写 hex>" }
  ]
}
```

- `kind` ∈ `exe` / `zip` / `msi`；`version` 为 `MAJOR.MINOR.PATCH`。
- 新增字段只能追加；破坏性变更必须提升 `schema`，客户端遇到不认识的 `schema` 视为"无更新"。

## 客户端检查更新（待实现，要求）

1. **检查**：启动后延迟约 30 秒、之后每 24 小时、以及「设置 → 检查更新」手动触发，GET `latest.json`，超时 10 秒。失败只写日志，不弹窗、不影响剪切板主链路（本地优先）。
2. **比较**：`System.Version.Parse(manifest.version)` 与当前程序集版本（`VersionPrefix`）比较，更大才提示。允许"跳过此版本"，记入设置。
3. **提示**：非模态提示（页脚/托盘气泡），显示 `title` 与 `notes`，按钮「更新」「打开下载页」「跳过」。
4. **下载**：仅取 `kind = exe`，下载到 `%LOCALAPPDATA%\ClipboardManager\updates\<version>\`，下载后核对 `size` 与 `sha256`，不一致即删除并报错。
5. **替换（单文件 EXE）**：
   - 当前进程把自身 EXE 重命名为 `*.old`（Windows 允许重命名正在运行的 EXE），把新 EXE 复制到原路径；
   - 以 `--wait-pid <当前PID>` 启动新 EXE 后退出；新进程先等待旧 PID 退出再获取单实例互斥体（否则会被当作第二实例唤醒旧窗口后直接退出）；
   - 新进程启动成功后删除 `*.old`；若复制失败，把 `*.old` 改回原名。
6. **MSI 安装**（`HKCU\Software\ClipboardManager\Installed = 1`）：不就地替换，改为打开 `pageUrl`，避免与 Windows Installer 记录不一致。
7. **数据**：用户数据在 `%LOCALAPPDATA%\ClipboardManager`，不随 EXE 替换；schema 迁移前沿用现有的升级前备份。
8. **测试**：版本比较、未知 schema、哈希不符、下载中断、`--wait-pid` 时序、`.old` 清理与回滚。

## 已知限制

- 产物未签名：首次运行与每次更新后都可能出现 SmartScreen 提示。
- 清单与安装包同源同服务器，未做独立签名；能改服务器文件的人即可替换安装包。需要更强保证时再给清单加签名（另行评估）。
- 轻量服务器带宽有限，同事集中下载时较慢；量大时可把 `files/` 改放 COS/CDN，清单里的 `url` 指向新地址即可，客户端无需改动。
