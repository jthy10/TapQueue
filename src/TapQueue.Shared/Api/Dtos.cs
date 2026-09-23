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

/// <param name="CardHint">The last few characters of the card number. The full number isn't stored.</param>
public sealed record BadgeDto(long Id, string Username, string CardHint, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record CreateBadgeRequest(string Username, string Card);

/// <summary>A card that was tapped at a station but isn't linked to anyone yet.</summary>
public sealed record UnknownTapDto(string Card, string StationId, DateTimeOffset At);

public sealed record StationDto(string Id, string PrinterId, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, string? LastIp);

public sealed record CreateStationRequest(string Id, string PrinterId);

/// <summary>Returned when a station is created or its token is reset. The token is only ever shown here.</summary>
public sealed record StationTokenResponse(StationDto Station, string Token);

/// <summary>What a release station gets back when it checks in.</summary>
public sealed record StationInfoResponse(string StationId, PrinterDto Printer);

public sealed record StationTapRequest(string Card);

public static class TapOutcome
{
    /// <summary>Every held job was sent to the printer.</summary>
    public const string Released = "released";
    /// <summary>The badge belongs to someone, but they have nothing waiting.</summary>
    public const string NoJobs = "no-jobs";
    /// <summary>Nobody has this badge.</summary>
    public const string UnknownBadge = "unknown-badge";
    /// <summary>Some or all jobs couldn't be sent. They stay held.</summary>
    public const string Failed = "failed";
}

/// <param name="Outcome">One of <see cref="TapOutcome"/>.</param>
/// <param name="Message">A short sentence suitable for showing at the printer.</param>
public sealed record StationTapResponse(string Outcome, string Message, UserDto? User, IReadOnlyList<ReleaseResult> Results);
