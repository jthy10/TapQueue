using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>The admin console is served, and the admin API opened to it, only in dev mode.</summary>
public sealed class AdminConsoleTests
{
    [Theory]
    [InlineData("/admin/")]
    [InlineData("/admin/users/jthy1")]
    public async Task DevModeServesTheConsoleForEveryPage(string path)
    {
        await using var server = await TestServer.StartAsync(authMode: "dev");
        using var browser = server.NewClient();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<script type=\"module\" src=\"app.js\">", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DevModeServesScriptsAndStyles()
    {
        await using var server = await TestServer.StartAsync(authMode: "dev");
        using var browser = server.NewClient();

        var script = await browser.GetAsync("/admin/app.js");
        var missing = await browser.GetAsync("/admin/pages/nope.js");

        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task DevModeAdminApiNeedsNoToken()
    {
        await using var server = await TestServer.StartAsync(authMode: "dev");
        using var browser = server.NewClient();

        var info = await browser.GetFromJsonAsync<ServerInfoDto>("/api/v1/admin/server", TapQueueJson.Options);
        var wrongToken = await server.NewClient("wrong").GetAsync("/api/v1/admin/users");

        Assert.Equal("dev", info?.AuthMode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
    }

    [Fact]
    public async Task TokenModeHasNoConsoleAndNeedsTheToken()
    {
        await using var server = await TestServer.StartAsync();
        using var browser = server.NewClient();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/admin/users")).StatusCode);
    }

    [Fact]
    public async Task AdminCancelsAHeldJob()
    {
        await using var server = await TestServer.StartAsync();
        using var client = await server.SignInAsync("alice");
        await server.PrintAsync("report.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        var job = (await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs?status=held", TapQueueJson.Options))!.Single();

        var canceled = await server.Admin.DeleteAsync($"/api/v1/admin/jobs/{job.Id}");
        var again = await server.Admin.DeleteAsync($"/api/v1/admin/jobs/{job.Id}");
        var after = await server.Admin.GetFromJsonAsync<JobDto>($"/api/v1/admin/jobs/{job.Id}", TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.NoContent, canceled.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("canceled", after?.Status);
    }
}
