using System.Net;
using TapQueue.Server.Admin;
using TapQueue.Server.Api;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Jobs;
using TapQueue.Server.Printers;
using TapQueue.Server.Users;
using TapQueue.Shared;

namespace TapQueue.Server;

/// <summary>Wires up the server from a loaded config. Program.cs runs it; tests start it on a random port.</summary>
public static class ServerApp
{
    public static WebApplication Build(ServerConfig config, string[]? args = null)
    {
        Directory.CreateDirectory(config.Server.DataDir);
        var database = new Database(Path.Combine(config.Server.DataDir, "tapqueue.db"));
        database.Migrate();

        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(ParseEndpoint(config.Server.Listen));
            kestrel.Limits.MaxRequestBodySize = 512L * 1024 * 1024; // big scanned PDFs
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // One line per event, so the log reads well in journalctl and on the server's screen.
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = TapQueueJson.Options.PropertyNamingPolicy);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(new Spool(config.Server.DataDir));
        builder.Services.AddSingleton(services => new ClientBuildStore(services.GetRequiredService<Database>(), config.Server.DataDir));
        builder.Services.AddSingleton(services => new StationBuildStore(services.GetRequiredService<Database>(), config.Server.DataDir));
        builder.Services.AddSingleton<ServerSettings>();
        builder.Services.AddSingleton<EventLog>();
        builder.Services.AddSingleton<UserStore>();
        builder.Services.AddSingleton<GroupStore>();
        builder.Services.AddSingleton<AccessPolicy>();
        builder.Services.AddSingleton<UserLifecycle>();
        builder.Services.AddSingleton<UserImport>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<BadgeStore>();
        builder.Services.AddSingleton<StationStore>();
        builder.Services.AddSingleton<PrinterStore>();
        builder.Services.AddSingleton<QueueStore>();
        builder.Services.AddSingleton<UnknownTaps>();
        builder.Services.AddSingleton<JobStore>();
        builder.Services.AddSingleton<JobOwnerResolver>();
        builder.Services.AddSingleton<PrinterRegistry>();
        builder.Services.AddSingleton<ReleaseService>();
        builder.Services.AddSingleton<IppPrinterEndpoint>();
        builder.Services.AddHostedService<PrinterMonitor>();
        builder.Services.AddHostedService<JobCleanupService>();

        var app = builder.Build();

        app.MapPost("/ipp/{queueId}", (HttpContext http, string queueId, IppPrinterEndpoint ipp) => ipp.HandleAsync(http, queueId));
        app.MapGet("/", () => "TapQueue server " + TapQueueVersion.Current);
        app.MapGet("/icons/{size:int}.png", (int size) =>
            typeof(ServerApp).Assembly.GetManifestResourceStream($"TapQueue.Server.Assets.icon-{size}.png") is { } png
                ? Results.Stream(png, "image/png")
                : Results.NotFound());
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = TapQueueVersion.Current }));
        app.MapClientApi();
        app.MapAdminApi();
        app.MapStationApi();
        app.MapAdminUi();
        return app;
    }

    public static IPEndPoint ParseEndpoint(string listen)
    {
        if (!IPEndPoint.TryParse(listen, out var endpoint))
            throw new InvalidDataException($"server.listen \"{listen}\" must look like 0.0.0.0:8631.");
        return endpoint;
    }
}
