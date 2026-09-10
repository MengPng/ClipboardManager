# 腾讯云部署凭据

`Import-TencentCloudCredential.ps1` 只把当前 Windows 用户可解密的凭据装入当前 PowerShell 进程环境，不打印值、不创建 CLI 配置文件。调用脚本必须用 `try/finally` 清除全部 `TENCENTCLOUD_*` 变量。

该长期密钥仅用于资源盘点和首次部署。生产 API 应改用绑定到实例或工作负载的 CAM 角色；Windows 客户端不得读取这个文件。

```powershell
try {
    . .\scripts\tencent-cloud\Import-TencentCloudCredential.ps1
    # invoke a checked deployment command here
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
```
