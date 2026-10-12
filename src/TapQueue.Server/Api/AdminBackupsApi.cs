using System.Globalization;
using TapQueue.Server.Backups;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/admin/server/backups: the server's backups, making one now, downloading them and the
/// daily schedule. Full admins only (AdminAccess): a backup holds everything, hashed passwords and
/// the server's private key included.
/// </summary>
public static class AdminBackupsApi
{
    public static void MapBackupsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/server/backups", Status);
        admin.MapPost("/server/backups", (BackupStore backups, EventLog events) =>
        {
            var backup = backups.Create(BackupKind.Manual);
            events.Admin(null, backups.CopyError is { } copyError
                ? $"Made backup {backup.Name}, but couldn't copy it to {backups.CopyTo}: {copyError}"
                : $"Made backup {backup.Name}.");
            return backup;
        });
        admin.MapGet("/server/backups/{name}", (string name, BackupStore backups, EventLog events) =>
        {
            if (backups.FileFor(name) is not { } path)
                return Results.NotFound(new ErrorResponse($"There's no backup \"{name}\"."));
            events.Admin(null, $"Downloaded backup {name}.");
            return Results.File(path, "application/gzip", name);
        });
        admin.MapDelete("/server/backups/{name}", (string name, BackupStore backups, EventLog events) =>
        {
            if (!backups.Delete(name))
                return Results.NotFound(new ErrorResponse($"There's no backup \"{name}\"."));
            events.Admin(null, $"Deleted backup {name}.");
            return Results.NoContent();
        });
        admin.MapPatch("/server/backups/schedule", UpdateSchedule);
    }

    private static BackupsDto Status(BackupStore backups, BackupService service, ServerSettings settings) => new(
        settings.BackupTime?.ToString("HH:mm", CultureInfo.InvariantCulture), settings.BackupKeep, service.NextRunAt(),
        backups.Directory, backups.List(), service.LastError, backups.Encrypts, backups.CopyTo, backups.CopyError, settings.TimeZone.Id);

    private static IResult UpdateSchedule(UpdateBackupScheduleRequest request, BackupStore backups, BackupService service, ServerSettings settings, EventLog events)
    {
        TimeOnly? time = null;
        var off = string.Equals(request.Time, "off", StringComparison.OrdinalIgnoreCase);
        if (request.Time is not null && !off)
        {
            if (!TimeOnly.TryParseExact(request.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return Results.BadRequest(new ErrorResponse("The backup time must look like 02:00 (24-hour clock), or \"off\"."));
            time = parsed;
        }
        if (request.Keep is < 1 or > 365)
            return Results.BadRequest(new ErrorResponse("Keep between 1 and 365 daily backups."));

        var changes = new List<string>();
        if (request.Time is not null && time != settings.BackupTime)
        {
            settings.SetBackupTime(time);
            changes.Add(off ? "daily backup turned off" : $"daily backup at {request.Time}");
        }
        if (request.Keep is { } keep && keep != settings.BackupKeep)
        {
            settings.SetBackupKeep(keep);
            changes.Add($"{keep} daily backups kept");
            // Fewer kept takes effect now, not after tomorrow's backup.
            backups.Prune(keep);
        }
        if (changes.Count > 0)
        {
            events.Admin(null, $"Backups: {string.Join("; ", changes)}.");
            service.Reschedule();
        }
        return Results.Ok(Status(backups, service, settings));
    }
}
