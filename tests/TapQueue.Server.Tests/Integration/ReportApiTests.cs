using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Admins;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>The usage report over the admin API.</summary>
public sealed class ReportApiTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task WhatWasJustPrintedIsInThisMonthsReport()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("report.pdf", "%PDF-1.4 test"u8.ToArray(), copies: 3);
        (await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options)).EnsureSuccessStatusCode();

        var byUser = (await _server.Admin.GetFromJsonAsync<UsageReportDto>("/api/v1/admin/reports/usage", TapQueueJson.Options))!;
        var row = Assert.Single(byUser.Rows);
        Assert.Equal(("alice", 1, 3L), (row.Key, row.Jobs, row.Pages));
        Assert.Equal(ReportGrouping.User, byUser.By);
        Assert.EndsWith("-01", byUser.From);

        var byPrinter = (await _server.Admin.GetFromJsonAsync<UsageReportDto>("/api/v1/admin/reports/usage?by=printer", TapQueueJson.Options))!;
        Assert.Equal((TestServer.PrinterId, "Office printer"), (byPrinter.Rows.Single().Key, byPrinter.Rows.Single().Name));

        var csv = await _server.Admin.GetAsync("/api/v1/admin/reports/usage?by=queue&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("tapqueue-pages-by-queue-", csv.Content.Headers.ContentDisposition!.FileName);
        Assert.Contains($"{TestServer.QueueId},{TestServer.QueueName},1,3,", await csv.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("from=yesterday")]
    [InlineData("from=2026-10-31&to=2026-10-01")]
    [InlineData("from=2000-01-01&to=2026-10-01")]
    [InlineData("by=colour")]
    [InlineData("format=pdf")]
    public async Task AReportThatMakesNoSenseIsRefused(string query)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _server.Admin.GetAsync($"/api/v1/admin/reports/usage?{query}")).StatusCode);
    }

    [Fact]
    public void ReportsAreForViewersOfJobs()
    {
        Assert.Equal(new AdminRequirement(AdminArea.Jobs, AdminRole.Viewer), AdminAccess.Requirement("GET", "/reports/usage"));
    }
}
