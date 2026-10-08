using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TapQueue.Client;
using TapQueue.Shared;

namespace TapQueue.Client.Tests;

public sealed class ServerCertificateTests : IDisposable
{
    private const SslPolicyErrors Untrusted = SslPolicyErrors.RemoteCertificateChainErrors;
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-pin").FullName;
    private string Saved => Path.Combine(_dir, "server-certificate.pem");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("ab:cd")]
    [InlineData("not hex at all, not hex at all, not hex at all, not hex at all, 1234")]
    public void AFingerprintMustBeSha256(string fingerprint) =>
        Assert.Throws<InvalidDataException>(() => ServerCertificatePin.Normalize(fingerprint));

    [Fact]
    public void FingerprintsMatchWithOrWithoutColons()
    {
        using var certificate = Certificate(["tapqueue"]);
        var fingerprint = ServerCertificatePin.FingerprintOf(certificate);
        var pin = new ServerCertificatePin("https://tapqueue:8632", fingerprint.Replace(":", "").ToLowerInvariant(), null, learn: false);
        Assert.True(pin.Check(certificate, Untrusted));
        Assert.Same(certificate, pin.Pinned);
    }

    [Fact]
    public void ACertificateTheSystemTrustsNeedsNoPin()
    {
        using var certificate = Certificate(["tapqueue"]);
        var pin = new ServerCertificatePin("https://tapqueue:8632", null, null, learn: false);
        Assert.True(pin.Check(certificate, SslPolicyErrors.None));
        Assert.Null(pin.Pinned);
        Assert.False(pin.Check(certificate, Untrusted));
    }

    [Fact]
    public void TheServiceTrustsTheFirstCertificateAndNoOtherAfterIt()
    {
        using var first = Certificate(["tapqueue"]);
        using var second = Certificate(["tapqueue"]);
        var service = new ServerCertificatePin("https://tapqueue:8632", null, Saved, learn: true);

        Assert.True(service.Check(first, Untrusted));
        Assert.True(File.Exists(Saved));
        Assert.False(service.Check(second, Untrusted));
        Assert.Contains("isn't the one this computer trusts", service.Refusal);

        // Starting again (or the tray app) reads what was saved.
        var tray = new ServerCertificatePin("https://TapQueue:8632/", null, Saved, learn: false);
        Assert.True(tray.Check(first, Untrusted));
        Assert.False(tray.Check(second, Untrusted));
    }

    [Fact]
    public void TheTrayAppDoesntLearnACertificate()
    {
        using var certificate = Certificate(["tapqueue"]);
        var tray = new ServerCertificatePin("https://tapqueue:8632", null, Saved, learn: false);
        Assert.False(tray.Check(certificate, Untrusted));
        Assert.False(File.Exists(Saved));
        Assert.Contains("once the TapQueue service has connected", tray.Refusal);
    }

    [Fact]
    public void ACertificateSavedForAnotherServerIsntUsed()
    {
        using var old = Certificate(["old"]);
        using var current = Certificate(["new"]);
        Assert.True(new ServerCertificatePin("https://old:8632", null, Saved, learn: true).Check(old, Untrusted));

        var moved = new ServerCertificatePin("https://new:8632", null, Saved, learn: true);
        Assert.True(moved.Check(current, Untrusted));
        Assert.False(moved.Check(old, Untrusted));
    }

    [Fact]
    public void AConfiguredFingerprintWinsOverTheSavedCertificate()
    {
        using var old = Certificate(["tapqueue"]);
        using var renewed = Certificate(["tapqueue"]);
        Assert.True(new ServerCertificatePin("https://tapqueue:8632", null, Saved, learn: true).Check(old, Untrusted));

        var pin = new ServerCertificatePin("https://tapqueue:8632", ServerCertificatePin.FingerprintOf(renewed), Saved, learn: true);
        Assert.True(pin.Check(renewed, Untrusted));
        Assert.False(pin.Check(old, Untrusted));
    }

    [Fact]
    public void TheServersOwnCertificateMayBeTrustedSystemWide()
    {
        using var certificate = Certificate(["tapqueue", "tapqueue.corp.example", "localhost", "print.corp.example", "192.0.2.10"]);
        Assert.Null(SystemTrust.Refusal(certificate, new Uri("https://tapqueue.corp.example:8632"), pcDomain: ""));
        // By address, its names have to be in the PC's own domain.
        Assert.Null(SystemTrust.Refusal(certificate, new Uri("https://192.0.2.10:8632"), pcDomain: "corp.example"));
        Assert.NotNull(SystemTrust.Refusal(certificate, new Uri("https://192.0.2.10:8632"), pcDomain: ""));
    }

    [Theory]
    [InlineData("https://other.corp.example:8632", "isn't for")]
    [InlineData("https://tapqueue.corp.example:8632", "outside the server's and this PC's domain", "tapqueue.corp.example", "www.bank.example")]
    [InlineData("https://tapqueue.corp.example:8632", "wildcard", "tapqueue.corp.example", "*.corp.example")]
    [InlineData("https://tapqueue.example:8632", "outside the server's and this PC's domain", "tapqueue.example", "bank.example")]
    public void CertificatesThatCouldStandInForOtherSitesArentTrustedSystemWide(string server, string reason, params string[] names)
    {
        using var certificate = Certificate(names.Length > 0 ? names : ["tapqueue.corp.example"]);
        Assert.Contains(reason, SystemTrust.Refusal(certificate, new Uri(server), pcDomain: ""));
    }

    [Fact]
    public void ACertificateAuthorityIsntTrustedSystemWide()
    {
        using var ca = Certificate(["tapqueue"], ca: true);
        Assert.Contains("certificate authority", SystemTrust.Refusal(ca, new Uri("https://tapqueue:8632"), pcDomain: ""));
    }

    private static X509Certificate2 Certificate(string[] names, bool ca = false)
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest($"CN={names[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var address)) san.AddIpAddress(address);
            else san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadCertificate(certificate.RawData);
    }
}
