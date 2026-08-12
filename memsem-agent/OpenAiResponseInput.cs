using System.Text.Json.Serialization;

internal sealed record OpenAiResponseInput([property: JsonPropertyName("role")] string? Role = null, [property: JsonPropertyName("content")] string? Content = null, [property: JsonPropertyName("type")] string? Type = null, [property: JsonPropertyName("call_id")] string? CallId = null, [property: JsonPropertyName("output")] string? Output = null);
