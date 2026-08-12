using System.Text.Json.Serialization;

internal sealed record OpenAiFunctionCall([property: JsonPropertyName("call_id")] string CallId, [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("arguments")] string Arguments);
