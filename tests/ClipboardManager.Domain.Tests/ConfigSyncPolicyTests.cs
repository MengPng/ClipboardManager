using System.Text;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Tests;

public sealed class ConfigSyncPolicyTests
{
    [Fact]
    public void Limits_MatchTheSpec()
    {
        Assert.Equal(53318, ConfigSyncPolicy.DefaultPort);
        Assert.Equal(4L * 1024 * 1024, ConfigSyncPolicy.MaxManifestBytes);
        Assert.Equal(200L * 1024 * 1024, ConfigSyncPolicy.MaxPackageBytes);
        Assert.Equal(50L * 1024 * 1024, ConfigSyncPolicy.MaxFileBytes);
        Assert.Equal(3, ConfigSyncPolicy.MaxSnapshots);
        Assert.Equal(TimeSpan.FromMinutes(5), ConfigSyncPolicy.PairingCodeLifetime);
        Assert.Equal(TimeSpan.FromMinutes(30), ConfigSyncPolicy.DefaultCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), ConfigSyncPolicy.AutoPublishQuietPeriod);
        Assert.Equal(5, ConfigSyncPolicy.MaxPairingFailures);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("RUN.BAT")]
    [InlineData("a.ps1")]
    [InlineData("shortcut.lnk")]
    [InlineData("pack.msi")]
    [InlineData("x.js")]
    [InlineData("link.url")]
    [InlineData("C:\\temp\\evil.cmd")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("evil.exe.")]
    public void HighRiskFiles_AreFlagged(string name) => Assert.True(ConfigSyncPolicy.IsHighRiskFile(name));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("photo.png")]
    [InlineData("report.xlsx")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryFiles_AreNotFlagged(string? name) => Assert.False(ConfigSyncPolicy.IsHighRiskFile(name));

    [Theory]
    [InlineData("../../evil.txt", "evil.txt")]
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("na<me>:x?.txt", "na_me__x_.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("  spaced.txt  ", "spaced.txt")]
    [InlineData("trailing.", "trailing")]
    [InlineData("", "file")]
    [InlineData("...", "file")]
    public void FileNames_AreNormalized(string input, string expected) =>
        Assert.Equal(expected, ConfigSyncPolicy.SanitizeFileName(input));

    [Fact]
    public void FileNames_NeverContainSeparatorsOrControlCharacters()
    {
        var name = ConfigSyncPolicy.SanitizeFileName("a\u0001b\\c/d\u0000.txt");
        Assert.Equal("d_.txt", name);
        Assert.DoesNotContain(name, character => character < ' ');
        Assert.True(ConfigSyncPolicy.SanitizeFileName(new string('x', 500) + ".txt").Length <= 124);
    }

    [Theory]
    [InlineData("192.168.1.20:53318", "192.168.1.20", 53318)]
    [InlineData("192.168.1.20", "192.168.1.20", 53318)]
    [InlineData("  192.168.1.20:9000  ", "192.168.1.20", 9000)]
    [InlineData("office-pc:53318", "office-pc", 53318)]
    [InlineData("https://192.168.1.20:53318/", "192.168.1.20", 53318)]
    [InlineData("[fe80::1]:53318", "fe80::1", 53318)]
    [InlineData("[fe80::1]", "fe80::1", 53318)]
    public void AddressParsing_AcceptsValidForms(string text, string host, int port)
    {
        Assert.True(ConfigSyncPolicy.TryParseAddress(text, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("192.168.1.20:0")]
    [InlineData("192.168.1.20:65536")]
    [InlineData("192.168.1.20:abc")]
    [InlineData("192.168.1.20:")]
    [InlineData("a b:53318")]
    [InlineData("fe80::1")]
    [InlineData("host/path:53318")]
    [InlineData(":53318")]
    public void AddressParsing_RejectsInvalidForms(string? text) =>
        Assert.False(ConfigSyncPolicy.TryParseAddress(text, out _, out _));

    [Fact]
    public void ImageHeaders_OnlyPngAndJpegPass()
    {
        Assert.True(ConfigSyncPolicy.IsPngOrJpeg([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]));
        Assert.True(ConfigSyncPolicy.IsPngOrJpeg([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.False(ConfigSyncPolicy.IsPngOrJpeg("GIF89a"u8));
        Assert.False(ConfigSyncPolicy.IsPngOrJpeg("<svg xmlns"u8));
        Assert.False(ConfigSyncPolicy.IsPngOrJpeg([]));
    }

    [Fact]
    public void Fingerprint_IsStableUppercaseSha256AndPrefixIsTwelveCharacters()
    {
        var fingerprint = ConfigSyncPolicy.Fingerprint(Encoding.UTF8.GetBytes("certificate"));
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint, ConfigSyncPolicy.Fingerprint(Encoding.UTF8.GetBytes("certificate")));
        Assert.NotEqual(fingerprint, ConfigSyncPolicy.Fingerprint(Encoding.UTF8.GetBytes("other")));
        Assert.Equal(fingerprint[..12], ConfigSyncPolicy.FingerprintPrefix(fingerprint));
        Assert.Equal("ABC", ConfigSyncPolicy.FingerprintPrefix("ABC"));
    }

    [Fact]
    public void Compare_ReportsAddedModifiedRemovedById()
    {
        var kept = Guid.NewGuid();
        var changed = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var installed = Manifest(1, Item(kept, "不变", "A"), Item(changed, "会改", "B1"), Item(gone, "将删除", "C"));
        var remote = Manifest(2, Item(kept, "不变", "A"), Item(changed, "会改", "B2"), Item(fresh, "新增", "D"));

        var summary = ConfigSyncPolicy.Compare(installed, remote);

        Assert.Equal((1, 1, 1), (summary.Added, summary.Modified, summary.Removed));
        Assert.Equal(["新增"], summary.AddedTitles);
        Assert.Equal(["会改"], summary.ModifiedTitles);
        Assert.Equal(["将删除"], summary.RemovedTitles);
    }

    [Fact]
    public void Compare_AgainstNothingInstalled_CountsEverythingAsAdded()
    {
        var summary = ConfigSyncPolicy.Compare(null, Manifest(1, Item(Guid.NewGuid(), "x", "1"), Item(Guid.NewGuid(), null, "2")));
        Assert.Equal((2, 0, 0), (summary.Added, summary.Modified, summary.Removed));
        Assert.Contains("（无标题）", summary.AddedTitles);
    }

    [Fact]
    public void Compare_TitleSamplesAreCapped()
    {
        var many = Enumerable.Range(0, 30).Select(i => Item(Guid.NewGuid(), $"t{i}", $"d{i}")).ToArray();
        var summary = ConfigSyncPolicy.Compare(null, Manifest(1, many), sampleLimit: 5);
        Assert.Equal(30, summary.Added);
        Assert.Equal(5, summary.AddedTitles.Count);
    }

    [Fact]
    public void Manifest_RoundTripsThroughJson()
    {
        var item = new ConfigManifestItem(Guid.NewGuid(), 3, "文件", "digest", 12, ["setup.exe"]);
        var manifest = Manifest(4, item);
        var parsed = ConfigSyncPolicy.ParseManifest(ConfigSyncPolicy.SerializeManifest(manifest));
        Assert.Equal(manifest.PublishedId, parsed.PublishedId);
        Assert.Equal(4, parsed.Version);
        Assert.True(parsed.HasRiskyFiles);
        Assert.Equal(["setup.exe"], parsed.Items[0].RiskyFiles);
    }

    private static ConfigManifest Manifest(int version, params ConfigManifestItem[] items) =>
        new(ConfigSyncPolicy.ProtocolVersion, Guid.NewGuid(), "测试", version, "hash", 1, DateTimeOffset.UtcNow, items);

    private static ConfigManifestItem Item(Guid id, string? title, string digest) => new(id, 0, title, digest, 1, []);
}

public sealed class PairingProtocolTests
{
    private const string Client = "AAAA1111";
    private const string Server = "BBBB2222";
    private static readonly byte[] ClientNonce = [1, 2, 3, 4];
    private static readonly byte[] ServerNonce = [9, 8, 7, 6];

    [Fact]
    public void GeneratedCodes_AreSixDigits()
    {
        for (var i = 0; i < 200; i++) Assert.True(PairingProtocol.IsWellFormedCode(PairingProtocol.GenerateCode()));
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("000000", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12345a", false)]
    [InlineData("12 456", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CodeShape_IsValidated(string? code, bool expected) => Assert.Equal(expected, PairingProtocol.IsWellFormedCode(code));

    [Fact]
    public void Proof_DoesNotContainTheCode()
    {
        var proof = PairingProtocol.ComputeProof("482916", 'C', Client, Server, ClientNonce, ServerNonce);
        Assert.Equal(32, proof.Length);
        Assert.DoesNotContain("482916", Convert.ToHexString(proof));
        Assert.DoesNotContain("482916", Encoding.UTF8.GetString(proof));
    }

    [Fact]
    public void Proof_VerifiesOnlyWithSameCodeRoleFingerprintsAndNonces()
    {
        var expected = PairingProtocol.ComputeProof("482916", 'C', Client, Server, ClientNonce, ServerNonce);
        Assert.True(PairingProtocol.VerifyProof(expected, PairingProtocol.ComputeProof("482916", 'C', Client, Server, ClientNonce, ServerNonce)));
        // Case-insensitive fingerprints (they are hex text) but every other input is bound.
        Assert.True(PairingProtocol.VerifyProof(expected, PairingProtocol.ComputeProof("482916", 'C', Client.ToLowerInvariant(), Server.ToLowerInvariant(), ClientNonce, ServerNonce)));
        Assert.False(PairingProtocol.VerifyProof(expected, PairingProtocol.ComputeProof("482917", 'C', Client, Server, ClientNonce, ServerNonce)));
        Assert.False(PairingProtocol.VerifyProof(expected, PairingProtocol.ComputeProof("482916", 'S', Client, Server, ClientNonce, ServerNonce)));
        Assert.False(PairingProtocol.VerifyProof(expected, PairingProtocol.ComputeProof("482916", 'C', "CCCC3333", Server, ClientNonce, ServerNonce)));
        Assert.False(PairingProtocol.VerifyProof(expected, PairingProtocol.ComputeProof("482916", 'C', Client, "CCCC3333", ClientNonce, ServerNonce)));
        Assert.False(PairingProtocol.VerifyProof(expected, PairingProtocol.ComputeProof("482916", 'C', Client, Server, [5, 5, 5, 5], ServerNonce)));
        Assert.False(PairingProtocol.VerifyProof(expected, PairingProtocol.ComputeProof("482916", 'C', Client, Server, ClientNonce, [5, 5, 5, 5])));
    }

    [Fact]
    public void ClientProof_CannotBeReflectedAsServerProof()
    {
        var clientProof = PairingProtocol.ComputeProof("482916", 'C', Client, Server, ClientNonce, ServerNonce);
        var serverProof = PairingProtocol.ComputeProof("482916", 'S', Client, Server, ClientNonce, ServerNonce);
        Assert.False(PairingProtocol.VerifyProof(serverProof, clientProof));
    }

    [Fact]
    public void Verify_RejectsNullAndWrongLength()
    {
        var expected = PairingProtocol.ComputeProof("482916", 'C', Client, Server, ClientNonce, ServerNonce);
        Assert.False(PairingProtocol.VerifyProof(expected, null));
        Assert.False(PairingProtocol.VerifyProof(expected, expected[..16]));
        Assert.False(PairingProtocol.VerifyProof(expected, [.. expected, 0]));
    }
}
