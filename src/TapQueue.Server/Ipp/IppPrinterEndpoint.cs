using TapQueue.Server.Data;
using TapQueue.Server.Jobs;

namespace TapQueue.Server.Ipp;

/// <summary>
/// Makes each configured queue look like an IPP printer. Jobs sent to it are spooled and held
/// until their owner releases them at a physical printer.
/// </summary>
public sealed class IppPrinterEndpoint(
    ServerSettings settings,
    QueueStore queues,
    JobStore jobs,
    Spool spool,
    JobOwnerResolver owners,
    UserStore users,
    AccessPolicy access,
    EventLog events,
    ILogger<IppPrinterEndpoint> logger)
{
    public async Task HandleAsync(HttpContext http, string queueId)
    {
        var queue = queues.Get(queueId);
        if (queue is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!string.Equals(http.Request.ContentType, "application/ipp", StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Expected Content-Type: application/ipp");
            return;
        }

        var body = new BufferedStream(http.Request.Body, 64 * 1024);
        IppMessage request;
        try
        {
            request = await IppReader.ReadAsync(body, http.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            logger.LogWarning("Malformed IPP request from {Ip}: {Message}", http.ClientIp(), ex.Message);
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var context = new RequestContext(http, queue, request, body, $"ipp://{http.Request.Host}/ipp/{queue.Id}");
        IppMessage response;
        try
        {
            response = request.VersionMajor is < 1 or > 2
                ? IppMessage.CreateResponse(request, IppStatus.ServerErrorVersionNotSupported)
                : Validate(request) ?? await DispatchAsync(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "IPP operation 0x{Op:X4} failed", request.Code);
            response = IppMessage.CreateResponse(request, IppStatus.ServerErrorInternal, "Internal server error");
        }

        logger.LogDebug("IPP op 0x{Op:X4} from {Ip} -> 0x{Status:X4}", request.Code, context.ClientIp, response.Code);
        http.Response.ContentType = "application/ipp";
        await http.Response.Body.WriteAsync(IppWriter.Encode(response), http.RequestAborted);
    }

    /// <summary>The checks RFC 8011 section 4.1 requires of every request.</summary>
    private static IppMessage? Validate(IppMessage request)
    {
        var operation = request.Groups.FirstOrDefault(g => g.Tag == IppTag.OperationAttributes);
        string? error = null;
        if (request.RequestId < 1)
            error = "request-id must be positive";
        else if (operation is null || operation.Attributes.Count < 2)
            error = "Missing operation attributes";
        else if (operation.Attributes[0].Name != "attributes-charset" || operation.Attributes[1].Name != "attributes-natural-language")
            error = "attributes-charset and attributes-natural-language must come first";
        else if (operation.Find("printer-uri") is null && operation.Find("job-uri") is null)
            error = "Missing printer-uri";
        return error is null ? null : IppMessage.CreateResponse(request, IppStatus.ClientErrorBadRequest, error);
    }

    private Task<IppMessage> DispatchAsync(RequestContext c) => c.Request.Code switch
    {
        IppOperation.GetPrinterAttributes => Task.FromResult(GetPrinterAttributes(c)),
        IppOperation.ValidateJob => Task.FromResult(ValidateJob(c)),
        IppOperation.PrintJob => PrintJobAsync(c),
        IppOperation.CreateJob => Task.FromResult(CreateJob(c)),
        IppOperation.SendDocument => SendDocumentAsync(c),
        IppOperation.CloseJob => CloseJobAsync(c),
        IppOperation.CancelJob => Task.FromResult(CancelJob(c)),
        IppOperation.GetJobAttributes => Task.FromResult(GetJobAttributes(c)),
        IppOperation.GetJobs => Task.FromResult(GetJobs(c)),
        IppOperation.CancelMyJobs => Task.FromResult(CancelMyJobs(c)),
        IppOperation.IdentifyPrinter => Task.FromResult(IppMessage.CreateResponse(c.Request, IppStatus.Ok)),
        _ => Task.FromResult(IppMessage.CreateResponse(c.Request, IppStatus.ServerErrorOperationNotSupported)),
    };

    private IppMessage GetPrinterAttributes(RequestContext c)
    {
        var requested = RequestedAttributes(c.Request);
        var response = IppMessage.CreateResponse(c.Request, IppStatus.Ok);
        response.Groups.Add(QueueAttributes.Build(
            c.Queue, c.PrinterUri, $"http://{c.Http.Request.Host}", ServerClock.UpTimeSeconds, jobs.CountHeld(), requested));
        return response;
    }

    private IppMessage ValidateJob(RequestContext c) =>
        UnsupportedFormat(c) ?? Refused(c) ?? IppMessage.CreateResponse(c.Request, IppStatus.Ok);

    private async Task<IppMessage> PrintJobAsync(RequestContext c)
    {
        if ((UnsupportedFormat(c) ?? Refused(c)) is { } error)
            return error;

        var job = NewJob(c);
        var size = await ReceiveDocumentAsync(c, job.Id, append: false);
        await HoldAsync(job.Id, size, c.Http.RequestAborted);
        return JobResponse(c, jobs.Get(job.Id)!);
    }

    private IppMessage CreateJob(RequestContext c)
    {
        if ((UnsupportedFormat(c) ?? Refused(c)) is { } error)
            return error;
        return JobResponse(c, NewJob(c));
    }

    private async Task<IppMessage> SendDocumentAsync(RequestContext c)
    {
        var lastDocument = c.Request.Find(IppTag.OperationAttributes, "last-document")?.First?.AsBool();
        if (lastDocument is null)
            return IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorBadRequest, "Missing last-document");
        if (FindOwnJob(c) is not { } job)
            return IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorNotFound, "No such job");
        if (job.Status != JobStatus.Receiving)
            return IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorNotPossible, "Job is not accepting documents");
        if (UnsupportedFormat(c) is { } error)
            return error;

        if (c.Request.OperationString("document-format") is { } format)
            jobs.SetDocumentFormat(job.Id, format);

        var size = await ReceiveDocumentAsync(c, job.Id, append: true);
        if (lastDocument.Value)
            await HoldAsync(job.Id, size, c.Http.RequestAborted);
        return JobResponse(c, jobs.Get(job.Id)!);
    }

    private async Task<IppMessage> CloseJobAsync(RequestContext c)
    {
        if (FindOwnJob(c) is not { } job)
            return IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorNotFound, "No such job");
        if (job.Status == JobStatus.Receiving)
        {
            var path = spool.PathFor(job.Id);
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            await HoldAsync(job.Id, size, c.Http.RequestAborted);
        }
        return JobResponse(c, jobs.Get(job.Id)!);
    }

    private IppMessage CancelJob(RequestContext c)
    {
        if (FindOwnJob(c) is not { } job)
            return IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorNotFound, "No such job");
        // Held jobs look "completed" over IPP, so only a job still arriving can be canceled here.
        // Users delete held jobs from the TapQueue client instead.
        if (jobs.TryTransition(job.Id, JobStatus.Receiving, JobStatus.Canceled))
        {
            spool.Delete(job.Id);
            logger.LogInformation("Job {JobId} canceled by IPP client {Ip}", job.Id, c.ClientIp);
            return IppMessage.CreateResponse(c.Request, IppStatus.Ok);
        }
        return IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorNotPossible, "Job is already complete");
    }

    private IppMessage GetJobAttributes(RequestContext c)
    {
        if (FindOwnJob(c) is not { } job)
            return IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorNotFound, "No such job");
        var response = IppMessage.CreateResponse(c.Request, IppStatus.Ok);
        response.Groups.Add(Filter(JobGroup(c, job), RequestedAttributes(c.Request)));
        return response;
    }

    private static HashSet<string>? RequestedAttributes(IppMessage request) =>
        request.Find(IppTag.OperationAttributes, "requested-attributes")?.Values
            .Select(v => v.AsString()).OfType<string>().ToHashSet();

    private static IppGroup Filter(IppGroup group, IReadOnlySet<string>? requested)
    {
        if (requested is null || requested.Overlaps(["all", "job-description", "job-template"]))
            return group;
        var filtered = new IppGroup(group.Tag);
        filtered.Attributes.AddRange(group.Attributes.Where(a => requested.Contains(a.Name)));
        return filtered;
    }

    private IppMessage GetJobs(RequestContext c)
    {
        var whichJobs = c.Request.OperationString("which-jobs") ?? "not-completed";
        var limit = Math.Clamp(c.Request.OperationInt("limit") ?? 50, 1, 200);
        var jobIds = c.Request.Find(IppTag.OperationAttributes, "job-ids")?.Values.Select(v => v.AsInt()).OfType<int>().ToHashSet();
        var requested = RequestedAttributes(c.Request) ?? ["job-id", "job-uri"]; // RFC 8011 4.2.6.1 default

        var response = IppMessage.CreateResponse(c.Request, IppStatus.Ok);
        foreach (var job in jobs.ListFromIp(c.ClientIp, 200)
                     .Where(j => j.QueueId == c.Queue.Id)
                     .Where(j => jobIds is not null ? jobIds.Contains((int)j.Id) : whichJobs == "all" || (whichJobs == "completed") == IsTerminal(j))
                     .Take(limit))
        {
            response.Groups.Add(Filter(JobGroup(c, job), requested));
        }
        return response;
    }

    /// <summary>Cancels this machine's jobs that are still arriving (held jobs count as completed over IPP).</summary>
    private IppMessage CancelMyJobs(RequestContext c)
    {
        foreach (var job in jobs.ListFromIp(c.ClientIp, 200).Where(j => j.QueueId == c.Queue.Id))
        {
            if (jobs.TryTransition(job.Id, JobStatus.Receiving, JobStatus.Canceled))
                spool.Delete(job.Id);
        }
        return IppMessage.CreateResponse(c.Request, IppStatus.Ok);
    }

    private JobRecord NewJob(RequestContext c)
    {
        var requestingUser = c.Request.OperationString("requesting-user-name");
        var (userId, ownerHint) = owners.Resolve(c.ClientIp, requestingUser);
        var name = c.Request.OperationString("job-name") ?? c.Request.OperationString("document-name") ?? "Untitled";
        var copies = c.Request.Find(IppTag.JobAttributes, "copies")?.First?.AsInt() ?? 1;
        var jobGroup = c.Request.Groups.FirstOrDefault(g => g.Tag == IppTag.JobAttributes);

        var job = jobs.Create(new NewJob(
            UserId: userId,
            OwnerHint: ownerHint,
            QueueId: c.Queue.Id,
            Name: name,
            DocumentFormat: c.Request.OperationString("document-format") ?? "application/octet-stream",
            Copies: Math.Clamp(copies, 1, 999),
            SourceIp: c.ClientIp,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(settings.HoldHours),
            JobAttributes: jobGroup is { Attributes.Count: > 0 } ? JobTemplate.Encode(jobGroup) : null));

        if (userId is null)
            logger.LogWarning(
                "Job {JobId} from {Ip} (\"{User}\") has no owner: no signed-in TapQueue client on that machine",
                job.Id, c.ClientIp, requestingUser);
        return job;
    }

    private async Task<long> ReceiveDocumentAsync(RequestContext c, long jobId, bool append)
    {
        await using (var file = append ? spool.OpenAppend(jobId) : spool.Create(jobId))
        {
            await c.Body.CopyToAsync(file, c.Http.RequestAborted);
        }
        return new FileInfo(spool.PathFor(jobId)).Length;
    }

    /// <summary>The whole document is here: count its pages and hold it for its owner.</summary>
    private async Task HoldAsync(long jobId, long size, CancellationToken ct)
    {
        var pages = PageCounter.Count(spool.PathFor(jobId));
        if (pages is not null && jobs.GetJobAttributes(jobId) is { } attributes)
        {
            var ranges = (await JobTemplate.DecodeAsync(attributes, ct)).Attributes
                .FirstOrDefault(a => a.Name == "page-ranges")?.Values.Select(v => v.Value).OfType<IppRange>().ToList();
            pages = PageCounter.ApplyPageRanges(pages.Value, ranges);
        }
        jobs.MarkReceived(jobId, size, pages);
        LogHeld(jobId);
    }

    private void LogHeld(long jobId)
    {
        if (jobs.Get(jobId) is not { } job) return;
        logger.LogInformation("Holding job {JobId} \"{Name}\" ({Size:N0} bytes, {Format}, {Pages} pages) for {Owner}",
            job.Id, job.Name, job.SizeBytes, job.DocumentFormat, job.Pages?.ToString() ?? "unknown",
            job.Username ?? $"nobody yet (client said \"{job.OwnerHint}\")");
        events.Record(EventCategory.Job, job.Username ?? job.OwnerHint ?? job.SourceIp,
            job.Username is null ? EventLog.Job(job.Id) : EventLog.User(job.Username),
            job.Username is null
                ? $"\"{job.Name}\" (job #{job.Id}) arrived from {job.SourceIp} with no signed-in client there, so it isn't matched to anyone."
                : $"{job.Username} printed \"{job.Name}\" (job #{job.Id}) to {job.QueueId}; it's held until they release it.");
    }

    /// <summary>Clients may only see and change jobs sent from their own machine.</summary>
    private JobRecord? FindOwnJob(RequestContext c) =>
        JobId(c.Request) is { } id && jobs.Get(id) is { } job && job.SourceIp == c.ClientIp && job.QueueId == c.Queue.Id
            ? job
            : null;

    /// <summary>A job is addressed by printer-uri + job-id, or by job-uri (".../ipp/secure/123").</summary>
    private static long? JobId(IppMessage request)
    {
        if (request.OperationInt("job-id") is { } id)
            return id;
        var uri = request.OperationString("job-uri");
        var slash = uri?.LastIndexOf('/') ?? -1;
        return slash >= 0 && long.TryParse(uri![(slash + 1)..], out var fromUri) ? fromUri : null;
    }

    /// <summary>
    /// Jobs from disabled users, or from users whose groups don't allow this queue, are turned away up
    /// front, so Windows tells them instead of holding a job that can never be released.
    /// </summary>
    private IppMessage? Refused(RequestContext c)
    {
        var (userId, _) = owners.Resolve(c.ClientIp, c.Request.OperationString("requesting-user-name"));
        if (userId is null || users.FindById(userId.Value) is not { } user)
            return null;
        var reason = user.Disabled ? "their account is disabled"
            : !access.CanPrintTo(user.Id, c.Queue.Id) ? $"their groups don't allow printing to {c.Queue.Name}"
            : null;
        if (reason is null)
            return null;
        logger.LogInformation("Refused a job from {User} at {Ip}: {Reason}", user.Username, c.ClientIp, reason);
        events.Record(EventCategory.Job, user.Username, EventLog.User(user.Username), $"Refused a job from {user.Username}: {reason}.");
        return IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorNotAuthorized,
            user.Disabled ? "Your TapQueue account is disabled." : $"You aren't allowed to print to {c.Queue.Name}.");
    }

    private static IppMessage? UnsupportedFormat(RequestContext c)
    {
        var format = c.Request.OperationString("document-format");
        if (format is null || QueueAttributes.DocumentFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            return null;
        var response = IppMessage.CreateResponse(c.Request, IppStatus.ClientErrorDocumentFormatNotSupported, $"{format} is not supported");
        response.Group(IppTag.UnsupportedAttributes).Add("document-format", IppValue.MimeType(format));
        return response;
    }

    private static IppMessage JobResponse(RequestContext c, JobRecord job)
    {
        var response = IppMessage.CreateResponse(c.Request, IppStatus.Ok);
        response.Groups.Add(JobGroup(c, job));
        return response;
    }

    /// <summary>
    /// Once a job has fully arrived we report it to the client as completed: from Windows'
    /// point of view it has been delivered. Holding it until release is TapQueue's business, and
    /// reporting it as held would leave it sitting in the user's Windows print queue for hours.
    /// </summary>
    private static IppGroup JobGroup(RequestContext c, JobRecord job)
    {
        var (state, reason) = job.Status switch
        {
            JobStatus.Receiving => (IppJobState.Pending, "job-incoming"),
            JobStatus.Canceled => (IppJobState.Canceled, "job-canceled-by-user"),
            _ => (IppJobState.Completed, "job-completed"),
        };
        // From the client's point of view the job finished when its data arrived; we don't track that
        // moment separately, so the submit time is the closest honest answer.
        DateTimeOffset? completedAt = state == IppJobState.Pending ? null : job.SubmittedAt;
        return new IppGroup(IppTag.JobAttributes)
            .Add("job-id", IppValue.Integer((int)job.Id))
            .Add("job-uri", IppValue.Uri($"{c.PrinterUri}/{job.Id}"))
            .Add("job-printer-uri", IppValue.Uri(c.PrinterUri))
            .Add("job-name", IppValue.Name(job.Name))
            .Add("job-originating-user-name", IppValue.Name(job.Username ?? job.OwnerHint ?? "unknown"))
            .Add("job-state", IppValue.Enum(state))
            .Add("job-state-reasons", IppValue.Keyword(reason))
            .Add("job-state-message", IppValue.Text(state == IppJobState.Completed ? "Held for release" : ""))
            .Add("job-printer-up-time", IppValue.Integer(ServerClock.UpTimeSeconds))
            .Add("time-at-creation", IppValue.Integer(ServerClock.ToUpTime(job.SubmittedAt)))
            .Add("time-at-processing", completedAt is null ? IppValue.NoValue() : IppValue.Integer(ServerClock.ToUpTime(completedAt.Value)))
            .Add("time-at-completed", completedAt is null ? IppValue.NoValue() : IppValue.Integer(ServerClock.ToUpTime(completedAt.Value)))
            .Add("date-time-at-creation", IppValue.DateTime(job.SubmittedAt))
            .Add("date-time-at-processing", completedAt is null ? IppValue.NoValue() : IppValue.DateTime(completedAt.Value))
            .Add("date-time-at-completed", completedAt is null ? IppValue.NoValue() : IppValue.DateTime(completedAt.Value))
            .Add("copies", IppValue.Integer(job.Copies))
            .Add("document-format", IppValue.MimeType(job.DocumentFormat))
            .Add("job-k-octets", IppValue.Integer((int)Math.Min(int.MaxValue, (job.SizeBytes + 1023) / 1024)));
    }

    private static bool IsTerminal(JobRecord job) => job.Status != JobStatus.Receiving;

    private sealed record RequestContext(HttpContext Http, QueueRecord Queue, IppMessage Request, Stream Body, string PrinterUri)
    {
        public string ClientIp { get; } = Http.ClientIp();
    }
}
