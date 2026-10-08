using System.Net;
using Microsoft.AspNetCore.Http;
using TapQueue.Server.Admin;
using TapQueue.Server.Tls;
using TapQueue.Shared;

namespace TapQueue.Server.Tests.Integration;

/// <summary>The admin console moves browsers on other machines to HTTPS when the server has it.</summary>
public sealed class ConsoleHttpsTests
{
    private static HttpRequest Request(string scheme, string host, string path, string query = "")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        return http.Request;
    }

    [Fact]
    public void PlainHttpFromAnotherMachineGoesToHttps()
    {
        var location = AdminUi.HttpsLocation(Request("http", "print.example.org:8631", "/admin/jobs", "?q=1"), IPAddress.Parse("10.0.0.5"), 8632);

        Assert.Equal("https://print.example.org:8632/admin/jobs?q=1", location);
    }

    [Theory]
    [InlineData("http", "127.0.0.1", 8632)]   // this machine
    [InlineData("http", "::1", 8632)]
    [InlineData("http", "10.0.0.5", null)]    // no HTTPS to go to
    [InlineData("https", "10.0.0.5", 8632)]   // already there
    public void OtherwiseTheConsoleIsServed(string scheme, string remote, int? tlsPort)
    {
        Assert.Null(AdminUi.HttpsLocation(Request(scheme, "print.example.org", "/admin/"), IPAddress.Parse(remote), tlsPort));
    }

    [Fact]
    public async Task OverHttpsTheConsoleAsksBrowsersToStayOnHttps()
    {
        await using var server = await TestServer.StartAsync(tls: true);
        var fingerprint = server.Service<ServerCertificate>().Fingerprint;
        using var https = new HttpClient(new ServerCertificatePin(server.HttpsUri!.ToString(), fingerprint, null, learn: false).CreateHandler());

        var overHttps = await https.GetAsync(new Uri(server.HttpsUri, "admin/"));
        var overHttp = await server.Admin.GetAsync("/admin/");

        Assert.Equal(HttpStatusCode.OK, overHttps.StatusCode);
        Assert.Equal("max-age=31536000", overHttps.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Equal(HttpStatusCode.OK, overHttp.StatusCode);
        Assert.False(overHttp.Headers.Contains("Strict-Transport-Security"));
    }
}
