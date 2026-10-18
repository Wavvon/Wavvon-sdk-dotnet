using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavvon.Sdk;

/// <summary>The JSON dialect of the hub: snake_case names, absent rather than null optionals.</summary>
internal static class WavvonJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static StringContent Content<T>(T value) =>
        new(JsonSerializer.Serialize(value, Options), System.Text.Encoding.UTF8, "application/json");
}
