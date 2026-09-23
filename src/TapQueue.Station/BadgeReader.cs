using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TapQueue.Station;

internal interface IBadgeReader
{
    /// <summary>Card numbers as the reader sends them, one per tap.</summary>
    IAsyncEnumerable<string> ReadCardsAsync(CancellationToken ct);

    /// <summary>"ok" while the reader is connected; otherwise what's wrong. Reported in heartbeats.</summary>
    string Status { get; }
}

/// <summary>One card number per line on standard input. For testing without a reader.</summary>
internal sealed class StdinBadgeReader : IBadgeReader
{
    public string Status => "ok";

    public async IAsyncEnumerable<string> ReadCardsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await Console.In.ReadLineAsync(ct) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return line.Trim();
        }
    }
}

/// <summary>
/// Reads a keyboard-style USB badge reader straight from its Linux input device. The device is grabbed
/// so card numbers aren't also typed into whatever console or login prompt has focus. If the reader is
/// unplugged, this waits for it to come back.
/// </summary>
internal sealed partial class EvdevBadgeReader(string devicePath, Action<string> log) : IBadgeReader
{
    private const ushort EvKey = 1;
    private const uint EviocGrab = 0x40044590; // _IOW('E', 0x90, int)
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public string Status { get; private set; } = "starting";

    // struct input_event: struct timeval (two longs), __u16 type, __u16 code, __s32 value.
    private static readonly int TimevalSize = 2 * IntPtr.Size;
    private static readonly int EventSize = TimevalSize + 8;

    public async IAsyncEnumerable<string> ReadCardsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var waitingLogged = false;
        while (!ct.IsCancellationRequested)
        {
            SafeFileHandle handle;
            try
            {
                handle = File.OpenHandle(devicePath, FileMode.Open, FileAccess.Read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = ex is UnauthorizedAccessException ? $"No permission to read {devicePath}" : $"Reader not found at {devicePath}";
                if (!waitingLogged)
                {
                    log(ex is UnauthorizedAccessException
                        ? $"No permission to read {devicePath}. Run as a member of the 'input' group (the systemd unit does this) or with sudo."
                        : $"Can't open badge reader {devicePath}: {ex.Message} Waiting for it...");
                    waitingLogged = true;
                }
                await Task.Delay(RetryDelay, ct);
                continue;
            }

            using (handle)
            {
                if (Ioctl((int)handle.DangerousGetHandle(), EviocGrab, 1) != 0)
                    log($"Warning: couldn't grab {devicePath} (errno {Marshal.GetLastPInvokeError()}); card numbers may also be typed into the console.");
                log($"Listening for badges on {devicePath}");
                Status = "ok";
                waitingLogged = false;

                var decoder = new KeyDecoder();
                using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
                var buffer = new byte[EventSize * 64];
                var filled = 0;
                while (!ct.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
                    }
                    catch (IOException ex)
                    {
                        log($"Lost badge reader {devicePath}: {ex.Message}");
                        break;
                    }
                    if (read == 0)
                    {
                        Status = $"Lost the reader at {devicePath}";
                        log($"Lost badge reader {devicePath}");
                        break;
                    }

                    filled += read;
                    var offset = 0;
                    for (; offset + EventSize <= filled; offset += EventSize)
                    {
                        var e = buffer.AsSpan(offset, EventSize);
                        if (BitConverter.ToUInt16(e[TimevalSize..]) != EvKey)
                            continue;
                        var seconds = IntPtr.Size == 8 ? BitConverter.ToInt64(e) : BitConverter.ToInt32(e);
                        var micros = IntPtr.Size == 8 ? BitConverter.ToInt64(e[8..]) : BitConverter.ToInt32(e[4..]);
                        var time = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMicroseconds(micros);
                        var code = BitConverter.ToUInt16(e[(TimevalSize + 2)..]);
                        var value = BitConverter.ToInt32(e[(TimevalSize + 4)..]);
                        if (decoder.Feed(code, value, time) is { } card)
                            yield return card;
                    }
                    // Keep any partial event for the next read.
                    buffer.AsSpan(offset, filled - offset).CopyTo(buffer);
                    filled -= offset;
                }
            }
            await Task.Delay(RetryDelay, ct);
        }
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, int arg);
}
