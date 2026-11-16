using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Admins;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// Cost accounting: what a page costs at each printer, a job's cost worked out when it's released,
/// and (when balances are enforced) people paying for what they print. Amounts are in mills.
/// </summary>
public sealed class AccountingTests : IAsyncLifetime
{
    private static readonly byte[] ThreePages = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pdf"));
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<AccountingSettingsDto> Set(UpdateAccountingRequest request) =>
        await TestServer.ReadAsync<AccountingSettingsDto>(await _server.Admin.PatchAsJsonAsync("/api/v1/admin/accounting", request, TapQueueJson.Options));

    /// <summary>5 cents a page in black and white, 20 in colour, a quarter off for two-sided.</summary>
    private Task<AccountingSettingsDto> Prices(string mode) =>
        Set(new UpdateAccountingRequest(mode, "$", PriceMono: 50, PriceColor: 200, DuplexDiscountPercent: 25));

    private async Task<UserAccountDto> Account(string username) =>
        (await _server.Admin.GetFromJsonAsync<UserAccountDto>($"/api/v1/admin/users/{username}/account", TapQueueJson.Options))!;

    private async Task<UserAccountDto> TopUp(string username, long amount, bool set = false) =>
        await TestServer.ReadAsync<UserAccountDto>(await _server.Admin.PostAsJsonAsync($"/api/v1/admin/users/{username}/balance",
            new AdjustBalanceRequest(amount, "at the front desk", set), TapQueueJson.Options));

    private static async Task<ReleaseResult> ReleaseOne(HttpClient user) =>
        Assert.Single((await TestServer.ReadAsync<ReleaseResponse>(await user.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options))).Results);

    private static async Task<JobDto> OnlyJob(HttpClient user) =>
        Assert.Single((await user.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs?all=true", TapQueueJson.Options))!);

    [Fact]
    public async Task ItIsOffUntilTurnedOnAndThenJobsHaveACost()
    {
        using var alice = await _server.SignInAsync("alice");
        var settings = await _server.Admin.GetFromJsonAsync<AccountingSettingsDto>("/api/v1/admin/accounting", TapQueueJson.Options);
        Assert.Equal(AccountingMode.Off, settings!.Mode);
        await _server.PrintAsync("free", ThreePages);
        Assert.Null((await ReleaseOne(alice)).Cost);

        await Prices(AccountingMode.Track);
        await _server.PrintAsync("report", ThreePages, copies: 2);
        var released = await ReleaseOne(alice);

        Assert.True(released.Success);
        Assert.Equal(6 * 50, released.Cost);
        Assert.Contains((await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs?all=true", TapQueueJson.Options))!, j => j.Name == "report" && j.Cost == 300);
        // Tracking only records what things cost: nobody pays, and nobody is refused.
        var account = await Account("alice");
        Assert.Equal(0, account.Balance);
        Assert.Empty(account.Transactions);

        var month = DateTimeOffset.UtcNow;
        var report = await _server.Admin.GetFromJsonAsync<UsageReportDto>(
            $"/api/v1/admin/reports/usage?from={month.AddDays(-1):yyyy-MM-dd}&to={month.AddDays(1):yyyy-MM-dd}&by=user", TapQueueJson.Options);
        Assert.Equal(300, report!.Cost);
        Assert.Equal("$", report.Currency);
        Assert.Equal(300, Assert.Single(report.Rows).Cost);
    }

    [Fact]
    public async Task ColourCostsMoreTwoSidedCostsLessAndAPrinterCanHaveItsOwnPrices()
    {
        using var alice = await _server.SignInAsync("alice");
        await Prices(AccountingMode.Track);
        _server.Printer.Color = true;
        _server.Printer.Sides = ["one-sided", "two-sided-long-edge"];
        var printers = _server.Service<Printers.PrinterRegistry>();
        await printers.ProbeAsync(printers.Find(TestServer.PrinterId)!, CancellationToken.None);

        await _server.PrintAsync("brochure", ThreePages, jobAttributes: a => a
            .Add("print-color-mode", IppValue.Keyword("color")).Add("sides", IppValue.Keyword("two-sided-long-edge")));
        Assert.Equal(3 * 200 * 75 / 100, (await ReleaseOne(alice)).Cost);

        // This printer charges its own price for black and white, and the server's for colour.
        var changed = await TestServer.ReadAsync<PrinterAdminDto>(await _server.Admin.PatchAsJsonAsync($"/api/v1/admin/printers/{TestServer.PrinterId}",
            new UpdatePrinterRequest(PriceMono: 35), TapQueueJson.Options));
        Assert.Equal(35, changed.PriceMono);
        Assert.Null(changed.PriceColor);
        await _server.PrintAsync("memo", ThreePages);
        Assert.Equal(3 * 35, (await ReleaseOne(alice)).Cost);

        // -1 hands the price back to the server.
        changed = await TestServer.ReadAsync<PrinterAdminDto>(await _server.Admin.PatchAsJsonAsync($"/api/v1/admin/printers/{TestServer.PrinterId}",
            new UpdatePrinterRequest(PriceMono: -1), TapQueueJson.Options));
        Assert.Null(changed.PriceMono);
        await _server.PrintAsync("memo 2", ThreePages);
        Assert.Equal(3 * 50, (await ReleaseOne(alice)).Cost);
    }

    [Fact]
    public async Task WithBalancesEnforcedAJobSomeoneCantPayForStaysHeld()
    {
        using var alice = await _server.SignInAsync("alice");
        await Prices(AccountingMode.Enforce);
        await _server.PrintAsync("report", ThreePages);

        var refused = await ReleaseOne(alice);
        Assert.False(refused.Success);
        Assert.Contains("costs $0.15 and you have $0.00", refused.Error);
        Assert.Equal(JobStatus.Held, (await OnlyJob(alice)).Status);
        Assert.Empty(_server.Printer.Jobs);

        Assert.Equal(1000, (await TopUp("alice", 1000)).Balance);
        var paid = await ReleaseOne(alice);
        Assert.True(paid.Success);
        Assert.Equal(150, paid.Cost);

        var account = await Account("alice");
        Assert.Equal(850, account.Balance);
        Assert.Equal([TransactionKind.Charge, TransactionKind.Adjust], account.Transactions.Select(t => t.Kind));
        Assert.Equal(-150, account.Transactions[0].Amount);
        Assert.Equal(850, account.Transactions[0].BalanceAfter);
        Assert.Contains("report", account.Transactions[0].Note);
        Assert.Equal("at the front desk", account.Transactions[1].Note);

        // They see the same from their PC, with what a page costs where they can print.
        var mine = await alice.GetFromJsonAsync<MyAccountDto>("/api/v1/me/account", TapQueueJson.Options);
        Assert.Equal(AccountingMode.Enforce, mine!.Mode);
        Assert.Equal(850, mine.Balance);
        Assert.Equal(2, mine.Transactions.Count);
        Assert.Equal(new PrinterPriceDto(TestServer.PrinterId, "Office printer", 50, 200), Assert.Single(mine.Prices));
    }

    [Fact]
    public async Task SomeoneUnrestrictedPrintsIntoTheRedAndABalanceCanBeSet()
    {
        using var staff = await _server.SignInAsync("staff");
        await Prices(AccountingMode.Enforce);
        await TestServer.ReadAsync<UserAccountDto>(await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/staff/unrestricted", new SetUnrestrictedRequest(true), TapQueueJson.Options));
        await _server.PrintAsync("minutes", ThreePages);

        Assert.True((await ReleaseOne(staff)).Success);
        var account = await Account("staff");
        Assert.True(account.Unrestricted);
        Assert.Equal(-150, account.Balance);

        // Setting a balance records the difference it made.
        account = await TopUp("staff", 5000, set: true);
        Assert.Equal(5000, account.Balance);
        Assert.Equal(5150, account.Transactions[0].Amount);
    }

    [Fact]
    public async Task AJobStoppedBeforeItPrintsIsPaidBackAndOneStoppedPartWayInPart()
    {
        _server.Printer.NewJobState = IppJobState.Pending;
        using var alice = await _server.SignInAsync("alice");
        await Prices(AccountingMode.Enforce);
        await TopUp("alice", 1000);
        await _server.PrintAsync("report", ThreePages, copies: 2);
        Assert.Equal(300, (await ReleaseOne(alice)).Cost);
        Assert.Equal(700, (await Account("alice")).Balance);

        var stopped = await TestServer.ReadAsync<CancelJobResponse>(await alice.DeleteAsync($"/api/v1/me/jobs/{(await OnlyJob(alice)).Id}"));
        Assert.Equal(6, stopped.RefundedPages);
        Assert.Contains("$0.30 is back on your balance", stopped.Message);
        var account = await Account("alice");
        Assert.Equal(1000, account.Balance);
        Assert.Equal(TransactionKind.Refund, account.Transactions[0].Kind);

        // Part way: four of six pages didn't print, so two thirds of the money comes back.
        _server.Printer.NewJobState = IppJobState.Processing;
        _server.Printer.ImpressionsCompleted = 2;
        await _server.PrintAsync("second", ThreePages, copies: 2);
        var results = (await TestServer.ReadAsync<ReleaseResponse>(await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options))).Results;
        var second = Assert.Single(results, r => r.Success);
        await TestServer.ReadAsync<CancelJobResponse>(await alice.DeleteAsync($"/api/v1/me/jobs/{second.JobId}"));
        Assert.Equal(1000 - 300 + 200, (await Account("alice")).Balance);
        var job = (await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs?all=true", TapQueueJson.Options))!.Single(j => j.Id == second.JobId);
        Assert.Equal(300, job.Cost);
        Assert.Equal(200, job.RefundedCost);
    }

    [Fact]
    public async Task NewUsersStartWithTheInitialBalanceAndAGroupCanBeToppedUpAtOnce()
    {
        await Set(new UpdateAccountingRequest(AccountingMode.Enforce, InitialBalance: 2500));
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", null), TapQueueJson.Options);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("bob", null), TapQueueJson.Options);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("carol", null), TapQueueJson.Options);
        Assert.Equal(2500, (await Account("alice")).Balance);

        await TestServer.ReadAsync<GroupDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/groups", new CreateGroupRequest("students", "Students"), TapQueueJson.Options));
        foreach (var member in new[] { "alice", "bob" })
            (await _server.Admin.PutAsync($"/api/v1/admin/groups/students/members/{member}", null)).EnsureSuccessStatusCode();
        var reached = await TestServer.ReadAsync<GroupBalanceResponse>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/groups/students/balance",
            new AdjustBalanceRequest(5000, "autumn term"), TapQueueJson.Options));

        Assert.Equal(2, reached.Users);
        Assert.Equal(7500, (await Account("bob")).Balance);
        Assert.Equal("autumn term", (await Account("bob")).Transactions[0].Note);
        Assert.Equal(2500, (await Account("carol")).Balance);
        var users = await _server.Admin.GetFromJsonAsync<List<UserAdminDto>>("/api/v1/admin/users", TapQueueJson.Options);
        Assert.Equal(7500, users!.Single(u => u.Username == "alice").Balance);
    }

    [Fact]
    public async Task ATapSaysWhatItCostAndWhatIsLeft()
    {
        using var alice = await _server.SignInAsync("alice");
        await Prices(AccountingMode.Enforce);
        await TopUp("alice", 1000);
        var station = await TestServer.ReadAsync<StationTokenResponse>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/stations",
            new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "1111"), TapQueueJson.Options);
        await _server.PrintAsync("report", ThreePages);

        using var reader = _server.NewClient(station.Token);
        var tap = await TestServer.ReadAsync<StationTapResponse>(await reader.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest("1111"), TapQueueJson.Options));

        Assert.Equal(TapOutcome.Released, tap.Outcome);
        Assert.Contains("That's $0.15; you have $0.85 left.", tap.Message);
    }

    [Fact]
    public async Task WhatIsntAPriceOrAnAmountIsRefused()
    {
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", null), TapQueueJson.Options);
        Task<HttpResponseMessage> Patch(UpdateAccountingRequest request) => _server.Admin.PatchAsJsonAsync("/api/v1/admin/accounting", request, TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Mode: "prepaid"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(PriceMono: -5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(DuplexDiscountPercent: 101))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Currency: "12"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _server.Admin.PostAsJsonAsync("/api/v1/admin/users/alice/balance", new AdjustBalanceRequest(0), TapQueueJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Admin.PostAsJsonAsync("/api/v1/admin/users/nobody/balance", new AdjustBalanceRequest(100), TapQueueJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _server.Admin.PatchAsJsonAsync($"/api/v1/admin/printers/{TestServer.PrinterId}",
            new UpdatePrinterRequest(PriceColor: -7), TapQueueJson.Options)).StatusCode);
    }

    [Fact]
    public void ToppingUpIsOperatorWorkAndPricesAreForAdmins()
    {
        Assert.Equal(new AdminRequirement(AdminArea.People, AdminRole.Operator), AdminAccess.Requirement("POST", "/users/alice/balance"));
        Assert.Equal(new AdminRequirement(AdminArea.People, AdminRole.Admin), AdminAccess.Requirement("PATCH", "/accounting"));
        Assert.Equal(new AdminRequirement(AdminArea.People, AdminRole.Admin), AdminAccess.Requirement("POST", "/groups/students/balance"));
        Assert.Equal(new AdminRequirement(AdminArea.People, AdminRole.Admin), AdminAccess.Requirement("PUT", "/users/alice/unrestricted"));
        Assert.Equal(new AdminRequirement(AdminArea.People, AdminRole.Viewer), AdminAccess.Requirement("GET", "/accounts"));
    }

    [Theory]
    [InlineData(50, "$", "$0.05")]
    [InlineData(12500, "$", "$12.50")]
    [InlineData(35, "€", "€0.035")]
    [InlineData(-150, "$", "-$0.15")]
    [InlineData(12500, "kr", "12.50 kr")]
    [InlineData(1000, "", "1.00")]
    public void MoneyIsWrittenWithItsCurrency(long mills, string currency, string expected) => Assert.Equal(expected, Money.Format(mills, currency));

    [Theory]
    [InlineData("1.25", 1250)]
    [InlineData("0.035", 35)]
    [InlineData("-2", -2000)]
    [InlineData(".5", 500)]
    public void AmountsAreReadAsMills(string text, long expected)
    {
        Assert.True(Money.TryParse(text, out var mills));
        Assert.Equal(expected, mills);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a lot")]
    [InlineData("0.0001")]
    [InlineData("1,25")]
    [InlineData("99999999999")]
    public void WhatIsntAnAmountIsntRead(string text) => Assert.False(Money.TryParse(text, out _));
}
