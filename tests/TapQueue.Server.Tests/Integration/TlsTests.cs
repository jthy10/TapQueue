using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using TapQueue.Server.Ipp;
using TapQueue.Server.Tls;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>HTTPS and IPPS: the self-signed certificate, pinning it, and printing over TLS.</summary>
public sealed class TlsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-tls").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task TheAdminApiShowsTheCertificateItServes()
    {
        await using var server = await TestServer.StartAsync(tls: true);
        var info = await TestServer.ReadAsync<ServerInfoDto>(await server.Admin.GetAsync("/api/v1/admin/server"));

        Assert.NotNull(info.Tls);
        Assert.True(info.Tls.SelfSigned);
        Assert.Equal(server.HttpsUri!.Port, info.Tls.Port);
        Assert.Contains("localhost", info.Tls.Names);
        Assert.Contains("127.0.0.1", info.Tls.Names);
        Assert.Equal(ServerCertificatePin.FingerprintOf(server.Service<ServerCertificate>().Current), info.Tls.Fingerprint);
    }

    [Fact]
    public async Task WithoutTlsTheServerSaysSo()
    {
        await using var server = await TestServer.StartAsync();
        var info = await TestServer.ReadAsync<ServerInfoDto>(await server.Admin.GetAsync("/api/v1/admin/server"));
        Assert.Null(info.Tls);
        Assert.Null(server.HttpsUri);
    }

    [Fact]
    public async Task AConfiguredFingerprintIsTrustedAndAnyOtherIsNot()
    {
        await using var server = await TestServer.StartAsync(tls: true);
        var fingerprint = server.Service<ServerCertificate>().Fingerprint;
        var url = server.HttpsUri!.ToString();

        using var pinned = new HttpClient(new ServerCertificatePin(url, fingerprint, null, learn: false).CreateHandler());
        var health = await pinned.GetFromJsonAsync<HealthDto>(new Uri(server.HttpsUri, "healthz"));
        Assert.Equal(server.HttpsUri.Port, health!.TlsPort);

        var unpinned = new ServerCertificatePin(url, null, null, learn: false);
        using var refused = new HttpClient(unpinned.CreateHandler());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => refused.GetStringAsync(new Uri(server.HttpsUri, "healthz")));
        Assert.Contains("isn't trusted", error.Message);
        Assert.Contains(fingerprint, error.Message);

        using var wrong = new HttpClient(new ServerCertificatePin(url, new string('A', 64), null, learn: false).CreateHandler());
        await Assert.ThrowsAsync<HttpRequestException>(() => wrong.GetStringAsync(new Uri(server.HttpsUri, "healthz")));
    }

    [Fact]
    public async Task TheFirstCertificateSeenIsSavedAndAnotherServersIsRefused()
    {
        var saved = Path.Combine(_dir, "server-certificate.pem");
        await using var server = await TestServer.StartAsync(tls: true);
        var url = server.HttpsUri!.ToString();

        var service = new ServerCertificatePin(url, null, saved, learn: true);
        X509Learned? learned = null;
        service.Learned = c => learned = new X509Learned(c.Subject);
        using (var http = new HttpClient(service.CreateHandler()))
            await http.GetStringAsync(new Uri(server.HttpsUri, "healthz"));
        Assert.NotNull(learned);
        Assert.True(File.Exists(saved));
        Assert.NotNull(service.Pinned);

        // The tray app doesn't learn, but trusts what the service saved.
        using (var tray = new HttpClient(new ServerCertificatePin(url, null, saved, learn: false).CreateHandler()))
            await tray.GetStringAsync(new Uri(server.HttpsUri, "healthz"));

        // Something else answering at the same address with its own certificate isn't trusted.
        await using var impostor = await TestServer.StartAsync(tls: true);
        var sameUrl = new ServerCertificatePin(impostor.HttpsUri!.ToString(), null, saved, learn: true);
        File.WriteAllText(saved, File.ReadAllText(saved).Replace(server.HttpsUri.Authority, impostor.HttpsUri.Authority));
        using var http2 = new HttpClient(sameUrl.CreateHandler());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http2.GetStringAsync(new Uri(impostor.HttpsUri, "healthz")));
        Assert.Contains("isn't the one this computer trusts", error.Message);
    }

    private sealed record X509Learned(string Subject);

    private sealed record HealthDto(string Status, string Version, int? TlsPort);

    [Fact]
    public async Task QueuesAdvertiseIppsOverTls()
    {
        await using var server = await TestServer.StartAsync(tls: true);
        var ipps = $"ipps://{server.HttpsUri!.Authority}/ipp/{TestServer.QueueId}";
        var request = IppMessage.CreateRequest(IppOperation.GetPrinterAttributes, IppClient.NextRequestId(), ipps);
        using var ipp = new IppClient(tlsSkipVerify: true, TimeSpan.FromSeconds(30));

        var response = await ipp.SendAsync(ipps, request);

        string? Attribute(IppMessage message, string name) => message.Find(IppTag.PrinterAttributes, name)?.First?.AsString();
        Assert.Equal(ipps, Attribute(response, "printer-uri-supported"));
        Assert.Equal("tls", Attribute(response, "uri-security-supported"));
        Assert.StartsWith($"https://{server.HttpsUri.Authority}/icons/", Attribute(response, "printer-icons"));

        // And plain IPP still says it isn't secure.
        var plain = await ipp.SendAsync(server.QueueUri, IppMessage.CreateRequest(IppOperation.GetPrinterAttributes, IppClient.NextRequestId(), server.QueueUri));
        Assert.Equal("none", Attribute(plain, "uri-security-supported"));
    }

    [Fact]
    public async Task AJobPrintedOverIppsIsHeld()
    {
        await using var server = await TestServer.StartAsync(tls: true);
        using var someone = await server.SignInAsync("someone");
        var ipps = $"ipps://{server.HttpsUri!.Authority}/ipp/{TestServer.QueueId}";
        var request = IppMessage.CreateRequest(IppOperation.PrintJob, IppClient.NextRequestId(), ipps);
        request.Group(IppTag.OperationAttributes)
            .Add("requesting-user-name", IppValue.Name("someone"))
            .Add("job-name", IppValue.Name("over tls"))
            .Add("document-format", IppValue.MimeType("application/pdf"));
        using var ipp = new IppClient(tlsSkipVerify: true, TimeSpan.FromSeconds(30));

        var response = await ipp.SendAsync(ipps, request, new MemoryStream("%PDF-1.4\n"u8.ToArray()));

        Assert.Equal(IppStatus.Ok, response.Code);
        var jobs = await TestServer.ReadAsync<List<JobDto>>(await server.Admin.GetAsync("/api/v1/admin/jobs"));
        Assert.Contains(jobs, j => j.Name == "over tls");
    }

    [Fact]
    public async Task ACertificateFromFilesIsServedAndPickedUpAgainWhenItChanges()
    {
        var cert = Path.Combine(_dir, "cert.pem");
        var key = Path.Combine(_dir, "key.pem");
        ServerCertificate.CreateSelfSigned(["first.example"], cert, key);
        await using var server = await TestServer.StartAsync(tls: true, configureTls: t => (t.CertFile, t.KeyFile) = (cert, key));
        var certificate = server.Service<ServerCertificate>();
        Assert.False(certificate.SelfSigned);
        Assert.Equal(["first.example"], certificate.Names);
        var first = certificate.Fingerprint;
        Assert.Equal(first, await FingerprintServedAsync(server.HttpsUri!));

        ServerCertificate.CreateSelfSigned(["second.example"], cert, key);
        File.SetLastWriteTimeUtc(cert, DateTime.UtcNow.AddSeconds(5)); // some filesystems keep whole seconds
        File.SetLastWriteTimeUtc(key, DateTime.UtcNow.AddSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(10.5)); // the files are looked at again every 10 seconds

        Assert.NotEqual(first, await FingerprintServedAsync(server.HttpsUri!));
        Assert.Equal(["second.example"], certificate.Names);
    }

    [Fact]
    public async Task TheSelfSignedCertificateIsKeptAcrossRestarts()
    {
        var first = new ServerCertificate(new Config.TlsSection(), _dir);
        var again = new ServerCertificate(new Config.TlsSection { Names = ["print.example.org"] }, _dir);

        Assert.Equal(first.Fingerprint, again.Fingerprint);
        Assert.Equal(["print.example.org"], again.Missing);
        Assert.Empty(first.Missing);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "tls", "server.key")));
        var basic = first.Current.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension>().Single();
        Assert.False(basic.CertificateAuthority);
    }

    [Theory]
    [InlineData(false, "10.0.0.5", "/api/v1/printers", false)]
    [InlineData(false, "10.0.0.5", "/ipp/secure", false)]
    [InlineData(false, "10.0.0.5", "/admin", false)]
    [InlineData(true, "10.0.0.5", "/api/v1/printers", true)]
    [InlineData(false, "127.0.0.1", "/api/v1/admin/users", true)]
    [InlineData(false, "::ffff:127.0.0.1", "/api/v1/admin/users", true)]
    [InlineData(false, "::1", "/api/v1/admin/users", true)]
    [InlineData(false, "10.0.0.5", "/", true)]
    [InlineData(false, "10.0.0.5", "/healthz", true)]
    public void RequiringTlsRefusesPlainHttpFromOtherMachines(bool https, string remote, string path, bool allowed) =>
        Assert.Equal(allowed, TlsRequirement.Allows(https, IPAddress.Parse(remote), path));

    [Fact]
    public async Task DiscoveryRepliesNameTheTlsPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var discoveryPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        await using var server = await TestServer.StartAsync(discoveryPort: discoveryPort, tls: true);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await udp.SendAsync(ServerDiscovery.RequestBytes, new IPEndPoint(IPAddress.Loopback, discoveryPort));
        var reply = ServerDiscovery.ParseReply((await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);

        Assert.Equal(server.BaseUri.Port, reply!.Port);
        Assert.Equal(server.HttpsUri!.Port, reply.TlsPort);
    }

    private static async Task<string> FingerprintServedAsync(Uri https)
    {
        string? seen = null;
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, c, _, _) =>
        {
            seen = ServerCertificatePin.FingerprintOf(new System.Security.Cryptography.X509Certificates.X509Certificate2(c!));
            return true;
        };
        using var http = new HttpClient(handler);
        await http.GetStringAsync(new Uri(https, "healthz"));
        return seen!;
    }
}
