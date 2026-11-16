using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Tests;

/// <summary>
/// Signed releases: a release's list of programs is signed where it's built, and PCs and stations
/// check that themselves, so a signed build installs without anyone reaching GitHub and a server
/// can't hand out a program of its own.
/// </summary>
public sealed class ReleaseSignatureTests
{
    private static readonly string Sha = new('a', 64);
    private static readonly string List = $"{Sha}  TapQueueClient.exe\n{new string('b', 64)}  tapqueue-client\n";

    private readonly ECDsa _releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _public;

    public ReleaseSignatureTests() => _public = Convert.ToBase64String(_releaseKey.ExportSubjectPublicKeyInfo());

    /// <summary>What `openssl dgst -sha256 -sign key | base64` writes, as scripts/sign-release.sh does.</summary>
    private static string Sign(ECDsa key, string list) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(list), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    [Fact]
    public void AListSignedWithATrustedKeyVouchesForTheProgramsInIt()
    {
        var signature = Sign(_releaseKey, List);

        Assert.True(ReleaseSignature.IsValid(List, signature, [_public]));
        Assert.Equal(ReleaseSignature.Verdict.Good, ReleaseSignature.Check("client", "0.19.0", "TapQueueClient.exe", Sha, List, signature, [_public]).Verdict);
        Assert.Equal(ReleaseSignature.Verdict.Good, ReleaseSignature.Check("client", "0.19.0", "TapQueueClient.exe", Sha.ToUpperInvariant(), List, signature + "\n", ["not a key", _public]).Verdict);

        // Signed, but for another program or another build of it.
        var other = ReleaseSignature.Check("client", "0.19.0", "tapqueue-station", Sha, List, signature, [_public]);
        Assert.Equal(ReleaseSignature.Verdict.Refused, other.Verdict);
        Assert.Contains("isn't the tapqueue-station in the signed release", other.Problem);
        Assert.Equal(ReleaseSignature.Verdict.Refused, ReleaseSignature.Check("client", "0.19.0", "TapQueueClient.exe", new string('c', 64), List, signature, [_public]).Verdict);
    }

    [Fact]
    public void AChangedListOrSomeoneElsesSignatureIsRefused()
    {
        using var mallory = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evil = new string('e', 64);

        // The list was edited after it was signed, to slip a program in.
        var edited = ReleaseSignature.Check("client", "0.19.0", "TapQueueClient.exe", evil, List.Replace(Sha, evil), Sign(_releaseKey, List), [_public]);
        Assert.Equal(ReleaseSignature.Verdict.Refused, edited.Verdict);
        Assert.Contains("signature this computer doesn't trust", edited.Problem);

        // Or signed properly, by a key nobody here trusts.
        var forged = List.Replace(Sha, evil);
        Assert.Equal(ReleaseSignature.Verdict.Refused, ReleaseSignature.Check("client", "0.19.0", "TapQueueClient.exe", evil, forged, Sign(mallory, forged), [_public]).Verdict);
        Assert.Equal(ReleaseSignature.Verdict.Refused, ReleaseSignature.Check("client", "0.19.0", "TapQueueClient.exe", Sha, List, "not base64!", [_public]).Verdict);
    }

    [Fact]
    public void WithoutASignatureOrAKeyThereIsNothingToGoBy()
    {
        var signature = Sign(_releaseKey, List);

        Assert.Equal(ReleaseSignature.Verdict.Unsigned, ReleaseSignature.Check("client", "0.18.0", "TapQueueClient.exe", Sha, List, null, [_public]).Verdict);
        Assert.Equal(ReleaseSignature.Verdict.Unsigned, ReleaseSignature.Check("client", "0.18.0", "TapQueueClient.exe", Sha, null, signature, [_public]).Verdict);
        // A TapQueue built without a release key trusts nobody's signature, and asks GitHub as before.
        Assert.Equal(ReleaseSignature.Verdict.Unsigned, ReleaseSignature.Check("client", "0.19.0", "TapQueueClient.exe", Sha, List, signature, []).Verdict);
    }

    [Fact]
    public void TheKeyFileIsOneKeyALineWithComments()
    {
        Assert.Equal(["AAAA", "BBBB"], ReleaseSignature.ParseKeys("# TapQueue's release keys\r\n\r\nAAAA\r\n  BBBB  \n# old: CCCC\n"));
        Assert.Empty(ReleaseSignature.ParseKeys("# none yet\n"));

        Assert.Equal(["site-key"], ReleaseSignature.TrustedKeys(" site-key ").Except(ReleaseSignature.ReleaseKeys));
        Assert.Equal(ReleaseSignature.ReleaseKeys, ReleaseSignature.TrustedKeys(""));
    }

    /// <summary>Counts what's asked of it and answers 404.</summary>
    private sealed class Counting : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static string ProgramName => OperatingSystem.IsWindows() ? "TapQueueClient.exe" : "tapqueue-client";

    [Fact]
    public async Task ASignedBuildIsDownloadedWithoutAskingWhereReleasesArePublished()
    {
        var list = $"{Sha}  {ProgramName}\n";
        var build = new ClientBuildDto("0.19.0+1a2b3c4", Sha, 1, DateTimeOffset.UtcNow, "/api/v1/client/builds/x", ClientPlatform.Current, list, Sign(_releaseKey, list));
        var (server, releases) = (new Counting(), new Counting());
        var updater = new ClientUpdater(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, NullLogger.Instance, new HttpClient(releases),
            trustedKeys: [_public]);

        // The pretend server has no file to give, so the install fails there; what matters is who was asked.
        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(build, CancellationToken.None));
        Assert.Equal(1, server.Requests);
        Assert.Equal(0, releases.Requests);
    }

    [Fact]
    public async Task ABuildWithAnUntrustedSignatureIsNeverDownloadedAndNotCheckedAnotherWay()
    {
        using var mallory = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var list = $"{Sha}  {ProgramName}\n";
        var build = new ClientBuildDto("0.19.0+1a2b3c4", Sha, 1, DateTimeOffset.UtcNow, "/api/v1/client/builds/x", ClientPlatform.Current, list, Sign(mallory, list));
        var (server, releases) = (new Counting(), new Counting());
        var updater = new ClientUpdater(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, NullLogger.Instance, new HttpClient(releases),
            trustedKeys: [_public]);

        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(build, CancellationToken.None));
        Assert.Contains("signature this computer doesn't trust", updater.LastError);
        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(build, CancellationToken.None)); // and it stays refused
        Assert.Equal(0, server.Requests);
        Assert.Equal(0, releases.Requests);
    }
}
