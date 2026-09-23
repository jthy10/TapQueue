using System.Text.Json;

namespace TapQueue.Shared;

public static class TapQueueJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
