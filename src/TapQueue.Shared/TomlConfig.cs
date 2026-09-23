using System.Text.Json;
using Tomlyn;

namespace TapQueue.Shared;

/// <summary>Loads snake_case TOML config files into POCOs.</summary>
public static class TomlConfig
{
    private static readonly TomlSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static T Load<T>(string path) where T : new()
    {
        var text = File.ReadAllText(path);
        try
        {
            return TomlSerializer.Deserialize<T>(text, Options) ?? new T();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not parse config file '{path}': {ex.Message}", ex);
        }
    }
}
