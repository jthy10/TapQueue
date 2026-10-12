using System.Net;
using System.Net.Sockets;
using TapQueue.Server.Admins;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Tls;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/admin/server/startup: the server.toml settings that only take effect at a restart
/// (<see cref="StartupSettings"/>), changed from the console. Full admins only.
/// </summary>
public static class AdminStartupSettingsApi
{
    public static void MapStartupSettingsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/server/startup", (StartupSettings startup) => Status(startup));
        admin.MapPatch("/server/startup", Update);
    }

    private static StartupSettingsDto Status(StartupSettings startup) => new(
        Values(startup.Running), Values(startup.Next()), Values(startup.File), startup.SetHere(), startup.RestartNeeded(),
        AdminServerApi.CanRestart, startup.Running.Server.Listen, startup.Running.Server.DataDir);

    private static StartupValuesDto Values(ServerConfig config) => new(
        config.Server.DiscoveryPort, config.Server.TrustedProxies, config.Auth.Mode, config.Tls.Listen, config.Tls.CertFile,
        config.Tls.KeyFile, config.Tls.Names, config.Tls.Require, config.Backup.CopyTo, config.Backup.Passphrase.Length > 0);

    private static IResult Update(UpdateStartupSettingsRequest request, StartupSettings startup, AdminRoster roster, AdminStore admins, EventLog events)
    {
        var before = startup.Next();
        var next = StartupSettings.Copy(before);
        if (request.DiscoveryPort is { } port) next.Server.DiscoveryPort = port;
        if (request.TrustedProxies is { } proxies) next.Server.TrustedProxies = Clean(proxies);
        if (request.AuthMode is { } mode) next.Auth.Mode = mode.Trim();
        if (request.TlsListen is { } listen) next.Tls.Listen = listen.Trim();
        if (request.TlsCertFile is { } certFile) next.Tls.CertFile = certFile.Trim();
        if (request.TlsKeyFile is { } keyFile) next.Tls.KeyFile = keyFile.Trim();
        if (request.TlsNames is { } names) next.Tls.Names = Clean(names);
        if (request.TlsRequire is { } require) next.Tls.Require = require;
        if (request.BackupCopyTo is { } copyTo) next.Backup.CopyTo = copyTo.Trim();
        if (request.BackupPassphrase is { } passphrase) next.Backup.Passphrase = passphrase;

        foreach (var key in request.Reset ?? [])
        {
            if (!StartupSettings.Keys.Contains(key))
                return Results.BadRequest(new ErrorResponse($"Can't reset \"{key}\"; only {string.Join(", ", StartupSettings.Keys)}."));
            Take(next, startup.File, key);
        }

        try
        {
            next.Validate();
            // The checks the server would otherwise only make as it starts, when the console can't say what's wrong.
            if (next.Tls.Enabled && next.Tls.CertFile.Length > 0 && Differs(next, before, StartupSettings.TlsCertFile, StartupSettings.TlsKeyFile))
                _ = new ServerCertificate(next.Tls, next.Server.DataDir);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        if (next.Tls.Enabled && next.Tls.Listen != startup.Running.Tls.Listen && Differs(next, before, StartupSettings.TlsListen)
            && CantListen(IPEndPoint.Parse(next.Tls.Listen)) is { } why)
            return Results.BadRequest(new ErrorResponse($"This server can't listen on {next.Tls.Listen}: {why}"));
        // The console is open in dev mode; with tokens it asks for a sign-in somebody has to be able to make.
        if (next.Auth.Mode == "token" && before.Auth.Mode != "token" && roster.FullAdmins(admins.Grants()) == 0)
            return Results.Conflict(new ErrorResponse(
                "With tokens the console asks for a sign-in, and nobody could: there's no full admin who can sign in. " +
                "Make someone a full admin under Admins first (with a password, or a domain account)."));

        var changes = StartupSettings.Keys.Where(key => Differs(next, before, key)).Select(key => Describe(next, key)).ToList();
        if (changes.Count > 0)
        {
            startup.Save(next);
            events.Admin(null, $"Startup settings: {string.Join("; ", changes)}. " +
                (startup.RestartNeeded() ? "Used from the next restart." : "The server already runs with these."));
        }
        return Results.Ok(Status(startup));
    }

    private static List<string> Clean(IReadOnlyList<string> values) =>
        values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool Differs(ServerConfig a, ServerConfig b, params string[] keys) =>
        keys.Any(key => StartupSettings.Text(a, key) != StartupSettings.Text(b, key));

    /// <summary>Puts server.toml's value for one setting back in <paramref name="config"/>.</summary>
    private static void Take(ServerConfig config, ServerConfig file, string key)
    {
        switch (key)
        {
            case StartupSettings.DiscoveryPort: config.Server.DiscoveryPort = file.Server.DiscoveryPort; break;
            case StartupSettings.TrustedProxies: config.Server.TrustedProxies = [.. file.Server.TrustedProxies]; break;
            case StartupSettings.AuthMode: config.Auth.Mode = file.Auth.Mode; break;
            case StartupSettings.TlsListen: config.Tls.Listen = file.Tls.Listen; break;
            case StartupSettings.TlsCertFile: config.Tls.CertFile = file.Tls.CertFile; break;
            case StartupSettings.TlsKeyFile: config.Tls.KeyFile = file.Tls.KeyFile; break;
            case StartupSettings.TlsNames: config.Tls.Names = [.. file.Tls.Names]; break;
            case StartupSettings.TlsRequire: config.Tls.Require = file.Tls.Require; break;
            case StartupSettings.BackupCopyTo: config.Backup.CopyTo = file.Backup.CopyTo; break;
            case StartupSettings.BackupPassphrase: config.Backup.Passphrase = file.Backup.Passphrase; break;
        }
    }

    /// <summary>For the activity log. The passphrase itself is never written there.</summary>
    private static string Describe(ServerConfig config, string key) => key switch
    {
        StartupSettings.DiscoveryPort => config.Server.DiscoveryPort == 0 ? "discovery off" : $"discovery on UDP port {config.Server.DiscoveryPort}",
        StartupSettings.TrustedProxies => config.Server.TrustedProxies.Count == 0 ? "no reverse proxies trusted" : $"reverse proxies {string.Join(", ", config.Server.TrustedProxies)}",
        StartupSettings.AuthMode => config.Auth.Mode == "dev" ? "sign-in mode dev (anyone can be anyone, and the console is open)" : "sign-in mode token",
        StartupSettings.TlsListen => config.Tls.Enabled ? $"HTTPS and IPPS on {config.Tls.Listen}" : "HTTPS and IPPS off",
        StartupSettings.TlsCertFile => config.Tls.CertFile.Length == 0 ? "a self-signed certificate" : $"certificate {config.Tls.CertFile}",
        StartupSettings.TlsKeyFile => config.Tls.KeyFile.Length == 0 ? "no private key file" : $"private key {config.Tls.KeyFile}",
        StartupSettings.TlsNames => config.Tls.Names.Count == 0 ? "no extra certificate names" : $"certificate names {string.Join(", ", config.Tls.Names)}",
        StartupSettings.TlsRequire => config.Tls.Require ? "plain HTTP refused from other machines" : "plain HTTP allowed",
        StartupSettings.BackupCopyTo => config.Backup.CopyTo.Length == 0 ? "backups copied nowhere else" : $"backups copied to {config.Backup.CopyTo}",
        StartupSettings.BackupPassphrase => config.Backup.Passphrase.Length == 0 ? "backups not encrypted" : "a new backup passphrase",
        _ => key,
    };

    /// <summary>Why this machine can't listen there (not one of its addresses, a port in use or one it may not take); null if it can.</summary>
    private static string? CantListen(IPEndPoint endpoint)
    {
        try
        {
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(endpoint);
            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => "something else is using that port.",
                SocketError.AddressNotAvailable => "that isn't one of this machine's addresses. 0.0.0.0 listens on all of them.",
                SocketError.AccessDenied => "the server isn't allowed to use that port. Pick one above 1023.",
                _ => ex.Message,
            };
        }
    }
}
