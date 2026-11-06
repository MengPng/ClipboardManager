using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services.ConfigSync;

/// <summary>
/// 本机固定匹配码的配对会话（ADR-0008，取代 ADR-0006 的 5 分钟临时码）。
/// 匹配码长期有效、成功后不作废；连续错 5 次锁定（1、2、4…最长 60 分钟），累计错 20 次停用直到更换。
/// 匹配码本身不上网：双方各自用它对“双方证书指纹 + 双方随机数”做 HMAC-SHA256 互证。
/// </summary>
public sealed class PairingSessions(TimeProvider time)
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(60);
    private const int MaxPending = 16;

    private readonly object sync = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.OrdinalIgnoreCase);
    private string? code;
    private bool disabled;
    private int consecutiveFailures;
    private int totalFailures;
    private int lockCount;
    private DateTimeOffset? lockedUntil;

    /// <summary>累计输错达到上限、匹配码刚被停用时触发（用于持久化停用状态）。</summary>
    public event EventHandler? CodeDisabled;

    public MatchCodeStatus Status
    {
        get
        {
            lock (sync)
            {
                var now = time.GetUtcNow();
                return new MatchCodeStatus(code, disabled, lockedUntil is { } until && until > now ? until : null, totalFailures);
            }
        }
    }

    /// <summary>载入或更换匹配码；计数与锁定全部清零。</summary>
    public void SetCode(string newCode, bool isDisabled = false)
    {
        if (!PairingProtocol.IsWellFormedCode(newCode)) throw new ArgumentException("匹配码应为 6 位数字。", nameof(newCode));
        lock (sync)
        {
            code = newCode;
            disabled = isDisabled;
            consecutiveFailures = 0;
            totalFailures = 0;
            lockCount = 0;
            lockedUntil = null;
            pending.Clear();
        }
    }

    public byte[] Begin(string clientFingerprint, string clientName, byte[] clientNonce, int? clientPort = null)
    {
        lock (sync)
        {
            EnsureUsable();
            var now = time.GetUtcNow();
            foreach (var stale in pending.Where(pair => now - pair.Value.StartedAt > PendingLifetime).Select(pair => pair.Key).ToList())
                pending.Remove(stale);
            if (pending.Count >= MaxPending && !pending.ContainsKey(clientFingerprint))
                throw new PairingException(ConfigSyncError.PairingCodeInvalid, "对方正在处理其他匹配请求，请稍后重试。");
            var serverNonce = PairingProtocol.NewNonce();
            var port = clientPort is >= 1 and <= 65535 ? clientPort : null;
            pending[clientFingerprint] = new Pending(clientName, clientNonce, serverNonce, now, port);
            return serverNonce;
        }
    }

    /// <summary>校验匹配方证明并返回本机证明；匹配码保持有效。</summary>
    public PairingAcceptance Finish(string clientFingerprint, string serverFingerprint, byte[] clientProof)
    {
        lock (sync)
        {
            if (!pending.Remove(clientFingerprint, out var session) || time.GetUtcNow() - session.StartedAt > PendingLifetime)
                throw new PairingException(ConfigSyncError.PairingCodeInvalid, "匹配流程不完整或已超时，请重新开始。", RemainingBeforeLock());
            EnsureUsable();

            var expected = PairingProtocol.ComputeProof(code!, 'C', clientFingerprint, serverFingerprint, session.ClientNonce, session.ServerNonce);
            if (!PairingProtocol.VerifyProof(expected, clientProof)) throw RecordFailure();

            consecutiveFailures = 0;
            var serverProof = PairingProtocol.ComputeProof(code!, 'S', clientFingerprint, serverFingerprint, session.ClientNonce, session.ServerNonce);
            return new PairingAcceptance(serverProof, session.ClientName, session.ClientPort);
        }
    }

    private PairingException RecordFailure()
    {
        consecutiveFailures++;
        totalFailures++;
        if (totalFailures >= LanMatchPolicy.MaxTotalFailures)
        {
            disabled = true;
            pending.Clear();
            ThreadPool.QueueUserWorkItem(_ => CodeDisabled?.Invoke(this, EventArgs.Empty));
            return new PairingException(ConfigSyncError.PairingRevoked,
                "匹配码输错次数过多，对方已停用这个匹配码。请让对方在「共享 → 传输」更换匹配码后再试。");
        }
        if (consecutiveFailures >= LanMatchPolicy.FailuresPerLock)
        {
            consecutiveFailures = 0;
            lockCount++;
            var duration = LanMatchPolicy.LockDuration(lockCount);
            lockedUntil = time.GetUtcNow() + duration;
            pending.Clear();
            return new PairingException(ConfigSyncError.PairingRevoked,
                $"匹配码连续输错 {LanMatchPolicy.FailuresPerLock} 次，对方已暂停匹配 {duration.TotalMinutes:0} 分钟。");
        }
        var remaining = RemainingBeforeLock();
        return new PairingException(ConfigSyncError.PairingCodeInvalid, $"匹配码不正确，还可以再试 {remaining} 次。", remaining);
    }

    private void EnsureUsable()
    {
        if (code is null)
            throw new PairingException(ConfigSyncError.PairingCodeExpired, "对方的匹配码还没有准备好，请稍后重试。");
        if (disabled)
            throw new PairingException(ConfigSyncError.PairingRevoked,
                "对方的匹配码因输错次数过多已停用。请让对方在「共享 → 传输」更换匹配码。");
        if (lockedUntil is { } until && time.GetUtcNow() < until)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((until - time.GetUtcNow()).TotalMinutes));
            throw new PairingException(ConfigSyncError.PairingRevoked, $"对方因多次输错暂停了匹配，请 {minutes} 分钟后再试。");
        }
    }

    private int RemainingBeforeLock() => Math.Max(0, LanMatchPolicy.FailuresPerLock - consecutiveFailures);

    private sealed record Pending(string ClientName, byte[] ClientNonce, byte[] ServerNonce, DateTimeOffset StartedAt, int? ClientPort);
}

/// <summary>匹配成功：本机证明、对方设备名与对方声明的监听端口。</summary>
public sealed record PairingAcceptance(byte[] ServerProof, string ClientName, int? ClientPort);
