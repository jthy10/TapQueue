using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Jobs;
using TapQueue.Server.Printers;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>Endpoints used by tapqueue-admin. Authenticated with admin.token from server.toml.</summary>
public static class AdminApi
{
    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").AddEndpointFilter(RequireAdmin);

        admin.MapGet("/users", (UserStore users) => users.List().Select(u => u.ToDto()));
        admin.MapPost("/users", CreateUser);
        admin.MapPost("/users/{username}/token", ResetToken);
        admin.MapGet("/jobs", (JobStore jobs, string? status) => jobs.List(status).Select(j => j.ToDto()));
        admin.MapGet("/printers", async (PrinterRegistry printers, bool? refresh, CancellationToken ct) =>
        {
            if (refresh == true)
                await Task.WhenAll(printers.All.Select(p => printers.ProbeAsync(p, ct)));
            return printers.All.Select(printers.ToDto);
        });
        admin.MapPost("/release", Release);
    }

    private static IResult CreateUser(CreateUserRequest request, UserStore users)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username))
            return Results.BadRequest(new ErrorResponse("username is required"));
        if (users.FindByUsername(username) is not null)
            return Results.Conflict(new ErrorResponse($"User \"{username}\" already exists."));

        var token = Tokens.New();
        var user = users.Create(username, string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(), Tokens.Hash(token));
        return Results.Ok(new UserTokenResponse(user.ToDto(), token));
    }

    private static IResult ResetToken(string username, UserStore users)
    {
        var user = users.FindByUsername(username);
        if (user is null)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        var token = Tokens.New();
        users.SetTokenHash(user.Id, Tokens.Hash(token));
        return Results.Ok(new UserTokenResponse(user.ToDto(), token));
    }

    private static async Task<IResult> Release(AdminReleaseRequest request, UserStore users, PrinterRegistry printers,
        ReleaseService release, CancellationToken ct)
    {
        var user = users.FindByUsername(request.Username);
        if (user is null)
            return Results.NotFound(new ErrorResponse($"No user \"{request.Username}\"."));
        var printer = printers.Find(request.PrinterId);
        if (printer is null)
            return Results.NotFound(new ErrorResponse($"Unknown printer \"{request.PrinterId}\"."));
        return Results.Ok(await release.ReleaseAsync(user, printer, request.JobIds, ct));
    }

    private static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var expected = http.RequestServices.GetRequiredService<ServerConfig>().Admin.Token;
        var token = ClientApi.BearerToken(http);
        if (token is null || !Tokens.FixedTimeEquals(token, expected))
            return Results.Json(new ErrorResponse("Admin token required."), statusCode: StatusCodes.Status401Unauthorized);
        return await next(context);
    }
}
