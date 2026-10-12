namespace TapQueue.Shared.Api;

/// <summary>The server itself, for `tapqueue-admin status` and the admin console.</summary>
/// <param name="ChangedSettings">Settings changed from the console or CLI rather than taken from server.toml.</param>
/// <param name="CanRestart">True when systemd runs the server, so it comes back after a restart.</param>
/// <param name="TapWaitSeconds">How long a station's tap waits for the printer before it answers and reads the next card.</param>
/// <param name="PrinterBusySeconds">How long a printer that says it's busy is retried before a job goes back on hold.</param>
/// <param name="PrinterSendSeconds">How long sending one job to a printer may take.</param>
/// <param name="JobHistoryDays">Finished jobs are deleted after this many days; 0 keeps them for ever.</param>
/// <param name="ActivityDays">The activity log is kept this many days; 0 keeps it for ever.</param>
/// <param name="TimeZone">The time zone days start in (page limits, reports, the daily backup and sync), as an IANA name.</param>
/// <param name="SignInAttempts">Wrong passwords for one name from one address before sign-in is refused there.</param>
/// <param name="SignInLockoutMinutes">How long sign-in stays refused after the last wrong password.</param>
/// <param name="TrustedProxies">server.trusted_proxies: the reverse proxies whose X-Forwarded-For is believed.</param>
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
    string QuotaOverrun = Api.QuotaOverrun.Allow,
    ServerTlsDto? Tls = null,
    string AddressMatching = Api.AddressMatching.On,
    bool EnrollmentCodeRequired = false,
    int HeldMegabytesPerUser = 0,
    int TapWaitSeconds = 0,
    int PrinterBusySeconds = 0,
    int PrinterSendSeconds = 0,
    int JobHistoryDays = 0,
    int ActivityDays = 0,
    string TimeZone = "",
    int SignInAttempts = 0,
    int SignInLockoutMinutes = 0,
    IReadOnlyList<string>? TrustedProxies = null);

/// <summary>A new enrollment code for PCs, shown once: the server keeps only its hash.</summary>
public sealed record EnrollmentCodeResponse(string Code);

/// <summary>What a usage report adds pages up by.</summary>
public static class ReportGrouping
{
    public const string User = "user";
    /// <summary>The groups people are in now. Someone in two groups counts in both.</summary>
    public const string Group = "group";
    public const string Printer = "printer";
    public const string Queue = "queue";
    public const string Day = "day";
    public const string Month = "month";

    public static readonly string[] All = [User, Group, Printer, Queue, Day, Month];
}

/// <summary>Pages printed from <paramref name="From"/> to <paramref name="To"/> (both "yyyy-MM-dd", both included).</summary>
/// <param name="By">One of <see cref="ReportGrouping"/>.</param>
/// <param name="TimeZone">The server's time zone, which decides where days and months start.</param>
/// <param name="Jobs">Released jobs in the period. With <see cref="ReportGrouping.Group"/> the rows can add up to more.</param>
/// <param name="UncountedJobs">Jobs whose pages couldn't be counted; each counts as 1 page a copy.</param>
/// <param name="ColorPages">Of <paramref name="Pages"/>, those sent to print in colour; <paramref name="MonoPages"/> in black and white. The rest didn't say.</param>
/// <param name="DuplexPages">Those sent to print on both sides of the paper.</param>
public sealed record UsageReportDto(
    string From,
    string To,
    string By,
    string TimeZone,
    int Jobs,
    long Pages,
    int UncountedJobs,
    IReadOnlyList<UsageRowDto> Rows,
    long ColorPages = 0,
    long MonoPages = 0,
    long DuplexPages = 0);

/// <param name="Key">The username, group, printer or queue id, or the day ("yyyy-MM-dd") or month ("yyyy-MM"). Empty for "nobody" rows.</param>
/// <param name="ColorPages">Of <paramref name="Pages"/>, those sent to print in colour.</param>
/// <param name="MonoPages">Those sent to print in black and white. The rest didn't say (or are from before 0.16).</param>
/// <param name="DuplexPages">Those sent to print on both sides of the paper.</param>
public sealed record UsageRowDto(string Key, string Name, int Jobs, long Pages, int UncountedJobs, long ColorPages = 0, long MonoPages = 0, long DuplexPages = 0);

/// <summary>Why a backup was made.</summary>
public static class BackupKind
{
    /// <summary>By the daily schedule. Old ones are deleted, keeping the newest few.</summary>
    public const string Daily = "daily";
    /// <summary>By an admin. Kept until an admin deletes it.</summary>
    public const string Manual = "manual";
    /// <summary>By a newer server, before it changed the database for itself.</summary>
    public const string Upgrade = "upgrade";
}

/// <summary>One backup file in data_dir/backups.</summary>
/// <param name="Kind">One of <see cref="BackupKind"/>.</param>
/// <param name="Encrypted">True if it needs the passphrase it was made with to be restored.</param>
/// <param name="Copied">Whether it's in backup.copy_to as well; null when there's no such folder.</param>
public sealed record BackupDto(string Name, string Kind, DateTimeOffset CreatedAt, long SizeBytes, bool Encrypted = false, bool? Copied = null);

/// <summary>The server's backups and when it makes them.</summary>
/// <param name="Time">"HH:mm" on the server's clock for the daily backup; null when it's off.</param>
/// <param name="Keep">How many daily backups are kept.</param>
/// <param name="LastError">Why the last scheduled backup failed, if it did.</param>
/// <param name="Encrypted">True when backup.passphrase is set in server.toml, so new backups are encrypted with it.</param>
/// <param name="CopyTo">backup.copy_to in server.toml: a second folder (another disk, a network share) every backup is copied to, or null.</param>
/// <param name="CopyError">Why the last copy there failed, if it did.</param>
/// <param name="TimeZone">The time zone <paramref name="Time"/> is in.</param>
public sealed record BackupsDto(
    string? Time,
    int Keep,
    DateTimeOffset? NextRunAt,
    string Directory,
    IReadOnlyList<BackupDto> Backups,
    string? LastError = null,
    bool Encrypted = false,
    string? CopyTo = null,
    string? CopyError = null,
    string TimeZone = "");

/// <summary>Null leaves a setting as it is. <paramref name="Time"/> is "HH:mm", or "off" for no daily backup.</summary>
public sealed record UpdateBackupScheduleRequest(string? Time = null, int? Keep = null);

/// <summary>The server's HTTPS listener, so admins can check the fingerprint clients see.</summary>
/// <param name="Fingerprint">SHA-256 of the certificate, "AB:CD:…". What server_cert_fingerprint in client.toml and station.toml expects.</param>
/// <param name="CertFile">The certificate file in use: tls.cert_file, or the self-signed one in data_dir/tls.</param>
public sealed record ServerTlsDto(
    string Listen,
    int Port,
    string Fingerprint,
    bool SelfSigned,
    string CertFile,
    string Subject,
    IReadOnlyList<string> Names,
    DateTimeOffset NotAfter,
    bool Require);

/// <summary>One line of the server's log, for the live log in the console.</summary>
/// <param name="Level">debug, info, warning or error.</param>
public sealed record LogLineDto(long Id, DateTimeOffset At, string Level, string Category, string Message);

/// <summary>
/// Settings that apply while the server runs. Null leaves one as it is; <see cref="Reset"/> names
/// ones (by the names they have here, in camelCase) to take from server.toml, or the default, again.
/// </summary>
/// <param name="QuotaOverrun">One of <see cref="Api.QuotaOverrun"/>.</param>
/// <param name="AddressMatching">One of <see cref="Api.AddressMatching"/>.</param>
/// <param name="HeldMegabytesPerUser">Megabytes of held documents one person may have waiting; 0 for no limit.</param>
/// <param name="JobHistoryDays">0 keeps finished jobs for ever; otherwise at least 32, so monthly page limits can still be counted.</param>
/// <param name="TimeZone">An IANA name like "Europe/Oslo".</param>
public sealed record UpdateServerSettingsRequest(
    int? HoldHours = null,
    int? SessionTimeoutMinutes = null,
    IReadOnlyList<string>? Reset = null,
    string? QuotaOverrun = null,
    string? AddressMatching = null,
    int? HeldMegabytesPerUser = null,
    int? TapWaitSeconds = null,
    int? PrinterBusySeconds = null,
    int? PrinterSendSeconds = null,
    int? JobHistoryDays = null,
    int? ActivityDays = null,
    string? TimeZone = null,
    int? SignInAttempts = null,
    int? SignInLockoutMinutes = null);

/// <summary>
/// Upgrading the server itself to a newer GitHub release: what's running, what's out, and any
/// upgrade that's scheduled, running or just finished.
/// </summary>
/// <param name="Current">The running version without the commit, e.g. "0.8.0".</param>
/// <param name="Latest">The newest server release on GitHub, once checked.</param>
/// <param name="CheckedAt">When GitHub was last asked; null if it hasn't been since the server started.</param>
/// <param name="CheckError">Why the last check failed, if it did.</param>
/// <param name="UpdateAvailable">True when <see cref="Latest"/> is newer than <see cref="Current"/>.</param>
/// <param name="CanApply">True when this server can install an update by itself (installed by install-server.sh, run by systemd).</param>
/// <param name="CannotApplyReason">Why not, and what to run instead.</param>
/// <param name="Scheduled">An upgrade waiting for its time.</param>
/// <param name="LastRun">The most recent upgrade the updater ran, or the one running now.</param>
public sealed record ServerUpdateDto(
    string Current,
    ServerReleaseDto? Latest,
    DateTimeOffset? CheckedAt,
    string? CheckError,
    bool UpdateAvailable,
    bool CanApply,
    string? CannotApplyReason,
    ScheduledUpdateDto? Scheduled,
    UpdateRunDto? LastRun);

/// <summary>A server release on GitHub.</summary>
/// <param name="Version">e.g. "0.9.0"; the tag is server-v{Version}.</param>
/// <param name="Url">The release page, with its notes.</param>
public sealed record ServerReleaseDto(string Version, string Url, DateTimeOffset? PublishedAt);

/// <summary>
/// Client or station builds against the newest release on GitHub: what "Check for updates" on the
/// console's Updates page shows.
/// </summary>
/// <param name="Latest">The newest release the last check found; null before the first check.</param>
/// <param name="Published">The version this server hands out now: one per client platform, or the station's. Empty if none.</param>
/// <param name="UpdateAvailable">True when <see cref="Latest"/> is newer than what's handed out (or nothing is).</param>
public sealed record BuildReleaseDto(
    ServerReleaseDto? Latest,
    DateTimeOffset? CheckedAt,
    string? CheckError,
    bool UpdateAvailable,
    IReadOnlyList<string> Published);

/// <summary>Publish the builds of release <see cref="Version"/>, which must be the one the last check found.</summary>
public sealed record PublishReleaseRequest(string Version);

public sealed record ScheduledUpdateDto(string Version, DateTimeOffset At, string? By);

/// <param name="State">running, done or failed.</param>
public sealed record UpdateRunDto(string State, string Version, DateTimeOffset? At, string Message);

/// <summary>Upgrade to <see cref="Version"/> now, or at <see cref="At"/> if it's given.</summary>
public sealed record ApplyServerUpdateRequest(string Version, DateTimeOffset? At = null);

/// <summary>What happens when a job would take someone past their page limit.</summary>
public static class QuotaOverrun
{
    /// <summary>A job prints in full if they're under the limit when it starts. The default.</summary>
    public const string Allow = "allow";
    /// <summary>Only jobs that fit in the pages they have left print.</summary>
    public const string Deny = "deny";

    public static readonly string[] All = [Allow, Deny];
}

/// <summary>
/// Whether a job that arrives without a workstation key (sent to a printer added by a client older
/// than 0.8, or straight to the server) may still be matched to whoever is signed in at the address
/// it came from. Jobs sent through a keyed printer are always matched by the PC they came from instead.
/// </summary>
public static class AddressMatching
{
    /// <summary>Keyless jobs go to the client signed in at their address, as before 0.8. The default, while PCs upgrade.</summary>
    public const string On = "on";
    /// <summary>Keyless jobs aren't matched to anyone. For once every PC has a key.</summary>
    public const string Off = "off";

    public static readonly string[] All = [On, Off];
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
/// <param name="Source">"local" for users managed in TapQueue, "ad" for users synced from Active Directory.</param>
/// <param name="Quota">Their own page limit, which overrides their groups'.</param>
/// <param name="DisabledBy">"admin" or "directory" when they're disabled.</param>
/// <param name="DirectoryState">Why AD sync disabled them: "disabled", "expired" or "missing" (out of the sync's scope).</param>
/// <param name="HasPassword">A local user with an admin console password.</param>
public sealed record UserAdminDto(
    long Id,
    string Username,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    IReadOnlyList<string> Groups,
    string Source = "local",
    QuotaDto? Quota = null,
    string? DisabledBy = null,
    string? DirectoryState = null,
    bool HasPassword = false);

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

/// <param name="Via">For Active Directory groups, the nested group they're a member through; null if direct.</param>
public sealed record GroupMemberDto(string Username, string? Via);

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

/// <summary>What admins see of a printer: the user-facing view plus where it is on the network and how it's doing.</summary>
public sealed record PrinterAdminDto(PrinterDto Printer, string Uri, bool TlsSkipVerify, DateTimeOffset? CheckedAt, PrinterHealthDto? Health = null);

public static class PrinterHealthLevel
{
    /// <summary>Ready, nothing wrong.</summary>
    public const string Ok = "ok";
    /// <summary>Printing, but something needs attention soon (toner low, paper low).</summary>
    public const string Warning = "warning";
    /// <summary>Reachable, but reports a problem (paper jam, door open) or has stopped.</summary>
    public const string Error = "error";
    /// <summary>Didn't answer.</summary>
    public const string Offline = "offline";
}

/// <param name="Level">One of <see cref="PrinterHealthLevel"/>.</param>
/// <param name="State">"idle", "printing" or "stopped", or null when the printer didn't answer.</param>
/// <param name="CanPrint">False when jobs aren't released to it: unreachable, stopped or not accepting jobs.</param>
/// <param name="Problems">What's wrong, in words: "Paper jam", "Black cartridge low (5%)".</param>
/// <param name="Since">When <paramref name="Level"/> last changed, as far as this server has seen.</param>
public sealed record PrinterHealthDto(
    string Level,
    string? State,
    bool CanPrint,
    IReadOnlyList<string> Problems,
    IReadOnlyList<PrinterSupplyDto> Supplies,
    DateTimeOffset? Since);

/// <param name="Type">IPP marker type: "toner-cartridge", "ink-cartridge", "waste-toner"…</param>
/// <param name="Color">A CSS colour like "#000000", or null.</param>
/// <param name="Level">Percent left, or null when the printer doesn't know.</param>
/// <param name="Low">At or below the printer's own "low" level.</param>
public sealed record PrinterSupplyDto(string Name, string? Type, string? Color, int? Level, bool Low);

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
/// <param name="RefundedPages">Of a released job's pages times copies: how many were given back because it was stopped at the printer.</param>
/// <param name="Color">True if it was sent to print in colour, false in black and white; null if the job didn't say (jobs from before 0.16 too).</param>
/// <param name="Duplex">True if it was sent to print on both sides of the paper; null for jobs from before 0.16.</param>
/// <param name="PrintState">
/// For a released job whose printer can be asked about it (servers from 0.17): "printing" until the
/// printer finishes it, then "printed", "stopped" (part way) or "unconfirmed" (the printer never said).
/// </param>
/// <param name="PrintedPages">What the printer said it printed, for printers that say.</param>
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
    int? Pages = null,
    int RefundedPages = 0,
    bool? Color = null,
    bool? Duplex = null,
    string? PrintState = null,
    int? PrintedPages = null);

/// <summary>What deleting one of your own jobs did (servers from 0.14; older ones answer with nothing).</summary>
/// <param name="RefundedPages">Pages that had counted against a page limit and no longer do.</param>
/// <param name="Message">What happened, for the person who asked.</param>
public sealed record CancelJobResponse(int RefundedPages, string Message);

/// <summary>How people sign in to the tray app, set with the Active Directory settings.</summary>
public static class ClientSignIn
{
    /// <summary>As the person signed in to the PC, without asking. The default.</summary>
    public const string Pc = "pc";
    /// <summary>With their domain account's name and password, from the tray's "Sign in as…". Remembered until they sign out.</summary>
    public const string Domain = "domain";

    public static readonly string[] All = [Pc, Domain];
}

/// <summary>What the tray app asks before signing in: whether it needs a domain account (<see cref="ClientSignIn"/>).</summary>
public sealed record ClientSignInInfoDto(string Mode);

/// <summary>Sent by the user client when it starts, and again whenever its session expires.</summary>
/// <param name="WindowsUser">The user signed in to the PC (Windows or Linux).</param>
/// <param name="Platform">A <see cref="ClientPlatform"/>; clients older than 0.5 don't send it (they're all Windows).</param>
/// <param name="Password">With <see cref="ClientSignIn.Domain"/>: the domain account's password, checked against AD. Never stored.</param>
/// <param name="RememberToken">With <see cref="ClientSignIn.Domain"/>: the token a password sign-in handed out, instead of the password.</param>
public sealed record ClientSessionRequest(
    string Username,
    string? Token,
    string? WindowsUser,
    string? Hostname,
    string? ClientVersion,
    string? Platform = null,
    string? Password = null,
    string? RememberToken = null);

/// <param name="RememberToken">After a password sign-in: keep this and sign in with it until the person signs out.</param>
public sealed record ClientSessionResponse(
    string SessionToken,
    UserDto User,
    IReadOnlyList<QueueDto> Queues,
    IReadOnlyList<PrinterDto> Printers,
    int HeartbeatSeconds,
    ClientBuildDto? ClientBuild = null,
    string? RememberToken = null);

/// <summary>Signing out from the tray: ends the session and forgets <paramref name="RememberToken"/> on the server.</summary>
public sealed record ClientSignOutRequest(string? RememberToken);

/// <summary>
/// The client build every PC of a <see cref="Platform"/> should be running. A PC whose own program
/// has a different SHA-256 downloads <see cref="DownloadPath"/> and installs it.
/// </summary>
/// <param name="Signature">
/// Whoever built it's signature of the program (<see cref="ReleaseSignature"/>), if it was published
/// with one. Clients from 0.11 install nothing without one they trust.
/// </param>
public sealed record ClientBuildDto(string Version, string Sha256, long SizeBytes, DateTimeOffset PublishedAt, string DownloadPath,
    string Platform = ClientPlatform.Windows, string? Signature = null);

/// <summary>The release station build every station should be running. Downloaded with the station token.</summary>
/// <param name="Signature">As for <see cref="ClientBuildDto"/>; stations from 0.17 need one they trust.</param>
public sealed record StationBuildDto(string Version, string Sha256, long SizeBytes, DateTimeOffset PublishedAt, string DownloadPath,
    string? Signature = null);

public sealed record ClientHeartbeatResponse(ClientBuildDto? ClientBuild);

/// <summary>What the TapQueue service on each PC needs: the printers to add and the client build to run.</summary>
/// <param name="Command">One of <see cref="WorkstationCommand"/>, sent once.</param>
/// <param name="WorkstationKey">
/// A new key for this PC, handed out once, the first time a service that asks for one checks in. The
/// queues' IPP paths then carry the key, so jobs printed through them are known to come from this PC.
/// </param>
public sealed record ClientSetupResponse(IReadOnlyList<QueueDto> Queues, ClientBuildDto? ClientBuild, string? Command = null,
    string? WorkstationKey = null);

/// <summary>The TapQueue service checking in: which PC it is, what it runs, and why its last update failed.</summary>
/// <param name="Platform">A <see cref="ClientPlatform"/>; services older than 0.5 don't send it (they're all Windows).</param>
/// <param name="WorkstationKey">
/// The key the server gave this PC, or "" from a service (0.8 on) that has none yet and wants one.
/// Null from older services, which don't know about keys.
/// </param>
/// <param name="EnrollmentCode">
/// enrollment_code from client.toml, sent by a PC that has no key yet. A server that asks for one
/// (`tapqueue-admin server enrollment-code new`) only lets new PCs in with it.
/// </param>
public sealed record ClientSetupRequest(string Computer, string? Version, string? Sha256, string? UpdateError, string? Platform = null,
    string? WorkstationKey = null, string? EnrollmentCode = null);

/// <summary>
/// The TapQueue service vouching for a tray app on its PC: the tray app with session
/// <paramref name="SessionToken"/> runs as the PC user <paramref name="PcUser"/>, which the service
/// checked from the operating system rather than taking the tray app's word for it. Jobs printed
/// through this PC's keyed printers by that PC user then go to that session's TapQueue user.
/// </summary>
public sealed record WorkstationSessionRequest(string Computer, string WorkstationKey, string SessionToken, string PcUser);

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
    IReadOnlyList<ClientSessionDto> Sessions,
    string Platform = ClientPlatform.Windows,
    bool HasKey = false);

/// <summary>A signed-in client, for `tapqueue-admin clients`.</summary>
/// <param name="Id">The session, for signing it out.</param>
/// <param name="Verified">The PC's TapQueue service vouched for it (<see cref="WorkstationSessionRequest"/>), so jobs from that PC are matched to it by key, not address.</param>
public sealed record ClientSessionDto(long Id, string Username, string? Hostname, string? WindowsUser, string? ClientVersion, string RemoteIp, DateTimeOffset LastSeenAt,
    bool Verified = false);

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
/// <param name="Source">"local" if an admin linked the card, "ad" if the directory sync did.</param>
public sealed record BadgeDto(long Id, string Username, string CardHint, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, string Label = "",
    string Source = "local");

public sealed record CreateBadgeRequest(string Username, string Card, string? Label = null);

/// <summary>Moves a card to another user and/or changes its label. Fields left null are unchanged.</summary>
public sealed record UpdateBadgeRequest(string? Username = null, string? Label = null);

/// <summary>A card that was tapped at a station but isn't linked to anyone yet.</summary>
public sealed record UnknownTapDto(string Card, string StationId, DateTimeOffset At);

/// <summary>A station as the card enrollment dialog lists it: somewhere a card can be tapped.</summary>
/// <param name="Online">It sent a heartbeat lately, so a tap there will reach the server.</param>
/// <param name="Enabled">False when it's out of service; its taps are ignored then.</param>
public sealed record BadgeStationDto(string Id, string Name, string Location, bool Online, bool Enabled);

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
    /// <summary>
    /// The jobs are on their way but the printer hasn't taken them all yet (a big one, or it's busy).
    /// The server carries on by itself; any that fail go back on hold and into the activity log.
    /// </summary>
    public const string Releasing = "releasing";
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

/// <summary>
/// A client program that crashed, sent by it (or, if it couldn't be sent then, the next time it starts).
/// </summary>
/// <param name="Program">What crashed: "service" (the TapQueue service) or "tray" (the tray app).</param>
/// <param name="Message">The error, one line.</param>
/// <param name="Details">The full exception with its stack trace.</param>
public sealed record CrashReportRequest(
    string Computer,
    string Program,
    string? Platform,
    string? Version,
    DateTimeOffset OccurredAt,
    string Message,
    string? Details);

public sealed record CrashReportDto(
    long Id,
    string Computer,
    string Ip,
    string Program,
    string Platform,
    string? Version,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string Message,
    string? Details);

public static class CrashProgram
{
    public const string Service = "service";
    public const string Tray = "tray";
}

/// <summary>
/// Active Directory sync settings. The bind password is never sent back; <paramref name="HasPassword"/> says
/// whether one is saved.
/// </summary>
/// <param name="CaCertificate">PEM of the CA that issued the domain controllers' certificates; empty to use the server's trusted CAs.</param>
/// <param name="BadgeAttribute">The user attribute with their card number (like employeeNumber); empty to manage cards in TapQueue.</param>
/// <param name="SyncTime">When the daily sync runs, HH:mm in the server's time zone.</param>
/// <param name="MaxDisablePercent">A sync that would disable more than this share of AD users stops instead, in case AD or the scope is wrong.</param>
/// <param name="ClientSignIn">How people sign in to the tray app: <see cref="Api.ClientSignIn"/>.</param>
public sealed record DirectoryConfigDto(
    bool Enabled,
    string Host,
    int Port,
    string BindDn,
    bool HasPassword,
    string CaCertificate,
    string BadgeAttribute,
    string SyncTime,
    int MaxDisablePercent,
    string ClientSignIn = Api.ClientSignIn.Pc);

/// <summary>Fields left null are unchanged. An empty password is ignored, so the console can leave it blank.</summary>
public sealed record UpdateDirectoryConfigRequest(
    bool? Enabled = null,
    string? Host = null,
    int? Port = null,
    string? BindDn = null,
    string? Password = null,
    string? CaCertificate = null,
    string? BadgeAttribute = null,
    string? SyncTime = null,
    int? MaxDisablePercent = null,
    string? ClientSignIn = null);

/// <summary>What the sync pulls users from.</summary>
public static class DirectoryScopeKind
{
    /// <summary>Every user anywhere under the OU.</summary>
    public const string OrganizationalUnit = "ou";
    /// <summary>Every member of the group, through nested groups too. The group also becomes a TapQueue group.</summary>
    public const string Group = "group";
    /// <summary>One user.</summary>
    public const string User = "user";

    public static readonly string[] All = [OrganizationalUnit, Group, User];
}

public sealed record DirectoryScopeDto(long Id, string Kind, string Guid, string Dn, string Name, DateTimeOffset AddedAt);

/// <summary>Adds an OU, group or user to the scope, by its objectGUID (from a search) or its DN.</summary>
public sealed record AddDirectoryScopeRequest(string? Guid = null, string? Dn = null);

/// <summary>An OU, group or user found in AD.</summary>
/// <param name="Kind">One of <see cref="DirectoryScopeKind"/>.</param>
/// <param name="Username">sAMAccountName, for users and groups.</param>
public sealed record DirectoryObjectDto(string Kind, string Guid, string Dn, string Name, string? Username, string? DisplayName, bool InScope);

public static class DirectoryRunOutcome
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    /// <summary>AD couldn't be read (unreachable, refused, scope object gone). Nothing changed.</summary>
    public const string Failed = "failed";
    /// <summary>Too many users would have been disabled, so nothing changed. Force a sync to go ahead.</summary>
    public const string Stopped = "stopped";
}

/// <param name="Trigger">"schedule" or "admin".</param>
/// <param name="DryRun">A preview: nothing was changed.</param>
/// <param name="Summary">What it did, one line.</param>
public sealed record DirectoryRunDto(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Trigger,
    bool DryRun,
    string Outcome,
    string Summary,
    string? Error);

/// <param name="DryRun">Only work out what would change.</param>
/// <param name="Force">Go ahead even if it would disable more users than the settings allow.</param>
public sealed record DirectorySyncRequest(bool DryRun = false, bool Force = false);

/// <summary>One thing a sync changes (or would, in a preview).</summary>
/// <param name="Action">One of <see cref="DirectoryChangeAction"/>.</param>
/// <param name="Subject">Who or what it's about: a username or group id.</param>
public sealed record DirectoryChangeDto(string Action, string Subject, string Description);

public static class DirectoryChangeAction
{
    public const string CreateUser = "create-user";
    /// <summary>A local user with the same username is handed to AD, keeping their cards and history.</summary>
    public const string LinkUser = "link-user";
    public const string UpdateUser = "update-user";
    public const string DisableUser = "disable-user";
    public const string EnableUser = "enable-user";
    public const string CreateGroup = "create-group";
    public const string UpdateGroup = "update-group";
    public const string DeleteGroup = "delete-group";
    public const string Members = "members";
    public const string Badge = "badge";
}

/// <param name="Applied">False for a preview, a failed sync or a stopped one: nothing changed.</param>
/// <param name="Users">Users found in the scope.</param>
/// <param name="Groups">Groups found in the scope.</param>
/// <param name="Warnings">Things skipped, like a username already taken by a different AD account.</param>
public sealed record DirectorySyncResultDto(
    long RunId,
    bool Applied,
    string Outcome,
    string Summary,
    string? Error,
    int Users,
    int Groups,
    IReadOnlyList<DirectoryChangeDto> Changes,
    IReadOnlyList<string> Warnings);

/// <param name="NextRunAt">When the daily sync runs next; null if it's off.</param>
public sealed record DirectoryStatusDto(
    DirectoryConfigDto Config,
    IReadOnlyList<DirectoryScopeDto> Scope,
    IReadOnlyList<DirectoryRunDto> Runs,
    DateTimeOffset? NextRunAt,
    bool Running);

/// <param name="NamingContext">The domain's root, like DC=lab,DC=example,DC=org.</param>
public sealed record DirectoryTestResultDto(bool Success, string Message, string? NamingContext);

/// <summary>
/// The parts of the admin console (and admin API) a role is given for. See docs/admin-roles.md.
/// </summary>
public static class AdminArea
{
    /// <summary>Held jobs and history: release, cancel.</summary>
    public const string Jobs = "jobs";
    /// <summary>Users, groups, cards and page limits.</summary>
    public const string People = "people";
    /// <summary>Active Directory: connection, scope, sync.</summary>
    public const string Directory = "directory";
    /// <summary>Printers, queues, stations and workstations.</summary>
    public const string Fleet = "fleet";
    /// <summary>Client and station builds.</summary>
    public const string Updates = "updates";
    /// <summary>Server settings, log, restart and crash reports.</summary>
    public const string Server = "server";
    /// <summary>In a grant: every area.</summary>
    public const string All = "*";

    public static readonly IReadOnlyList<string> Each = [Jobs, People, Directory, Fleet, Updates, Server];
}

/// <summary>What a grant lets someone do in an area. Each includes the ones before it.</summary>
public static class AdminRole
{
    /// <summary>Look, change nothing.</summary>
    public const string Viewer = "viewer";
    /// <summary>Day-to-day work: release and cancel jobs, cards, restart stations, sign PCs out, run a sync.</summary>
    public const string Operator = "operator";
    /// <summary>Everything, including settings. Admin in every area is a full admin, who also manages admins.</summary>
    public const string Admin = "admin";

    public static readonly IReadOnlyList<string> All = [Viewer, Operator, Admin];

    /// <summary>0 for no role, then 1 to 3 in the order above.</summary>
    public static int Rank(string? role) => role switch { Viewer => 1, Operator => 2, Admin => 3, _ => 0 };
}

/// <param name="Username">Set for a grant to one user; <paramref name="GroupId"/> for a grant to a group's members.</param>
/// <param name="Area">One of <see cref="AdminArea"/>, or <see cref="AdminArea.All"/>.</param>
public sealed record AdminGrantDto(long Id, string? Username, string? GroupId, string? GroupName, string Area, string Role, DateTimeOffset CreatedAt);

/// <summary>Gives a user or a group a role in some areas, replacing the role they had there.</summary>
/// <param name="Areas">Null or empty for every area.</param>
public sealed record GrantAdminRequest(string? Username, string? GroupId, string Role, IReadOnlyList<string>? Areas = null);

/// <summary>Someone who can use the admin console, with the roles their own grants and groups add up to.</summary>
/// <param name="Roles">Area to role, for the areas they have one in.</param>
/// <param name="Via">Where the roles come from: "direct" and/or group names.</param>
/// <param name="SignInProblem">Why they can't sign in right now (no password, disabled, AD not set up); null if they can.</param>
public sealed record AdminPersonDto(
    string Username,
    string DisplayName,
    string Source,
    IReadOnlyDictionary<string, string> Roles,
    bool FullAdmin,
    IReadOnlyList<string> Via,
    string? SignInProblem);

/// <param name="FullAdmins">People who are full admins and can sign in: with none, only dev mode or admin.token can manage the server.</param>
public sealed record AdminsDto(IReadOnlyList<AdminGrantDto> Grants, IReadOnlyList<AdminPersonDto> People, int FullAdmins);

public sealed record AdminSignInRequest(string Username, string Password);

/// <summary>Who is using the console, and what they may do.</summary>
/// <param name="Username">Null when nobody is signed in: dev mode's open console, or the admin token.</param>
/// <param name="Roles">Area to role for every area they have one in.</param>
/// <param name="HasPassword">A local user with a TapQueue password, which they can change.</param>
public sealed record AdminMeDto(
    string AuthMode,
    string? Username,
    string? DisplayName,
    IReadOnlyDictionary<string, string> Roles,
    bool FullAdmin,
    bool HasPassword);

/// <summary>Sets a local user's admin console password, or with null removes it.</summary>
public sealed record SetPasswordRequest(string? Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>How the connection to the mail server is protected.</summary>
public static class MailSecurity
{
    /// <summary>Starts plain and upgrades with STARTTLS before signing in (usually port 587). Refused if the server doesn't offer it.</summary>
    public const string StartTls = "starttls";
    /// <summary>TLS from the first byte (usually port 465).</summary>
    public const string Tls = "tls";
    /// <summary>No encryption: only for a relay on the same machine or a trusted network (usually port 25).</summary>
    public const string None = "none";

    public static readonly string[] All = [StartTls, Tls, None];
}

/// <summary>The mail server alerts are sent through and what's worth an email. The password is never sent back.</summary>
/// <param name="Security">One of <see cref="MailSecurity"/>.</param>
/// <param name="Username">Empty for a relay that doesn't ask anyone to sign in.</param>
/// <param name="From">The address alerts come from; most providers only accept one that belongs to the account.</param>
/// <param name="PrinterProblems">A printer is unreachable or has stopped (paper jam, out of paper).</param>
/// <param name="PrinterWarnings">A printer still prints but wants attention soon (toner low).</param>
/// <param name="DelayMinutes">How long a printer or station must stay that way before an email goes out.</param>
public sealed record NotificationConfigDto(
    bool Enabled,
    string Host,
    int Port,
    string Security,
    bool TlsSkipVerify,
    string Username,
    bool HasPassword,
    string From,
    string FromName,
    IReadOnlyList<string> Recipients,
    bool PrinterProblems,
    bool PrinterWarnings,
    bool StationsOffline,
    bool CrashReports,
    int DelayMinutes);

/// <summary>Fields left null are unchanged. An empty password is ignored, so the console can leave it blank.</summary>
public sealed record UpdateNotificationConfigRequest(
    bool? Enabled = null,
    string? Host = null,
    int? Port = null,
    string? Security = null,
    bool? TlsSkipVerify = null,
    string? Username = null,
    string? Password = null,
    string? From = null,
    string? FromName = null,
    IReadOnlyList<string>? Recipients = null,
    bool? PrinterProblems = null,
    bool? PrinterWarnings = null,
    bool? StationsOffline = null,
    bool? CrashReports = null,
    int? DelayMinutes = null);

/// <summary>Something an email went out about that hasn't recovered yet.</summary>
/// <param name="Subject">Like "printer:m404n" or "station:office", as in the activity log.</param>
public sealed record OpenAlertDto(string Subject, string Summary, DateTimeOffset Since);

/// <param name="LastSentAt">When an email last went out, since the server started.</param>
/// <param name="LastError">Why the last email couldn't be sent; null once one goes through.</param>
/// <param name="Waiting">Emails that couldn't be sent yet and will be tried again.</param>
public sealed record NotificationsDto(
    NotificationConfigDto Config,
    IReadOnlyList<OpenAlertDto> Alerts,
    DateTimeOffset? LastSentAt,
    string? LastError,
    int Waiting);

public sealed record NotificationTestResultDto(bool Success, string Message);
