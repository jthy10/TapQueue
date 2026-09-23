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

/// <summary>What admins see of a printer: the user-facing view plus where it is on the network.</summary>
public sealed record PrinterAdminDto(PrinterDto Printer, string Uri, bool TlsSkipVerify, DateTimeOffset? CheckedAt);

/// <param name="Name">Defaults to the id.</param>
public sealed record CreatePrinterRequest(string Id, string Uri, string? Name = null, string? Location = null, bool? TlsSkipVerify = null);

/// <summary>Fields left null are unchanged.</summary>
public sealed record UpdatePrinterRequest(string? Uri = null, string? Name = null, string? Location = null, bool? TlsSkipVerify = null);

public sealed record QueueAdminDto(
    string Id,
    string Name,
    string Description,
    string Location,
    bool Color,
    bool Duplex,
    string DefaultMedia,
    string IppPath);

public sealed record CreateQueueRequest(
    string Id,
    string Name,
    string? Description = null,
    string? Location = null,
    bool? Color = null,
    bool? Duplex = null,
    string? DefaultMedia = null);

/// <summary>Fields left null are unchanged.</summary>
public sealed record UpdateQueueRequest(
    string? Name = null,
    string? Description = null,
    string? Location = null,
    bool? Color = null,
    bool? Duplex = null,
    string? DefaultMedia = null);

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
    int HeartbeatSeconds,
    ClientBuildDto? ClientBuild = null);

/// <summary>
/// The Windows client build every client should be running. A client whose own exe has a different
/// SHA-256 downloads <see cref="DownloadPath"/> (with its session token) and installs it.
/// </summary>
public sealed record ClientBuildDto(string Version, string Sha256, long SizeBytes, DateTimeOffset PublishedAt, string DownloadPath);

public sealed record ClientHeartbeatResponse(ClientBuildDto? ClientBuild);

/// <summary>What the TapQueue service on each PC needs: the printers to add and the client build to run.</summary>
public sealed record ClientSetupResponse(IReadOnlyList<QueueDto> Queues, ClientBuildDto? ClientBuild);

/// <summary>A signed-in client, for `tapqueue-admin clients`.</summary>
public sealed record ClientSessionDto(string Username, string? Hostname, string? WindowsUser, string? ClientVersion, string RemoteIp, DateTimeOffset LastSeenAt);

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

public sealed record UpdateStationRequest(string PrinterId);

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
