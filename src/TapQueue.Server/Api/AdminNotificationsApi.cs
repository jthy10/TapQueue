using TapQueue.Server.Admins;
using TapQueue.Server.Data;
using TapQueue.Server.Notifications;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/admin/server/notifications: the mail server alerts are sent through, who gets them and
/// what for, and sending a test email. Part of the Server area.
/// </summary>
public static class AdminNotificationsApi
{
    private const int MaxRecipients = 20;

    public static void MapNotificationsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/server/notifications", Status);
        admin.MapPatch("/server/notifications", Update);
        admin.MapPost("/server/notifications/test", Test);
    }

    private static NotificationsDto Status(NotificationStore store, NotificationService service) =>
        new(store.Config().ToDto(), service.OpenAlerts(), service.LastSentAt, service.LastError, service.Waiting);

    private static IResult Update(UpdateNotificationConfigRequest request, NotificationStore store, NotificationService service, EventLog events)
    {
        if (request.Port is < 1 or > 65535)
            return Results.BadRequest(new ErrorResponse("port must be 1 to 65535 (587 with STARTTLS, 465 with TLS)."));
        if (request.Security is { } security && !MailSecurity.All.Contains(security))
            return Results.BadRequest(new ErrorResponse($"security must be one of: {string.Join(", ", MailSecurity.All)}."));
        if (request.DelayMinutes is < 0 or > 1440)
            return Results.BadRequest(new ErrorResponse("delayMinutes must be 0 to 1440 (a day)."));
        if (request.Host is { } host && (host.Trim().Length > 255 || host.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '/')))
            return Results.BadRequest(new ErrorResponse("host must be the mail server's name or address, like smtp.example.org."));
        if (request.From is { Length: > 0 } from && !MailMessage.IsValidAddress(from.Trim()))
            return Results.BadRequest(new ErrorResponse($"\"{from}\" isn't an email address."));
        if (request.Recipients?.FirstOrDefault(r => !MailMessage.IsValidAddress(r?.Trim())) is { } bad)
            return Results.BadRequest(new ErrorResponse($"\"{bad}\" isn't an email address."));
        if (request.Recipients is { Count: > MaxRecipients })
            return Results.BadRequest(new ErrorResponse($"At most {MaxRecipients} recipients; use a mailing list for more."));
        if (new[] { request.Username, request.Password, request.FromName }.Any(v => v is not null && (v.Length > 500 || v.Any(char.IsControl))))
            return Results.BadRequest(new ErrorResponse("The username, password and sender name can't contain line breaks or be longer than 500 characters."));

        var old = store.Config();
        var config = old with
        {
            Enabled = request.Enabled ?? old.Enabled,
            Host = request.Host?.Trim() ?? old.Host,
            Port = request.Port ?? old.Port,
            Security = request.Security ?? old.Security,
            TlsSkipVerify = request.TlsSkipVerify ?? old.TlsSkipVerify,
            Username = request.Username?.Trim() ?? old.Username,
            Password = string.IsNullOrEmpty(request.Password) ? old.Password : request.Password,
            From = request.From?.Trim() ?? old.From,
            FromName = request.FromName?.Trim() ?? old.FromName,
            Recipients = request.Recipients?.Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? old.To,
            PrinterProblems = request.PrinterProblems ?? old.PrinterProblems,
            PrinterWarnings = request.PrinterWarnings ?? old.PrinterWarnings,
            StationsOffline = request.StationsOffline ?? old.StationsOffline,
            CrashReports = request.CrashReports ?? old.CrashReports,
            DelayMinutes = request.DelayMinutes ?? old.DelayMinutes,
        };
        // A relay that asks nobody to sign in has no use for a password.
        if (config.Username.Length == 0)
            config = config with { Password = "" };
        // The saved password goes to whatever server these name, and without TLS (or without checking
        // its certificate) to whoever is in between. So changing them takes the password again.
        var elsewhere = config.Host != old.Host || config.Port != old.Port || config.Security != old.Security || config.TlsSkipVerify != old.TlsSkipVerify;
        if (elsewhere && config.Password.Length > 0 && config.Password == old.Password && string.IsNullOrEmpty(request.Password))
            return Results.BadRequest(new ErrorResponse("Enter the mail account's password again to change the mail server, its port or how the connection is protected."));
        if (config.Enabled && !config.CanSend)
            return Results.BadRequest(new ErrorResponse("Set the mail server, the address to send from and at least one recipient before turning notifications on."));

        var changes = new List<string>();
        if (config.Enabled != old.Enabled) changes.Add(config.Enabled ? "turned on" : "turned off");
        if (config.Host != old.Host || config.Port != old.Port || config.Security != old.Security)
            changes.Add($"mail server {config.Host}:{config.Port} ({Describe(config.Security)})");
        if (config.TlsSkipVerify != old.TlsSkipVerify) changes.Add(config.TlsSkipVerify ? "accepts any certificate from it" : "checks its certificate");
        if (config.Username != old.Username) changes.Add(config.Username.Length == 0 ? "no sign-in" : $"signs in as {config.Username}");
        if (config.Password != old.Password && config.Password.Length > 0) changes.Add("new password");
        if (config.From != old.From || config.FromName != old.FromName) changes.Add($"sent from {config.FromName} <{config.From}>".Replace("  ", " "));
        if (!config.To.SequenceEqual(old.To)) changes.Add(config.To.Length == 0 ? "no recipients" : $"sent to {string.Join(", ", config.To)}");
        if (config.PrinterProblems != old.PrinterProblems) changes.Add(OnOff("printer problems", config.PrinterProblems));
        if (config.PrinterWarnings != old.PrinterWarnings) changes.Add(OnOff("printer warnings", config.PrinterWarnings));
        if (config.StationsOffline != old.StationsOffline) changes.Add(OnOff("stations going offline", config.StationsOffline));
        if (config.CrashReports != old.CrashReports) changes.Add(OnOff("crash reports", config.CrashReports));
        if (config.DelayMinutes != old.DelayMinutes)
            changes.Add(config.DelayMinutes == 0 ? "sent straight away" : $"sent after {config.DelayMinutes} minute{(config.DelayMinutes == 1 ? "" : "s")}");
        if (changes.Count > 0)
        {
            store.SaveConfig(config);
            events.Admin(null, $"Notifications: {string.Join("; ", changes)}.");
        }
        return Results.Ok(Status(store, service));

        static string OnOff(string what, bool on) => $"{what} {(on ? "on" : "off")}";
    }

    /// <summary>Sends an email to everyone on the list now, whether or not notifications are on.</summary>
    private static async Task<NotificationTestResultDto> Test(NotificationStore store, NotificationService service, IMailSender mail, HttpContext http, EventLog events)
    {
        var config = store.Config();
        var message = new MailMessage("TapQueue: test email",
            $"This is a test from the TapQueue server on {Environment.MachineName}, sent by {http.Caller().Actor}.\n\n" +
            "If you can read it, the mail settings work. This address gets an email when something in the list on the Notifications page happens.\n\n" +
            NotificationService.Footer);
        try
        {
            await mail.SendAsync(config, message, http.RequestAborted);
        }
        catch (MailException ex)
        {
            service.Sent(DateTimeOffset.UtcNow, ex.Message);
            return new NotificationTestResultDto(false, ex.Message);
        }
        service.Sent(DateTimeOffset.UtcNow, null);
        events.Admin(null, $"Sent a test email to {string.Join(", ", config.To)}.");
        return new NotificationTestResultDto(true, $"Sent to {string.Join(", ", config.To)}. Check the inbox (and the spam folder).");
    }

    private static string Describe(string security) => security switch
    {
        MailSecurity.Tls => "TLS",
        MailSecurity.None => "no encryption",
        _ => "STARTTLS",
    };
}
