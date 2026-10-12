using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Backups;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Backups from the admin API: making, listing, downloading and the daily schedule.</summary>
public sealed class BackupApiTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<BackupsDto> Status() =>
        (await _server.Admin.GetFromJsonAsync<BackupsDto>("/api/v1/admin/server/backups", TapQueueJson.Options))!;

    private async Task<HttpResponseMessage> PatchSchedule(UpdateBackupScheduleRequest request) =>
        await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/backups/schedule", request, TapQueueJson.Options);

    [Fact]
    public async Task ABackupMadeNowCanBeDownloadedRestoredAndDeleted()
    {
        using var alice = await _server.SignInAsync("alice");

        var made = await TestServer.ReadAsync<BackupDto>(await _server.Admin.PostAsync("/api/v1/admin/server/backups", null));
        Assert.Equal(BackupKind.Manual, made.Kind);
        Assert.Equal(made, Assert.Single((await Status()).Backups));

        var dir = Directory.CreateTempSubdirectory("tapqueue-it-restore").FullName;
        try
        {
            var file = Path.Combine(dir, made.Name);
            await File.WriteAllBytesAsync(file, await _server.Admin.GetByteArrayAsync($"/api/v1/admin/server/backups/{made.Name}"));
            var data = Path.Combine(dir, "data");
            BackupRestore.Run(new() { Server = { Listen = "127.0.0.1:0", DataDir = data } }, file);
            var restored = new Database(Path.Combine(data, "tapqueue.db"));
            Assert.NotNull(new UserStore(restored).FindByUsername("alice"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.DeleteAsync($"/api/v1/admin/server/backups/{made.Name}")).StatusCode);
        Assert.Empty((await Status()).Backups);
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Admin.GetAsync($"/api/v1/admin/server/backups/{made.Name}")).StatusCode);
    }

    [Fact]
    public async Task TheDailyBackupIsOnAtTwoUnlessChanged()
    {
        var before = await Status();
        Assert.Equal("02:00", before.Time);
        Assert.Equal(7, before.Keep);
        Assert.NotNull(before.NextRunAt);

        var changed = await TestServer.ReadAsync<BackupsDto>(await PatchSchedule(new("23:30", 14)));
        Assert.Equal("23:30", changed.Time);
        Assert.Equal(14, changed.Keep);
        Assert.Equal(new TimeOnly(23, 30), TimeOnly.FromDateTime(changed.NextRunAt!.Value.LocalDateTime));

        var off = await TestServer.ReadAsync<BackupsDto>(await PatchSchedule(new("off")));
        Assert.Null(off.Time);
        Assert.Null(off.NextRunAt);
        Assert.Equal(14, off.Keep);
    }

    [Theory]
    [InlineData("2am", null)]
    [InlineData("25:00", null)]
    [InlineData(null, 0)]
    [InlineData(null, 366)]
    public async Task AScheduleThatMakesNoSenseIsRefused(string? time, int? keep)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchSchedule(new(time, keep))).StatusCode);
    }

    [Fact]
    public async Task TheScheduledRunMakesADailyBackupAndDeletesOldOnes()
    {
        await TestServer.ReadAsync<BackupsDto>(await PatchSchedule(new(Keep: 1)));
        var service = _server.Service<BackupService>();

        service.RunScheduled();
        service.RunScheduled();

        var backup = Assert.Single((await Status()).Backups);
        Assert.Equal(BackupKind.Daily, backup.Kind);

        await TestServer.ReadAsync<BackupsDto>(await PatchSchedule(new("off")));
        await _server.Admin.DeleteAsync($"/api/v1/admin/server/backups/{backup.Name}");
        service.RunScheduled();
        Assert.Empty((await Status()).Backups);
    }

    [Theory]
    [InlineData("GET", "/server/backups")]
    [InlineData("POST", "/server/backups")]
    [InlineData("GET", "/server/backups/tapqueue-20261001T020000Z-daily.tar.gz")]
    [InlineData("DELETE", "/server/backups/tapqueue-20261001T020000Z-daily.tar.gz")]
    [InlineData("PATCH", "/server/backups/schedule")]
    public void BackupsAreForFullAdminsOnly(string method, string path)
    {
        Assert.Equal(Admins.AdminRequirement.FullAdminOnly, Admins.AdminAccess.Requirement(method, path));
    }

    [Fact]
    public async Task MakingAndDownloadingABackupIsInTheActivityLog()
    {
        var made = await TestServer.ReadAsync<BackupDto>(await _server.Admin.PostAsync("/api/v1/admin/server/backups", null));
        await _server.Admin.GetByteArrayAsync($"/api/v1/admin/server/backups/{made.Name}");

        var events = (await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events", TapQueueJson.Options))!;
        Assert.Contains(events, e => e.Message == $"Made backup {made.Name}.");
        Assert.Contains(events, e => e.Message == $"Downloaded backup {made.Name}.");
    }
}
