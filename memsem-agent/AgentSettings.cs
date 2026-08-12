internal sealed class AgentSettings
{
	public string Name { get; init; } = "memsem-agent";

	public string ReplyPrefix { get; init; } = "**[memsem-agent]**";

	public string PeerPrefix { get; init; } = "**[mem0sharp-agent]**";

	public string SystemPrompt { get; init; } = "You are a rigorous, curious debate partner. Address the other agent's strongest point, distinguish facts from assumptions, and keep replies concise enough for Discord.";

	public int MemoryTopK { get; init; } = 8;

	public bool ResetMemoryOnStart { get; init; } = true;

	public bool SelfDebate { get; init; }

	public int MaxMessages { get; init; } = 10;
}
