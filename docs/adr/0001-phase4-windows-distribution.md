# ADR 0001：Phase 4 Windows 发布采用自包含文件夹与 WiX MSI

- 状态：已接受
- 日期：2026-09-08

## 背景

应用使用 WPF、Win32 全局快捷键、剪切板监听、托盘和当前用户开机启动。MSIX 对传统桌面启动注册、升级渠道和签名证书有额外约束；当前项目尚没有可信代码签名证书和发布服务。

## 决策

1. 先以 `win-x64` 自包含、非单文件的文件夹发布作为可验证交付物，不要求用户安装 .NET Runtime。
2. 安装包采用 WiX Toolset MSI 作为后续封装入口，但在安装定义和签名链实际建立前，不宣称安装包已交付。
3. 数据继续存放在 `%LOCALAPPDATA%\ClipboardManager`，不放在安装目录；普通升级不得覆盖数据库、设置、设备 ID、blobs 或 thumbnails。
4. 应用数据库迁移保持单向、可追踪。Phase 4 存储服务负责用户备份；安装程序不得自行删除数据。
5. 卸载程序只移除程序文件和启动项。是否删除用户数据必须由显式操作完成，不静默删除。

## 发布命令

```powershell
dotnet publish src/ClipboardManager.Windows/ClipboardManager.Windows.csproj -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -o artifacts/publish/win-x64
```

发布验收至少检查：离线启动、单实例、托盘、全局快捷键、SQLite 迁移、设置和设备 ID 保留。MSI 另需检查安装、覆盖升级、卸载和签名状态。

## 结果与边界

文件夹发布体积大于依赖框架发布，但减少目标机器前置条件。没有证书时生成的 MSI 也不具备可信发行条件，因此签名和安装验证保持独立待办，不能用 `dotnet publish` 成功代替。
