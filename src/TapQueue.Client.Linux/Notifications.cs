using Tmds.DBus.Protocol;

namespace TapQueue.Client.Linux;

/// <summary>
/// Desktop notifications (the Windows client's balloon tips), sent to the desktop's notification
/// service (org.freedesktop.Notifications on the session bus), which GNOME, KDE and the others
/// provide. Failing to notify never stops the app; there just isn't a popup.
/// </summary>
public static class Notifications
{
    private static DBusConnection? _connection;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static async Task ShowAsync(string title, string body, bool urgent = false)
    {
        await Lock.WaitAsync();
        try
        {
            if (_connection is null)
            {
                if (DBusAddress.Session is not { } address)
                    return;
                var connection = new DBusConnection(address);
                await connection.ConnectAsync();
                _connection = connection;
            }
            await _connection.CallMethodAsync(Notify(_connection, title, body, urgent));
        }
        catch (Exception ex) when (ex is DBusConnectionException or DBusErrorReplyException or DBusMessageException or IOException or InvalidOperationException)
        {
            _connection?.Dispose();
            _connection = null;
            Console.Error.WriteLine($"TapQueue: couldn't show a notification: {ex.Message}");
        }
        finally
        {
            Lock.Release();
        }
    }

    // Notify(app_name s, replaces_id u, app_icon s, summary s, body s, actions as, hints a{sv}, expire_timeout i)
    private static MessageBuffer Notify(DBusConnection connection, string title, string body, bool urgent)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.Notifications",
            path: "/org/freedesktop/Notifications",
            @interface: "org.freedesktop.Notifications",
            member: "Notify",
            signature: "susssasa{sv}i");
        writer.WriteString("TapQueue");
        writer.WriteUInt32(0);
        writer.WriteString(urgent ? "dialog-error" : "printer");
        writer.WriteString(title);
        writer.WriteString(body);
        writer.WriteArray(Array.Empty<string>());
        var hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("urgency");
        writer.WriteVariantByte(urgent ? (byte)2 : (byte)1);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("desktop-entry");
        writer.WriteVariantString("tapqueue-client");
        writer.WriteDictionaryEnd(hints);
        writer.WriteInt32(-1);
        return writer.CreateMessage();
    }
}
