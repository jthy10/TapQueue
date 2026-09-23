using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TapQueue.Station;

/// <summary>
/// Reads an RFIDeas pcProx reader over its vendor HID feature-report channel (/dev/hidraw*) instead of
/// keystrokes. Works when the reader is set to "SDK mode" and types nothing, and never leaks card
/// numbers into a console.
///
/// Ported from https://github.com/jthy10/ID-Card-Reader, which documents the hardware quirks this
/// relies on:
/// - Only 0x8f (read card buffer) and 0x8e (read bit count) are ever sent. Config commands
///   (0x80-0x82, 0x8a, 0x90) silently stop card detection until the reader is unplugged.
/// - Polling faster than ~2 Hz stops the reader detecting cards; a read stays in its buffer for
///   only ~1 s. So it's polled every 0.5 s.
/// - After a replug, an old hidraw handle keeps "working" and returns zeros. The USB device number
///   in sysfs is watched to notice this.
/// - A card resting on the reader is re-read every ~1.7 s, so repeat reads within a few seconds
///   count as one tap.
/// </summary>
internal sealed partial class PcProxReader(string? devicePath, Action<string> log) : IBadgeReader
{
    private const string VendorId = "0C27";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan RetriggerGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReplugCheckInterval = TimeSpan.FromSeconds(2);

    // _IOC(_IOC_READ|_IOC_WRITE, 'H', 0x06/0x07, 9): one report-ID byte + 8-byte feature report.
    private const uint HidIocSFeature = 0xC0094806;
    private const uint HidIocGFeature = 0xC0094807;
    private const int ReportLength = 8;

    public string Status { get; private set; } = "starting";

    public async IAsyncEnumerable<string> ReadCardsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var waitingLogged = false;
        while (!ct.IsCancellationRequested)
        {
            var node = devicePath is { Length: > 0 } ? devicePath : FindHidraw();
            SafeFileHandle? handle = null;
            try
            {
                if (node is not null)
                    handle = File.OpenHandle(node, FileMode.Open, FileAccess.ReadWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = ex is UnauthorizedAccessException ? $"No permission to open {node}" : $"Can't open {node}";
                if (!waitingLogged)
                {
                    log(ex is UnauthorizedAccessException
                        ? $"No permission to open {node}. Install deploy/udev/60-tapqueue-pcprox.rules or run with sudo."
                        : $"Can't open {node}: {ex.Message}");
                    waitingLogged = true;
                }
            }
            if (handle is null)
            {
                if (!waitingLogged)
                {
                    log("No RFIDeas pcProx reader found. Waiting for it...");
                    Status = "No pcProx reader plugged in";
                    waitingLogged = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            using (handle)
            {
                waitingLogged = false;
                var fd = (int)handle.DangerousGetHandle();
                var openedDevnum = UsbDevnum(node!);
                log($"Polling pcProx reader on {node}");
                Status = "ok";

                byte[]? lastCard = null;
                var lastSeen = DateTimeOffset.MinValue;
                var lastReplugCheck = DateTimeOffset.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(PollInterval, ct);

                    var now = DateTimeOffset.UtcNow;
                    if (now - lastReplugCheck >= ReplugCheckInterval)
                    {
                        lastReplugCheck = now;
                        if (UsbDevnum(node!) is var devnum && (devnum is null || devnum != openedDevnum))
                        {
                            log($"pcProx reader was unplugged or re-enumerated (USB device {openedDevnum ?? "?"} -> {devnum ?? "gone"}); reconnecting.");
                            break;
                        }
                    }

                    byte[]? card, meta;
                    try
                    {
                        card = Interact(fd, 0x8f);
                        meta = card is null ? null : Interact(fd, 0x8e);
                    }
                    catch (IOException ex)
                    {
                        log($"pcProx reader error: {ex.Message}; reconnecting.");
                        break;
                    }
                    if (card is null || meta is null || meta[0] == 0)
                        continue;

                    var bits = meta[0];
                    var cardBytes = card.AsSpan(0, Math.Min((bits + 7) / 8, ReportLength)).ToArray();
                    var seen = DateTimeOffset.UtcNow;
                    if (lastCard is not null && cardBytes.AsSpan().SequenceEqual(lastCard) && seen - lastSeen < RetriggerGap)
                    {
                        lastSeen = seen; // still resting on the reader
                        continue;
                    }
                    (lastCard, lastSeen) = (cardBytes, seen);
                    yield return DecodeCard(card, bits);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>
    /// The card buffer is little-endian; the low <paramref name="bits"/> bits are the raw card data
    /// (e.g. a 26-bit HID H10301 Wiegand frame). Returned in decimal, as the reader types it by default.
    /// </summary>
    internal static string DecodeCard(ReadOnlySpan<byte> buffer, int bits)
    {
        var bytes = buffer[..Math.Min((bits + 7) / 8, buffer.Length)];
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        if (bits < bytes.Length * 8)
            value &= (BigInteger.One << bits) - 1;
        return value.ToString();
    }

    /// <summary>Sends a one-byte command as a feature report and reads the reply. Null if the reply is empty.</summary>
    private static unsafe byte[]? Interact(int fd, byte command)
    {
        var buffer = stackalloc byte[ReportLength + 1];
        new Span<byte>(buffer, ReportLength + 1).Clear();
        buffer[1] = command;
        if (Ioctl(fd, HidIocSFeature, buffer) < 0)
            throw new IOException($"HIDIOCSFEATURE failed (errno {Marshal.GetLastPInvokeError()})");
        Thread.Sleep(2);

        new Span<byte>(buffer, ReportLength + 1).Clear();
        if (Ioctl(fd, HidIocGFeature, buffer) < 0)
            throw new IOException($"HIDIOCGFEATURE failed (errno {Marshal.GetLastPInvokeError()})");
        var reply = new Span<byte>(buffer + 1, ReportLength).ToArray();
        return reply.AsSpan().ContainsAnyExcept((byte)0) ? reply : null;
    }

    private static string? FindHidraw()
    {
        const string sysHidraw = "/sys/class/hidraw";
        if (!Directory.Exists(sysHidraw)) return null;
        foreach (var dir in Directory.GetDirectories(sysHidraw).Order())
        {
            try
            {
                var uevent = File.ReadAllText(Path.Combine(dir, "device", "uevent"));
                var node = "/dev/" + Path.GetFileName(dir);
                if (uevent.Contains($"HID_ID=0003:0000{VendorId}", StringComparison.OrdinalIgnoreCase) && File.Exists(node))
                    return node;
            }
            catch (IOException)
            {
                // Device went away while we looked.
            }
        }
        return null;
    }

    /// <summary>The reader's current USB device number, from sysfs (no USB traffic). Changes on replug.</summary>
    internal static string? UsbDevnum(string node)
    {
        try
        {
            // sysfs is nested relative symlinks; only the kernel's own resolution gets them right.
            var path = RealPath(Path.Combine("/sys/class/hidraw", Path.GetFileName(RealPath(node) ?? node), "device"));
            for (var i = 0; i < 8 && path is not null; i++)
            {
                path = Path.GetDirectoryName(path);
                var devnum = path is null ? null : Path.Combine(path, "devnum");
                if (devnum is not null && File.Exists(devnum))
                    return File.ReadAllText(devnum).Trim();
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

    private static string? RealPath(string path)
    {
        var resolved = RealPathNative(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Marshal.FreeHGlobal(resolved); // malloc'd by libc; FreeHGlobal is free() on Unix
        }
    }

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPathNative(string path, IntPtr resolved);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static unsafe partial int Ioctl(int fd, nuint request, byte* arg);
}
