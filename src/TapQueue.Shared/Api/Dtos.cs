namespace TapQueue.Shared.Api;

/// <summary>The server itself, for `tapqueue-admin status` and the admin console.</summary>
/// <param name="ChangedSettings">Settings changed from the console or CLI rather than taken from server.toml.</param>
/// <param name="CanRestart">True when systemd runs the server, so it comes back after a restart.</param>
public sealed record ServerInfoDto(
    string Version,
    DateTimeOffset StartedAt,
    string AuthMode,
    string Listen,
    string DataDir,
    int SchemaVersion,
    int HoldHours,
    int SessionTimeoutMinutes,
    int HeldJobs,
    IReadOnlyList<string>? ChangedSettings = null,
    bool CanRestart = false,
    string QuotaOverrun = Api.QuotaOverrun.Allow);

/// <summary>One line of the server's log, for the live log in the console.</summary>
/// <param name="Level">debug, info, warning or error.</param>
public sealed record LogLineDto(long Id, DateTimeOffset At, string Level, string Category, string Message);

/// <summary>
/// Settings that apply while the server runs. Null leaves one as it is; <see cref="Reset"/> names
/// ones (holdHours, sessionTimeoutMinutes, quotaOverrun) to take from server.toml, or the default, again.
/// </summary>
/// <param name="QuotaOverrun">One of <see cref="Api.QuotaOverrun"/>.</param>
public sealed record UpdateServerSettingsRequest(
    int? HoldHours = null,
    int? SessionTimeoutMinutes = null,
    IReadOnlyList<string>? Reset = null,
    string? QuotaOverrun = null);

/// <summary>What happens when a job would take someone past their page limit.</summary>
public static class QuotaOverrun
{
    /// <summary>A job prints in full if they're under the limit when it starts. The default.</summary>
    public const string Allow = "allow";
    /// <summary>Only jobs that fit in the pages they have left print.</summary>
    public const string Deny = "deny";

    public static readonly string[] All = [Allow, Deny];
}

/// <summary>How often a page limit starts over: at midnight, on Monday, or on the 1st, in the server's time zone.</summary>
public static class QuotaPeriod
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static readonly string[] All = [Day, Week, Month];
}

/// <summary>One line of the activity log.</summary>
/// <param name="Category">admin, job, tap or signin.</param>
/// <param name="Actor">Who did it: "admin", a username, "station:&lt;id&gt;" or "system".</param>
/// <param name="Subject">What it's about, like "user:alice" or "printer:m404n".</param>
public sealed record EventDto(long Id, DateTimeOffset At, string Category, string Actor, string? Subject, string Message);

public sealed record UserDto(long Id, string Username, string DisplayName);

/// <summary>What admins see of a user. A disabled user can't sign in, print or release.</summary>
/// <param name="Groups">Ids of the groups they're in.</param>
/// <param name="Source">"local" for users managed in TapQueue; later, the directory that syncs them.</param>
/// <param name="Quota">Their own page limit, which overrides their groups'.</param>
public sealed record UserAdminDto(
    long Id,
    string Username,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    IReadOnlyList<string> Groups,
    string Source = "local",
    QuotaDto? Quota = null);

/// <summary>A page limit: at most <paramref name="Pages"/> pages per <paramref name="Period"/> (one of <see cref="QuotaPeriod"/>).</summary>
public sealed record QuotaDto(int Pages, string Period);

/// <summary>A limit that applies to a user, and how much of it they've used this period.</summary>
/// <param name="Source">"user" for their own limit, or "group:&lt;id&gt;".</param>
public sealed record QuotaUsageDto(
    int Pages,
    string Period,
    string Source,
    int Used,
    int Remaining,
    DateTimeOffset PeriodStart,
    DateTimeOffset ResetsAt);

/// <summary>
/// A user's page limits. <paramref name="Applies"/> is their own limit if they have one, otherwise
/// their groups' (a job prints if any of them allows it); empty means unlimited.
/// </summary>
public sealed record UserQuotaDto(string Username, QuotaDto? Own, IReadOnlyList<QuotaUsageDto> Applies);

/// <summary>Do one thing to several users at once.</summary>
/// <param name="Action">One of <see cref="BulkUserAction"/>.</param>
/// <param name="GroupId">For add-to-group and remove-from-group.</param>
public sealed record BulkUsersRequest(IReadOnlyList<string> Usernames, string Action, string? GroupId = null);

public static class BulkUserAction
{
    public const string Disable = "disable";
    public const string Enable = "enable";
    public const string Delete = "delete";
    public const string AddToGroup = "add-to-group";
    public const string RemoveFromGroup = "remove-from-group";
}

/// <param name="Errors">Users that couldn't be changed, and why. The rest were.</param>
public sealed record BulkUsersResponse(int Changed, IReadOnlyList<string> Errors);

public static class ImportAction
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Unchanged = "unchanged";
    public const string Error = "error";
}

/// <summary>What one CSV row does (or would do, in a preview).</summary>
/// <param name="Row">The row number in the file, counting the header as 1.</param>
public sealed record ImportRowDto(int Row, string Username, string Action, IReadOnlyList<string> Changes, string? Error);

/// <param name="Applied">False for a preview: nothing was changed.</param>
public sealed record ImportResponse(bool Applied, int Creates, int Updates, int Errors, IReadOnlyList<ImportRowDto> Rows);

/// <summary>
/// A group of users and what they may use. Users in no group may use everything; users in groups may
/// use whatever any of their groups allows.
/// </summary>
public sealed record GroupDto(
    string Id,
    string Name,
    string Description,
    bool AllQueues,
    IReadOnlyList<string> QueueIds,
    bool AllPrinters,
    IReadOnlyList<string> PrinterIds,
    int MemberCount,
    string Source = "local",
    QuotaDto? Quota = null);

/// <param name="AllQueues">Defaults to true, so a new group doesn't take anything away until it's restricted.</param>
public sealed record CreateGroupRequest(
    string Id,
    string Name,
    string? Description = null,
    bool? AllQueues = null,
    IReadOnlyList<string>? QueueIds = null,
    bool? AllPrinters = null,
    IReadOnlyList<string>? PrinterIds = null);

/// <summary>Fields left null are unchanged. QueueIds/PrinterIds replace the whole list.</summary>
public sealed record UpdateGroupRequest(
    string? Name = null,
    string? Description = null,
    bool? AllQueues = null,
    IReadOnlyList<string>? QueueIds = null,
    bool? AllPrinters = null,
    IReadOnlyList<string>? PrinterIds = null);

/// <summary>Fields left null are unchanged.</summary>
public sealed record UpdateUserRequest(string? DisplayName = null, bool? Disabled = null);

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
/// <param name="FormerOwner">For jobs of a user who has since been deleted, their username.</param>
/// <param name="Pages">Pages per copy (after any page range), or null if they couldn't be counted.</param>
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
    string? Error,
    string? FormerOwner = null,
    int? Pages = null);

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

/// <summary>The release station build every station should be running. Downloaded with the station token.</summary>
public sealed record StationBuildDto(string Version, string Sha256, long SizeBytes, DateTimeOffset PublishedAt, string DownloadPath);

public sealed record ClientHeartbeatResponse(ClientBuildDto? ClientBuild);

/// <summary>What the TapQueue service on each PC needs: the printers to add and the client build to run.</summary>
/// <param name="Command">One of <see cref="WorkstationCommand"/>, sent once.</param>
public sealed record ClientSetupResponse(IReadOnlyList<QueueDto> Queues, ClientBuildDto? ClientBuild, string? Command = null);

/// <summary>The TapQueue service checking in: which PC it is, what it runs, and why its last update failed.</summary>
public sealed record ClientSetupRequest(string Computer, string? Version, string? Sha256, string? UpdateError);

public static class WorkstationCommand
{
    /// <summary>Check for a client build now, and try again even if installing it failed before.</summary>
    public const string Update = "update";
}

public static class WorkstationStatus
{
    /// <summary>The TapQueue service checks in this often.</summary>
    public const int CheckInSeconds = 60;

    /// <summary>A PC that hasn't checked in for this long is shown as offline.</summary>
    public const int OfflineAfterSeconds = 3 * CheckInSeconds;
}

/// <summary>A PC running the TapQueue service, and who's signed in on it.</summary>
/// <param name="Online">Its TapQueue service checked in in the last <c>WorkstationStatus.OfflineAfterSeconds</c> seconds.</param>
/// <param name="UpToDate">It runs the client build the server publishes (true when none is published).</param>
/// <param name="UpdateError">Why installing the published build failed, until it succeeds.</param>
public sealed record WorkstationDto(
    string Hostname,
    string LastIp,
    string? Version,
    bool UpToDate,
    string? UpdateError,
    string? PendingCommand,
    bool Online,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<ClientSessionDto> Sessions);

/// <summary>A signed-in client, for `tapqueue-admin clients`.</summary>
/// <param name="Id">The session, for signing it out.</param>
public sealed record ClientSessionDto(long Id, string Username, string? Hostname, string? WindowsUser, string? ClientVersion, string RemoteIp, DateTimeOffset LastSeenAt);

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
/// <param name="Label">The admin's note to tell cards apart, e.g. "spare" or "blue fob".</param>
public sealed record BadgeDto(long Id, string Username, string CardHint, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, string Label = "");

public sealed record CreateBadgeRequest(string Username, string Card, string? Label = null);

/// <summary>Moves a card to another user and/or changes its label. Fields left null are unchanged.</summary>
public sealed record UpdateBadgeRequest(string? Username = null, string? Label = null);

/// <summary>A card that was tapped at a station but isn't linked to anyone yet.</summary>
public sealed record UnknownTapDto(string Card, string StationId, DateTimeOffset At);

/// <param name="LastSeenAt">Its last request of any kind (heartbeat, check-in, tap).</param>
/// <param name="Online">It sent a heartbeat in the last <c>StationStatus.OfflineAfterSeconds</c> seconds.</param>
/// <param name="ReaderStatus">"ok", or what's wrong with the badge reader, as the station last reported.</param>
public sealed record StationDto(
    string Id,
    string PrinterId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    string? LastIp,
    string Name = "",
    string Location = "",
    bool Online = false,
    string? Version = null,
    DateTimeOffset? StartedAt = null,
    string? ReaderStatus = null,
    DateTimeOffset? LastHeartbeatAt = null,
    StationSettingsDto? Settings = null,
    string? PendingCommand = null);

/// <summary>
/// Settings the server holds for a station. Null reader/device/repeat/min-length mean "whatever
/// station.toml says"; the station reports what it actually uses in its heartbeat.
/// </summary>
/// <param name="Version">Goes up with every change, so a station knows when to re-apply them.</param>
/// <param name="Feedback">How the station tells people what happened. Only "none" so far; reserved for a screen or status light.</param>
/// <param name="Enabled">A disabled station answers taps with <paramref name="MaintenanceMessage"/> and releases nothing.</param>
public sealed record StationSettingsDto(
    int Version,
    string PrinterId,
    string? Reader,
    string? Device,
    int? RepeatSeconds,
    int? MinCardLength,
    string Feedback,
    bool Enabled,
    string MaintenanceMessage);

public static class StationStatus
{
    /// <summary>Stations send a heartbeat this often.</summary>
    public const int HeartbeatSeconds = 15;
    /// <summary>A station that hasn't sent one for this long shows as offline.</summary>
    public const int OfflineAfterSeconds = 45;
}

public static class StationCommand
{
    /// <summary>Exit, and let systemd start the station again (it re-reads everything).</summary>
    public const string Restart = "restart";
}

/// <param name="Reader">The reader the station is actually using: "keyboard" or "pcprox".</param>
/// <param name="ReaderStatus">"ok", or what's wrong (unplugged, no permission…).</param>
/// <param name="Sha256">Of the station's own program file, to tell whether it needs the published build.</param>
/// <param name="SettingsVersion">The settings version it has applied.</param>
public sealed record StationHeartbeatRequest(
    string Version,
    DateTimeOffset StartedAt,
    string Reader,
    string ReaderStatus,
    string? Sha256,
    int SettingsVersion);

/// <param name="Command">One of <see cref="StationCommand"/>, sent once.</param>
/// <param name="Build">The build the station should be running, if one has been published.</param>
public sealed record StationHeartbeatResponse(StationSettingsDto Settings, string? Command, StationBuildDto? Build);

public sealed record CreateStationRequest(string Id, string PrinterId);

/// <summary>
/// Fields left null are unchanged. <paramref name="Reset"/> names settings to hand back to station.toml:
/// "reader", "device", "repeatSeconds", "minCardLength".
/// </summary>
public sealed record UpdateStationRequest(
    string? PrinterId = null,
    string? Name = null,
    string? Location = null,
    bool? Enabled = null,
    string? MaintenanceMessage = null,
    string? Reader = null,
    string? Device = null,
    int? RepeatSeconds = null,
    int? MinCardLength = null,
    string? Feedback = null,
    IReadOnlyList<string>? Reset = null);

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
    /// <summary>The badge's owner isn't allowed to release at this station's printer (their groups).</summary>
    public const string NotAllowed = "not-allowed";
    /// <summary>The station is disabled (out of service).</summary>
    public const string StationDisabled = "station-disabled";
    /// <summary>The badge's owner is disabled.</summary>
    public const string Disabled = "disabled";
    /// <summary>Some or all jobs couldn't be sent. They stay held.</summary>
    public const string Failed = "failed";
}

/// <param name="Outcome">One of <see cref="TapOutcome"/>.</param>
/// <param name="Message">A short sentence suitable for showing at the printer.</param>
public sealed record StationTapResponse(string Outcome, string Message, UserDto? User, IReadOnlyList<ReleaseResult> Results);
