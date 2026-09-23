using System.Threading.Channels;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Logging;

/// <summary>
/// Keeps the server's recent log lines in memory for the admin console's live log, and hands new
/// ones to whoever is watching. The full log is still in journalctl; this is only the tail.
/// </summary>
public sealed class LogBuffer : ILoggerProvider
{
    public const int Capacity = 2000;

    private readonly Lock _lock = new();
    private readonly Queue<LogLineDto> _lines = new();
    private readonly List<Channel<LogLineDto>> _watchers = [];
    private long _nextId = 1;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <summary>Lines after <paramref name="afterId"/>, oldest first, at most <paramref name="limit"/> of the newest.</summary>
    public List<LogLineDto> Since(long afterId, int limit)
    {
        lock (_lock)
            return _lines.Where(l => l.Id > afterId).TakeLast(limit).ToList();
    }

    /// <summary>Lines after <paramref name="afterId"/>, then every new line until <paramref name="ct"/> is canceled.</summary>
    public async IAsyncEnumerable<LogLineDto> WatchAsync(long afterId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // A slow browser drops lines rather than holding up logging; it can reload to catch up.
        var channel = Channel.CreateBounded<LogLineDto>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest });
        List<LogLineDto> backlog;
        lock (_lock)
        {
            backlog = _lines.Where(l => l.Id > afterId).ToList();
            _watchers.Add(channel);
        }
        try
        {
            foreach (var line in backlog)
                yield return line;
            await foreach (var line in channel.Reader.ReadAllAsync(ct))
                yield return line;
        }
        finally
        {
            lock (_lock)
                _watchers.Remove(channel);
        }
    }

    private void Add(LogLevel level, string category, string message)
    {
        lock (_lock)
        {
            var line = new LogLineDto(_nextId++, DateTimeOffset.UtcNow, Level(level), ShortCategory(category), message);
            _lines.Enqueue(line);
            if (_lines.Count > Capacity)
                _lines.Dequeue();
            foreach (var watcher in _watchers)
                watcher.Writer.TryWrite(line);
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warning",
        _ => "error",
    };

    /// <summary>"TapQueue.Server.Api.ClientApi" reads as "ClientApi".</summary>
    private static string ShortCategory(string category) => category[(category.LastIndexOf('.') + 1)..];

    public void Dispose()
    {
        lock (_lock)
            foreach (var watcher in _watchers)
                watcher.Writer.TryComplete();
    }

    private sealed class Logger(LogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception is not null)
                message += $" | {exception.GetType().Name}: {exception.Message}";
            buffer.Add(logLevel, category, message);
        }
    }
}
