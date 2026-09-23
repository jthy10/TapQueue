namespace TapQueue.Server;

public static class HttpContextExtensions
{
    /// <summary>The caller's IP, with IPv4-mapped IPv6 addresses turned back into plain IPv4.</summary>
    public static string ClientIp(this HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
