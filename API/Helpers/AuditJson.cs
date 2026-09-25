using System.Text.Json;

namespace Platform.Api.Helpers;

public static class AuditJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
