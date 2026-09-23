namespace TapQueue.Station;

/// <summary>/etc/tapqueue/station.toml</summary>
public sealed class StationConfig
{
    /// <summary>The TapQueue server, e.g. http://tapqueue-server:8631.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>From `tapqueue-admin stations add`.</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// "keyboard": a reader that types the card number (read from /dev/input).
    /// "pcprox": an RFIDeas pcProx polled over /dev/hidraw, which also works when it's set to type nothing.
    /// </summary>
    public string Reader { get; set; } = "keyboard";

    /// <summary>
    /// The badge reader's input device, e.g. /dev/input/by-id/usb-…-event-kbd.
    /// Empty reads card numbers from standard input instead (for testing).
    /// For reader = "pcprox", a /dev/hidraw* path; empty finds the reader automatically.
    /// </summary>
    public string Device { get; set; } = "";

    /// <summary>The same card tapped again within this many seconds is ignored.</summary>
    public int RepeatSeconds { get; set; } = 5;

    /// <summary>Reads shorter than this are treated as noise.</summary>
    public int MinCardLength { get; set; } = 4;

    public void Validate(bool needServer)
    {
        if (needServer)
        {
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidDataException("server_url must be an http:// or https:// URL, e.g. http://tapqueue-server:8631.");
            if (string.IsNullOrWhiteSpace(Token))
                throw new InvalidDataException("Set token to the station token from `tapqueue-admin stations add`.");
        }
        if (Reader is not ("keyboard" or "pcprox"))
            throw new InvalidDataException($"reader must be \"keyboard\" or \"pcprox\", not \"{Reader}\".");
        if (RepeatSeconds < 0 || MinCardLength < 1)
            throw new InvalidDataException("repeat_seconds must be >= 0 and min_card_length >= 1.");
    }
}
