using System.Text;

namespace TapQueue.Station;

/// <summary>
/// Turns key events from a keyboard-style badge reader into card numbers. Readers "type" the card
/// number and finish with Enter (some use Tab). Only the keys readers actually send are mapped.
/// </summary>
internal sealed class KeyDecoder
{
    // Linux input-event-codes.h
    private const int KeyTab = 15, KeyEnter = 28, KeyLeftShift = 42, KeyRightShift = 54, KeyKpEnter = 96;

    /// <summary>A reader types a whole card in well under a second; anything slower is a stale partial read.</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(1);

    private static readonly Dictionary<int, (char Normal, char Shifted)> Keys = BuildKeyMap();

    private readonly StringBuilder _buffer = new();
    private bool _leftShift, _rightShift;
    private TimeSpan _lastKeyAt;

    /// <param name="code">EV_KEY code.</param>
    /// <param name="value">1 = press, 0 = release, 2 = autorepeat.</param>
    /// <param name="time">When the event happened (any monotonic clock).</param>
    /// <returns>A complete card number when Enter/Tab ends one; otherwise null.</returns>
    public string? Feed(int code, int value, TimeSpan time)
    {
        if (code == KeyLeftShift) { _leftShift = value != 0; return null; }
        if (code == KeyRightShift) { _rightShift = value != 0; return null; }
        if (value != 1) return null;

        if (_buffer.Length > 0 && time - _lastKeyAt > MaxGap)
            _buffer.Clear();
        _lastKeyAt = time;

        if (code is KeyEnter or KeyKpEnter or KeyTab)
        {
            var card = _buffer.ToString();
            _buffer.Clear();
            return card.Length > 0 ? card : null;
        }
        if (Keys.TryGetValue(code, out var key) && _buffer.Length < 256)
            _buffer.Append(_leftShift || _rightShift ? key.Shifted : key.Normal);
        return null;
    }

    private static Dictionary<int, (char, char)> BuildKeyMap()
    {
        var map = new Dictionary<int, (char, char)>();
        void Row(int firstCode, string normal, string shifted)
        {
            for (var i = 0; i < normal.Length; i++)
                map[firstCode + i] = (normal[i], shifted[i]);
        }
        Row(2, "1234567890-=", "!@#$%^&*()_+");
        Row(16, "qwertyuiop", "QWERTYUIOP");
        Row(30, "asdfghjkl;", "ASDFGHJKL:");
        Row(44, "zxcvbnm,./", "ZXCVBNM<>?");
        // Keypad: some readers type digits on the number pad.
        Row(71, "789-456+1230.", "789-456+1230.");
        return map;
    }
}
