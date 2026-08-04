using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal sealed class OpenAiToolClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly HttpClient httpClient;
    private readonly string apiKey;
    private readonly string chatModel;
    private readonly string? reasoningEffort;

    public OpenAiToolClient(HttpClient httpClient, string apiKey, string chatModel, string? reasoningEffort)
    {
        this.httpClient = httpClient;
        this.apiKey = apiKey;
        this.chatModel = chatModel;
        this.reasoningEffort = reasoningEffort;
    }

    public async Task<OpenAiToolCompletion> CompleteAsync(
        IReadOnlyList<OpenAiResponseInput> input,
        IReadOnlyList<OpenAiToolDefinition> tools,
        string? previousResponseId = null,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = chatModel,
            ["input"] = input,
            ["tools"] = tools,
            ["tool_choice"] = "auto"
        };
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            requestBody["reasoning"] = new { effort = reasoningEffort };
        if (!string.IsNullOrWhiteSpace(previousResponseId))
            requestBody["previous_response_id"] = previousResponseId;
        var serializedRequest = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = new StringContent(serializedRequest, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"OpenAI tool request failed with {(int)response.StatusCode}: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("OpenAI returned an empty tool response.");
        var responseId = payload["id"]?.GetValue<string>();
        var outputText = ReadOutputText(payload);
        var toolCalls = new List<OpenAiFunctionCall>();
        if (payload["output"] is JsonArray output)
        {
            foreach (var item in output.OfType<JsonObject>())
            {
                if (!string.Equals(item["type"]?.GetValue<string>(), "function_call", StringComparison.Ordinal))
                    continue;
                var callId = item["call_id"]?.GetValue<string>();
                var name = item["name"]?.GetValue<string>();
                var arguments = item["arguments"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(name) && arguments is not null)
                    toolCalls.Add(new OpenAiFunctionCall(callId, name, arguments));
            }
        }

        return new OpenAiToolCompletion(responseId, outputText, toolCalls);
    }

    private static string? ReadOutputText(JsonObject payload)
    {
        var topLevelText = payload["output_text"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(topLevelText))
            return topLevelText;

        if (payload["output"] is not JsonArray output)
            return null;

        var fragments = new List<string>();
        foreach (var outputItem in output.OfType<JsonObject>())
        {
            if (!string.Equals(outputItem["type"]?.GetValue<string>(), "message", StringComparison.Ordinal) ||
                outputItem["content"] is not JsonArray content)
                continue;

            foreach (var contentItem in content.OfType<JsonObject>())
            {
                if (string.Equals(contentItem["type"]?.GetValue<string>(), "output_text", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(contentItem["text"]?.GetValue<string>()))
                    fragments.Add(contentItem["text"]!.GetValue<string>());
            }
        }

        return fragments.Count == 0 ? null : string.Join(Environment.NewLine, fragments);
    }
}

internal sealed record OpenAiToolCompletion(
    string? Id,
    string? Text,
    IReadOnlyList<OpenAiFunctionCall> ToolCalls);

internal sealed record OpenAiResponseInput(
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("call_id")] string? CallId = null,
    [property: JsonPropertyName("output")] string? Output = null);

internal sealed record OpenAiFunctionCall(
    [property: JsonPropertyName("call_id")] string CallId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

internal sealed record OpenAiToolDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonObject Parameters);
