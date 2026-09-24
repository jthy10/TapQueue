using System.Formats.Tar;
using System.IO.Compression;
using TapQueue.Shared;

namespace TapQueue.Admin;

/// <summary>Reads the client release packages scripts/package.sh builds, for `clients publish`.</summary>
public static class ClientPackage
{
    private static readonly string[] ProgramNames = ["TapQueueClient.exe", "tapqueue-client"];

    /// <summary>
    /// Copies the client program out of <paramref name="path"/> (a .zip, a .tar.gz, or the bare
    /// program) to <paramref name="destination"/>, and returns the version.txt packaged with it, if any.
    /// </summary>
    public static async Task<string?> ExtractAsync(string path, string destination)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var program = zip.Entries.FirstOrDefault(e => ProgramNames.Contains(e.Name))
                          ?? throw new InvalidDataException($"{path} has no TapQueueClient.exe or tapqueue-client in it.");
            await using (var from = program.Open())
            await using (var to = File.Create(destination))
                await from.CopyToAsync(to);
            if (zip.Entries.FirstOrDefault(e => e.Name == "version.txt") is not { } versionEntry)
                return null;
            using var reader = new StreamReader(versionEntry.Open());
            return (await reader.ReadToEndAsync()).Trim();
        }

        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            string? version = null;
            var found = false;
            await using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            await using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync() is { } entry)
            {
                var name = Path.GetFileName(entry.Name);
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                    continue;
                if (ProgramNames.Contains(name))
                {
                    await using var to = File.Create(destination);
                    await entry.DataStream.CopyToAsync(to);
                    found = true;
                }
                else if (name == "version.txt")
                {
                    using var reader = new StreamReader(entry.DataStream);
                    version = (await reader.ReadToEndAsync()).Trim();
                }
            }
            return found ? version : throw new InvalidDataException($"{path} has no TapQueueClient.exe or tapqueue-client in it.");
        }

        File.Copy(path, destination, overwrite: true);
        return null;
    }

    /// <summary>The platform a program is built for, from its first bytes, or null if it's neither.</summary>
    public static string? PlatformOf(string program)
    {
        Span<byte> magic = stackalloc byte[4];
        using var file = File.OpenRead(program);
        var read = file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        if (read >= 2 && magic[0] == 'M' && magic[1] == 'Z')
            return ClientPlatform.Windows;
        if (read == 4 && magic[0] == 0x7f && magic[1] == 'E' && magic[2] == 'L' && magic[3] == 'F')
            return ClientPlatform.Linux;
        return null;
    }
}
