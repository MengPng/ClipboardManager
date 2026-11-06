using ClipboardManager.Application.Services.ConfigSync;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

/// <summary>固定匹配码（ADR-0008）：长期有效、成功不作废；连续错 5 次锁定并翻倍，累计错 20 次停用直到更换。</summary>
public sealed class PairingSessionsTests
{
    private const string ClientFingerprint = "AAAA1111";
    private const string ServerFingerprint = "BBBB2222";
    private const string Code = "482913";
    private const string Wrong = "000000";

    private readonly ManualTime _time = new();

    [Fact]
    public void WithoutCode_MatchingIsRefused()
    {
        var sessions = new PairingSessions(_time);
        var error = Assert.Throws<PairingException>(() => sessions.Begin(ClientFingerprint, "客户端", [1]));
        Assert.Equal(ConfigSyncError.PairingCodeExpired, error.Error);
        Assert.Null(sessions.Status.Code);
    }

    [Fact]
    public void SetCode_RejectsMalformedCodes()
    {
        var sessions = new PairingSessions(_time);
        Assert.Throws<ArgumentException>(() => sessions.SetCode("12345"));
        Assert.Throws<ArgumentException>(() => sessions.SetCode("12a456"));
        Assert.Null(sessions.Status.Code);
    }

    [Fact]
    public void CorrectProof_MatchesAndTheCodeStaysValid()
    {
        var sessions = Ready();
        var (clientNonce, serverNonce) = Begin(sessions, clientPort: 53318);

        var acceptance = sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof(Code, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce));

        Assert.Equal("客户端", acceptance.ClientName);
        Assert.Equal(53318, acceptance.ClientPort);
        Assert.True(PairingProtocol.VerifyProof(
            PairingProtocol.ComputeProof(Code, 'S', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce), acceptance.ServerProof));

        // 固定码：同一个码之后还能给另一台设备使用。
        var (secondClient, secondServer) = Begin(sessions, "CCCC3333");
        var second = sessions.Finish("CCCC3333", ServerFingerprint,
            PairingProtocol.ComputeProof(Code, 'C', "CCCC3333", ServerFingerprint, secondClient, secondServer));
        Assert.Equal("客户端", second.ClientName);
        Assert.Equal(Code, sessions.Status.Code);
    }

    [Fact]
    public void OutOfRangePort_IsDropped()
    {
        var sessions = Ready();
        var (clientNonce, serverNonce) = Begin(sessions, clientPort: 70000);
        var acceptance = sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof(Code, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce));
        Assert.Null(acceptance.ClientPort);
    }

    [Fact]
    public void FiveWrongAttempts_LockForOneMinuteThenTwo()
    {
        var sessions = Ready();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var error = FailOnce(sessions);
            Assert.Equal(ConfigSyncError.PairingCodeInvalid, error.Error);
            Assert.Equal(5 - attempt, error.RemainingAttempts);
            Assert.Contains($"{5 - attempt} 次", error.Message);
        }

        var locked = FailOnce(sessions);
        Assert.Equal(ConfigSyncError.PairingRevoked, locked.Error);
        Assert.Contains("1 分钟", locked.Message);
        Assert.NotNull(sessions.Status.LockedUntil);

        // 锁定期间连正确的码也不能用。
        Assert.Equal(ConfigSyncError.PairingRevoked,
            Assert.Throws<PairingException>(() => sessions.Begin(ClientFingerprint, "客户端", [1])).Error);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(sessions.Status.LockedUntil);
        for (var attempt = 1; attempt <= 4; attempt++) FailOnce(sessions);
        var secondLock = FailOnce(sessions);
        Assert.Contains("2 分钟", secondLock.Message);
    }

    [Fact]
    public void SuccessResetsConsecutiveFailures()
    {
        var sessions = Ready();
        for (var attempt = 1; attempt <= 4; attempt++) FailOnce(sessions);
        var (clientNonce, serverNonce) = Begin(sessions);
        sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof(Code, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce));

        var error = FailOnce(sessions);
        Assert.Equal(ConfigSyncError.PairingCodeInvalid, error.Error);
        Assert.Equal(4, error.RemainingAttempts);
        Assert.Equal(5, sessions.Status.TotalFailures);
    }

    [Fact]
    public void TwentyTotalFailures_DisableTheCodeUntilChanged()
    {
        var sessions = Ready();
        var disabledRaised = new ManualResetEventSlim();
        sessions.CodeDisabled += (_, _) => disabledRaised.Set();

        PairingException? last = null;
        for (var round = 0; round < 4; round++)
        {
            for (var attempt = 0; attempt < LanMatchPolicy.FailuresPerLock; attempt++) last = FailOnce(sessions);
            _time.Advance(LanMatchPolicy.MaxLock);
        }

        Assert.Equal(ConfigSyncError.PairingRevoked, last!.Error);
        Assert.Contains("停用", last.Message);
        Assert.True(sessions.Status.Disabled);
        Assert.Equal(LanMatchPolicy.MaxTotalFailures, sessions.Status.TotalFailures);
        Assert.True(disabledRaised.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(ConfigSyncError.PairingRevoked,
            Assert.Throws<PairingException>(() => sessions.Begin(ClientFingerprint, "客户端", [1])).Error);

        // 更换匹配码后恢复，计数清零。
        sessions.SetCode("135790");
        Assert.False(sessions.Status.Disabled);
        Assert.Equal(0, sessions.Status.TotalFailures);
        var (clientNonce, serverNonce) = Begin(sessions);
        sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof("135790", 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce));
    }

    [Fact]
    public void LoadingADisabledCode_RefusesMatching()
    {
        var sessions = new PairingSessions(_time);
        sessions.SetCode(Code, isDisabled: true);
        Assert.True(sessions.Status.Disabled);
        Assert.Equal(ConfigSyncError.PairingRevoked,
            Assert.Throws<PairingException>(() => sessions.Begin(ClientFingerprint, "客户端", [1])).Error);
    }

    [Fact]
    public void HandshakeOlderThanSixtySeconds_IsRefused()
    {
        var sessions = Ready();
        var (clientNonce, serverNonce) = Begin(sessions);
        _time.Advance(TimeSpan.FromSeconds(61));
        var error = Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof(Code, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce)));
        Assert.Equal(ConfigSyncError.PairingCodeInvalid, error.Error);
        Assert.Equal(0, sessions.Status.TotalFailures);
    }

    [Fact]
    public void FinishWithoutBegin_IsRefusedWithoutCountingAFailure()
    {
        var sessions = Ready();
        var error = Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint, new byte[32]));
        Assert.Equal(ConfigSyncError.PairingCodeInvalid, error.Error);
        Assert.Equal(0, sessions.Status.TotalFailures);
    }

    [Fact]
    public void ProofBoundToAnotherFingerprint_IsRejected()
    {
        var sessions = Ready();
        var (clientNonce, serverNonce) = Begin(sessions);
        // A relay that presents a different certificate cannot reuse the proof.
        var proof = PairingProtocol.ComputeProof(Code, 'C', "MITM9999", ServerFingerprint, clientNonce, serverNonce);
        Assert.Equal(ConfigSyncError.PairingCodeInvalid,
            Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint, proof)).Error);
    }

    [Fact]
    public void ChangingTheCode_InvalidatesOpenHandshakes()
    {
        var sessions = Ready();
        var (clientNonce, serverNonce) = Begin(sessions);
        sessions.SetCode("135790");
        Assert.Equal(ConfigSyncError.PairingCodeInvalid,
            Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint,
                PairingProtocol.ComputeProof(Code, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce))).Error);
        Assert.Equal("135790", sessions.Status.Code);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(20, 60)]
    public void LockDuration_DoublesUpToOneHour(int lockCount, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), LanMatchPolicy.LockDuration(lockCount));

    private PairingSessions Ready()
    {
        var sessions = new PairingSessions(_time);
        sessions.SetCode(Code);
        return sessions;
    }

    private static PairingException FailOnce(PairingSessions sessions)
    {
        var (clientNonce, serverNonce) = Begin(sessions);
        return Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof(Wrong, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce)));
    }

    private static (byte[] ClientNonce, byte[] ServerNonce) Begin(PairingSessions sessions, string client = ClientFingerprint,
        int? clientPort = null)
    {
        var clientNonce = PairingProtocol.NewNonce();
        return (clientNonce, sessions.Begin(client, "客户端", clientNonce, clientPort));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
