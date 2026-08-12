using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

internal sealed class OpenAiToolClient
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
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

	public async Task<OpenAiToolCompletion> CompleteAsync(IReadOnlyList<OpenAiResponseInput> input, IReadOnlyList<OpenAiToolDefinition> tools, string? previousResponseId = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		Dictionary<string, object?> requestBody = new Dictionary<string, object>
		{
			["model"] = chatModel,
			["input"] = input,
			["tools"] = tools,
			["tool_choice"] = "auto"
		};
		if (!string.IsNullOrWhiteSpace(reasoningEffort))
		{
			requestBody["reasoning"] = new
			{
				effort = reasoningEffort
			};
		}
		if (!string.IsNullOrWhiteSpace(previousResponseId))
		{
			requestBody["previous_response_id"] = previousResponseId;
		}
		string serializedRequest = JsonSerializer.Serialize(requestBody, JsonOptions);
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
		{
			Content = new StringContent(serializedRequest, Encoding.UTF8, "application/json")
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			string body = await response.Content.ReadAsStringAsync(cancellationToken);
			throw new HttpRequestException($"OpenAI tool request failed with {response.StatusCode}: {body}");
		}
		JsonObject payload = (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions, cancellationToken)) ?? throw new InvalidDataException("OpenAI returned an empty tool response.");
		string responseId = payload["id"]?.GetValue<string>();
		string outputText = ReadOutputText(payload);
		List<OpenAiFunctionCall> toolCalls = new List<OpenAiFunctionCall>();
		JsonNode jsonNode = payload["output"];
		if (jsonNode is JsonArray output)
		{
			foreach (JsonObject item in output.OfType<JsonObject>())
			{
				if (string.Equals(item["type"]?.GetValue<string>(), "function_call", StringComparison.Ordinal))
				{
					string callId = item["call_id"]?.GetValue<string>();
					string name = item["name"]?.GetValue<string>();
					string arguments = item["arguments"]?.GetValue<string>();
					if (!string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(name) && arguments != null)
					{
						toolCalls.Add(new OpenAiFunctionCall(callId, name, arguments));
					}
				}
			}
		}
		return new OpenAiToolCompletion(responseId, outputText, toolCalls);
	}

	private static string? ReadOutputText(JsonObject payload)
	{
		string text = payload["output_text"]?.GetValue<string>();
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text;
		}
		if (!(payload["output"] is JsonArray source))
		{
			return null;
		}
		List<string> list = new List<string>();
		foreach (JsonObject item in source.OfType<JsonObject>())
		{
			if (!string.Equals(item["type"]?.GetValue<string>(), "message", StringComparison.Ordinal) || !(item["content"] is JsonArray source2))
			{
				continue;
			}
			foreach (JsonObject item2 in source2.OfType<JsonObject>())
			{
				if (string.Equals(item2["type"]?.GetValue<string>(), "output_text", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item2["text"]?.GetValue<string>()))
				{
					list.Add(item2["text"].GetValue<string>());
				}
			}
		}
		return (list.Count == 0) ? null : string.Join(Environment.NewLine, list);
	}
}
