using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal sealed record OpenAiToolDefinition([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("description")] string Description, [property: JsonPropertyName("parameters")] JsonObject Parameters);
