namespace TapQueue.Shared.Api;

public sealed record UserDto(long Id, string Username, string DisplayName);

public sealed record QueueDto(string Id, string Name, string Description, string IppPath);

public sealed record PrinterDto(
    string Id,
    string Name,
    string Location,
    bool Online,
    string? MakeAndModel,
    string? StateMessage);

/// <param name="Owner">The TapQueue user the job belongs to, or null if nobody has been matched to it yet.</param>
/// <param name="ClaimedUser">The username the print client sent. Unverified; only shown to help an admin sort out unowned jobs.</param>
public sealed record JobDto(
    long Id,
    string Name,
    string QueueId,
    string Status,
    long SizeBytes,
    string DocumentFormat,
    int Copies,
    string? Owner,
    string? ClaimedUser,
    DateTimeOffset SubmittedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ReleasedAt,
    string? ReleasedPrinterId,
    string? Error);

/// <summary>Sent by the user client when it starts, and again whenever its session expires.</summary>
public sealed record ClientSessionRequest(
    string Username,
    string? Token,
    string? WindowsUser,
    string? Hostname,
    string? ClientVersion);

public sealed record ClientSessionResponse(
    string SessionToken,
    UserDto User,
    IReadOnlyList<QueueDto> Queues,
    IReadOnlyList<PrinterDto> Printers,
    int HeartbeatSeconds);

/// <summary>Release held jobs to a printer. A null <see cref="JobIds"/> releases every held job.</summary>
public sealed record ReleaseRequest(string PrinterId, IReadOnlyList<long>? JobIds = null);

public sealed record AdminReleaseRequest(string Username, string PrinterId, IReadOnlyList<long>? JobIds = null);

public sealed record ReleaseResult(long JobId, string JobName, bool Success, string? Error);

public sealed record ReleaseResponse(IReadOnlyList<ReleaseResult> Results);

public sealed record CreateUserRequest(string Username, string? DisplayName);

/// <summary>Returned when a user is created or their token is reset. The token is only ever shown here.</summary>
public sealed record UserTokenResponse(UserDto User, string Token);

public sealed record ErrorResponse(string Error);
