namespace TapQueue.Server.Data;

/// <summary>Ids of queues, printers and stations end up in URLs and config files, so they're kept plain.</summary>
public static class Ids
{
    public const string Rule = "letters, digits, '-' or '_' (at most 64)";

    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
