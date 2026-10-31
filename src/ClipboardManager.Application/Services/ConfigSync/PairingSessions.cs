using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services.ConfigSync;

/// <summary>
/// 发布方的配对会话：同一时刻只有一个 6 位配对码，5 分钟有效、成功即作废、输错 5 次作废。
/// 配对码本身不上网：订阅方与发布方各自用它对“双方证书指纹 + 双方随机数”做 HMAC-SHA256 互证。
/// </summary>
public sealed class PairingSessions(TimeProvider time)
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(60);
    private const int MaxPending = 16;

    private readonly object sync = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.OrdinalIgnoreCase);
    private string? code;
    private DateTimeOffset expiresAt;
    private int failures;

    /// <summary>当前可用的配对码；过期、已用或已作废时为 null。</summary>
    public PairingCodeInfo? Current
    {
        get
        {
            lock (sync)
            {
                ExpireIfNeeded();
                return code is null ? null : new PairingCodeInfo(code, expiresAt);
            }
        }
    }

    public PairingCodeInfo Issue()
    {
        lock (sync)
        {
            code = PairingProtocol.GenerateCode();
            expiresAt = time.GetUtcNow() + ConfigSyncPolicy.PairingCodeLifetime;
            failures = 0;
            pending.Clear();
            return new PairingCodeInfo(code, expiresAt);
        }
    }

    public void Cancel()
    {
        lock (sync)
        {
            code = null;
            pending.Clear();
        }
    }

    public byte[] Begin(string clientFingerprint, string clientName, byte[] clientNonce)
    {
        lock (sync)
        {
            ExpireIfNeeded();
            if (code is null)
                throw new PairingException(ConfigSyncError.PairingCodeExpired,
                    "发布方当前没有可用的配对码（未生成、已过期或已作废）。请在发布方点击“生成配对码”。");
            var now = time.GetUtcNow();
            foreach (var stale in pending.Where(pair => now - pair.Value.StartedAt > PendingLifetime).Select(pair => pair.Key).ToList())
                pending.Remove(stale);
            if (pending.Count >= MaxPending && !pending.ContainsKey(clientFingerprint))
                throw new PairingException(ConfigSyncError.PairingCodeInvalid, "发布方正在处理其他配对请求，请稍后重试。");
            var serverNonce = PairingProtocol.NewNonce();
            pending[clientFingerprint] = new Pending(clientName, clientNonce, serverNonce, now);
            return serverNonce;
        }
    }

    /// <summary>校验订阅方证明并返回发布方证明；成功后配对码立即作废。</summary>
    public (byte[] ServerProof, string ClientName) Finish(string clientFingerprint, string serverFingerprint, byte[] clientProof)
    {
        lock (sync)
        {
            ExpireIfNeeded();
            if (!pending.Remove(clientFingerprint, out var session) || time.GetUtcNow() - session.StartedAt > PendingLifetime)
                throw new PairingException(ConfigSyncError.PairingCodeInvalid, "配对流程不完整或已超时，请重新开始配对。", RemainingAttempts());
            if (code is null)
                throw new PairingException(ConfigSyncError.PairingCodeExpired, "配对码已过期或已作废，请在发布方重新生成。");

            var expected = PairingProtocol.ComputeProof(code, 'C', clientFingerprint, serverFingerprint, session.ClientNonce, session.ServerNonce);
            if (!PairingProtocol.VerifyProof(expected, clientProof))
            {
                failures++;
                var remaining = ConfigSyncPolicy.MaxPairingFailures - failures;
                if (remaining <= 0)
                {
                    code = null;
                    pending.Clear();
                    throw new PairingException(ConfigSyncError.PairingRevoked,
                        "配对码连续输错 5 次，已作废。请在发布方重新生成配对码。");
                }
                throw new PairingException(ConfigSyncError.PairingCodeInvalid, $"配对码不正确，还可以尝试 {remaining} 次。", remaining);
            }

            var serverProof = PairingProtocol.ComputeProof(code, 'S', clientFingerprint, serverFingerprint, session.ClientNonce, session.ServerNonce);
            code = null;
            pending.Clear();
            return (serverProof, session.ClientName);
        }
    }

    private int RemainingAttempts() => Math.Max(0, ConfigSyncPolicy.MaxPairingFailures - failures);

    private void ExpireIfNeeded()
    {
        if (code is not null && time.GetUtcNow() >= expiresAt)
        {
            code = null;
            pending.Clear();
        }
    }

    private sealed record Pending(string ClientName, byte[] ClientNonce, byte[] ServerNonce, DateTimeOffset StartedAt);
}
