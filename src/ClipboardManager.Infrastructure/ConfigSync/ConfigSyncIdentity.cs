using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Infrastructure.ConfigSync;

/// <summary>
/// 本机共享身份：自签 ECDSA P-256 证书。证书（公开信息）存放在数据目录，私钥存入 Windows 凭据管理器；
/// 指纹是证书 DER 的 SHA-256，首次生成后保持不变。
/// </summary>
public sealed class ConfigSyncIdentity(AppPaths paths, ICredentialVault vault, ILogger<ConfigSyncIdentity> logger) : IConfigSyncIdentity
{
    private const string VaultKey = "ConfigSync/IdentityKey";
    private readonly Lazy<X509Certificate2> certificate = new(() => LoadOrCreate(paths, vault, logger), LazyThreadSafetyMode.ExecutionAndPublication);

    public string DeviceName => string.IsNullOrWhiteSpace(Environment.MachineName) ? "本机" : Environment.MachineName;

    public X509Certificate2 Certificate => certificate.Value;

    public string Fingerprint => ConfigSyncPolicy.Fingerprint(Certificate.RawData);

    private static X509Certificate2 LoadOrCreate(AppPaths paths, ICredentialVault vault, ILogger logger)
    {
        var certPath = Path.Combine(paths.Root, "sync", "identity.cer");
        var keyText = vault.ReadAsync(VaultKey).GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(keyText) && File.Exists(certPath))
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(keyText), out _);
                using var publicOnly = new X509Certificate2(File.ReadAllBytes(certPath));
                using var publicKey = publicOnly.GetECDsaPublicKey();
                if (publicKey is not null && publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
                    return WithUsablePrivateKey(publicOnly.CopyWithPrivateKey(key));
                logger.LogWarning("共享证书与凭据管理器中的私钥不匹配，将重新生成（已配对设备需要重新配对）。");
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
            {
                logger.LogWarning(ex, "读取共享证书失败，将重新生成（已配对设备需要重新配对）。");
            }
        }

        using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN=ClipboardManager-{Guid.NewGuid():N}"[..40], created, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        [
            new Oid("1.3.6.1.5.5.7.3.1"), // serverAuth
            new Oid("1.3.6.1.5.5.7.3.2")  // clientAuth
        ], false));
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));

        try
        {
            vault.WriteAsync(VaultKey, Convert.ToBase64String(created.ExportPkcs8PrivateKey())).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);
            var temp = certPath + ".tmp";
            File.WriteAllBytes(temp, selfSigned.RawData);
            File.Move(temp, certPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new ConfigSyncException(ConfigSyncError.Local,
                $"无法保存共享证书（{ex.GetType().Name}）。请检查数据目录与 Windows 凭据管理器是否可写。", ex);
        }
        return WithUsablePrivateKey(selfSigned);
    }

    // Windows 的 Schannel 作为服务端时不接受纯内存（Ephemeral）密钥（握手报 0x8009030E“没有可用的凭证”），
    // 必须由 CNG 密钥容器承载。经 PFX 往返并使用默认密钥集导入：不带 PersistKeySet，证书释放后容器随之删除；
    // 进程异常退出时可能遗留一个当前用户的临时 CNG 密钥，私钥的权威副本仍在凭据管理器。
    private static X509Certificate2 WithUsablePrivateKey(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx, string.Empty);
        return new X509Certificate2(pfx, string.Empty, X509KeyStorageFlags.DefaultKeySet);
    }
}
