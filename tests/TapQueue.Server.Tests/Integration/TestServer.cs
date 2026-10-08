using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TapQueue.Server.Config;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// A real tapqueue-server on a random localhost port with its own data directory and one fake
/// printer ("office"), driven over HTTP and IPP the same way clients, stations and admins use it.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    public const string AdminToken = "test-admin-token";
    public const string QueueId = "secure";
    public const string PrinterId = "office";

    private readonly WebApplication _app;
    private readonly string _dataDir;

    public FakePrinter Printer { get; }
    public Uri BaseUri { get; }

    /// <summary>The HTTPS listener, when started with tls.</summary>
    public Uri? HttpsUri { get; }

    public string QueueUri => $"ipp://{BaseUri.Authority}/ipp/{QueueId}";
    public HttpClient Admin { get; }

    private TestServer(WebApplication app, string dataDir, FakePrinter printer)
    {
        _app = app;
        _dataDir = dataDir;
        Printer = printer;
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(a => new Uri(a)).ToList();
        BaseUri = addresses.First(a => a.Scheme == "http");
        HttpsUri = addresses.FirstOrDefault(a => a.Scheme == "https");
        Admin = NewClient(AdminToken);
    }

    /// <param name="tls">Also listen for HTTPS, with a self-signed certificate (or <paramref name="configureTls"/>'s).</param>
    public static async Task<TestServer> StartAsync(string authMode = "token", int discoveryPort = 0, Action<IServiceCollection>? configureServices = null,
        bool tls = false, Action<TlsSection>? configureTls = null)
    {
        var printer = await FakePrinter.StartAsync();
        var dataDir = Directory.CreateTempSubdirectory("tapqueue-it").FullName;
        var config = new ServerConfig
        {
            Server = { Listen = "127.0.0.1:0", DataDir = dataDir, DiscoveryPort = discoveryPort },
            Auth = { Mode = authMode },
            Admin = { Token = AdminToken },
            Tls = { Listen = tls ? "127.0.0.1:0" : "" },
        };
        configureTls?.Invoke(config.Tls);
        var app = ServerApp.Build(config, configureServices: configureServices);
        await app.StartAsync();
        var server = new TestServer(app, dataDir, printer);
        await ReadAsync<QueueAdminDto>(await server.Admin.PostAsJsonAsync("/api/v1/admin/queues",
            new CreateQueueRequest(QueueId, "Test Secure Print"), TapQueueJson.Options));
        await ReadAsync<PrinterAdminDto>(await server.Admin.PostAsJsonAsync("/api/v1/admin/printers",
            new CreatePrinterRequest(PrinterId, printer.Uri, "Office printer"), TapQueueJson.Options));
        return server;
    }

    public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public HttpClient NewClient(string? bearerToken = null)
    {
        var http = new HttpClient { BaseAddress = BaseUri };
        if (bearerToken is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return http;
    }

    /// <summary>Creates a user and signs their tray client in from this machine. Returns a client with the session token.</summary>
    public async Task<HttpClient> SignInAsync(string username, string? windowsUser = null)
    {
        var created = await ReadAsync<UserTokenResponse>(await Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(username, null), TapQueueJson.Options));
        using var anonymous = NewClient();
        var session = await ReadAsync<ClientSessionResponse>(await anonymous.PostAsJsonAsync("/api/v1/client/session",
            new ClientSessionRequest(username, created.Token, windowsUser ?? username, "TEST-PC", "test"), TapQueueJson.Options));
        return NewClient(session.SessionToken);
    }

    /// <summary>Prints a document to the hold queue the way Windows does, with a single Print-Job.</summary>
    public async Task<IppMessage> PrintAsync(string jobName, byte[] document, string? requestingUser = null, int copies = 1)
    {
        var request = IppMessage.CreateRequest(IppOperation.PrintJob, IppClient.NextRequestId(), QueueUri);
        request.Group(IppTag.OperationAttributes)
            .Add("requesting-user-name", IppValue.Name(requestingUser ?? "someone"))
            .Add("job-name", IppValue.Name(jobName))
            .Add("document-format", IppValue.MimeType("application/pdf"));
        request.Group(IppTag.JobAttributes).Add("copies", IppValue.Integer(copies));
        using var ipp = new IppClient(tlsSkipVerify: false, TimeSpan.FromSeconds(30));
        return await ipp.SendAsync(QueueUri, request, new MemoryStream(document));
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TapQueueJson.Options))!;
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await Printer.DisposeAsync();
        Directory.Delete(_dataDir, recursive: true);
    }
}
