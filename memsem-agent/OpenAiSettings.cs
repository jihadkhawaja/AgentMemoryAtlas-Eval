internal sealed class OpenAiSettings
{
	public string Endpoint { get; init; } = "https://api.openai.com/";

	public string ApiKey { get; init; } = string.Empty;

	public string ChatModel { get; init; } = "gpt-5.6-luna";

	public string? ReasoningEffort { get; init; } = "medium";
}
