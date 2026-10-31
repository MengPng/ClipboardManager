using ClipboardManager.Application.Services.ConfigSync;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class PairingSessionsTests
{
    private const string ClientFingerprint = "AAAA1111";
    private const string ServerFingerprint = "BBBB2222";

    private readonly ManualTime _time = new();

    [Fact]
    public void WithoutIssuedCode_PairingIsRefused()
    {
        var sessions = new PairingSessions(_time);
        var error = Assert.Throws<PairingException>(() => sessions.Begin(ClientFingerprint, "客户端", [1]));
        Assert.Equal(ConfigSyncError.PairingCodeExpired, error.Error);
        Assert.Null(sessions.Current);
    }

    [Fact]
    public void CorrectProof_PairsOnceAndConsumesTheCode()
    {
        var sessions = new PairingSessions(_time);
        var code = sessions.Issue().Code;
        var (clientNonce, serverNonce) = Begin(sessions);

        var (serverProof, clientName) = sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof(code, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce));

        Assert.Equal("客户端", clientName);
        Assert.True(PairingProtocol.VerifyProof(
            PairingProtocol.ComputeProof(code, 'S', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce), serverProof));
        Assert.Null(sessions.Current);
        // Single use: a second handshake cannot reuse the consumed code.
        Assert.Equal(ConfigSyncError.PairingCodeExpired,
            Assert.Throws<PairingException>(() => sessions.Begin(ClientFingerprint, "客户端", [1])).Error);
    }

    [Fact]
    public void WrongCode_CountsDownAndFifthFailureRevokesTheCode()
    {
        var sessions = new PairingSessions(_time);
        var code = sessions.Issue().Code;
        var wrong = code == "000000" ? "111111" : "000000";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var (clientNonce, serverNonce) = Begin(sessions);
            var error = Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint,
                PairingProtocol.ComputeProof(wrong, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce)));
            Assert.Equal(ConfigSyncError.PairingCodeInvalid, error.Error);
            Assert.Equal(5 - attempt, error.RemainingAttempts);
            Assert.Contains($"{5 - attempt} 次", error.Message);
        }

        var (lastClientNonce, lastServerNonce) = Begin(sessions);
        var revoked = Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof(wrong, 'C', ClientFingerprint, ServerFingerprint, lastClientNonce, lastServerNonce)));
        Assert.Equal(ConfigSyncError.PairingRevoked, revoked.Error);
        Assert.Null(sessions.Current);

        // Even the right code no longer works once revoked.
        Assert.Equal(ConfigSyncError.PairingCodeExpired,
            Assert.Throws<PairingException>(() => sessions.Begin(ClientFingerprint, "客户端", [1])).Error);
    }

    [Fact]
    public void Code_ExpiresAfterFiveMinutes()
    {
        var sessions = new PairingSessions(_time);
        sessions.Issue();
        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.NotNull(sessions.Current);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(sessions.Current);
        Assert.Equal(ConfigSyncError.PairingCodeExpired,
            Assert.Throws<PairingException>(() => sessions.Begin(ClientFingerprint, "客户端", [1])).Error);
    }

    [Fact]
    public void CodeExpiringMidHandshake_IsRefused()
    {
        var sessions = new PairingSessions(_time);
        var code = sessions.Issue().Code;
        var (clientNonce, serverNonce) = Begin(sessions);
        _time.Advance(TimeSpan.FromMinutes(5));
        var error = Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint,
            PairingProtocol.ComputeProof(code, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce)));
        Assert.True(error.Error is ConfigSyncError.PairingCodeExpired or ConfigSyncError.PairingCodeInvalid);
    }

    [Fact]
    public void FinishWithoutBegin_IsRefusedWithoutBurningTheCode()
    {
        var sessions = new PairingSessions(_time);
        sessions.Issue();
        var error = Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint, new byte[32]));
        Assert.Equal(ConfigSyncError.PairingCodeInvalid, error.Error);
        Assert.NotNull(sessions.Current);
    }

    [Fact]
    public void ProofBoundToAnotherFingerprint_IsRejected()
    {
        var sessions = new PairingSessions(_time);
        var code = sessions.Issue().Code;
        var (clientNonce, serverNonce) = Begin(sessions);
        // A relay that presents a different certificate cannot reuse the proof.
        var proof = PairingProtocol.ComputeProof(code, 'C', "MITM9999", ServerFingerprint, clientNonce, serverNonce);
        Assert.Equal(ConfigSyncError.PairingCodeInvalid,
            Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint, proof)).Error);
    }

    [Fact]
    public void ReissuingResetsFailuresAndInvalidatesOpenHandshakes()
    {
        var sessions = new PairingSessions(_time);
        var first = sessions.Issue().Code;
        var (clientNonce, serverNonce) = Begin(sessions);
        var second = sessions.Issue().Code;
        Assert.Equal(ConfigSyncError.PairingCodeInvalid,
            Assert.Throws<PairingException>(() => sessions.Finish(ClientFingerprint, ServerFingerprint,
                PairingProtocol.ComputeProof(first, 'C', ClientFingerprint, ServerFingerprint, clientNonce, serverNonce))).Error);
        Assert.NotNull(sessions.Current);
        Assert.True(PairingProtocol.IsWellFormedCode(second));
    }

    [Fact]
    public void Cancel_RemovesTheCode()
    {
        var sessions = new PairingSessions(_time);
        sessions.Issue();
        sessions.Cancel();
        Assert.Null(sessions.Current);
    }

    private static (byte[] ClientNonce, byte[] ServerNonce) Begin(PairingSessions sessions)
    {
        var clientNonce = PairingProtocol.NewNonce();
        return (clientNonce, sessions.Begin(ClientFingerprint, "客户端", clientNonce));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
