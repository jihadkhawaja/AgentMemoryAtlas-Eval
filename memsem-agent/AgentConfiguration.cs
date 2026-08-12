using System;
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

internal sealed class AgentConfiguration
{
	private static readonly Regex EnvironmentVariablePattern = new Regex("\\$\\{([A-Za-z_][A-Za-z0-9_]*)\\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public DiscordSettings Discord { get; init; } = new DiscordSettings();

	public OpenAiSettings OpenAi { get; init; } = new OpenAiSettings();

	public MemsemSettings Memsem { get; init; } = new MemsemSettings();

	public AgentSettings Agent { get; init; } = new AgentSettings();

	public static AgentConfiguration Load(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Copy config.example.yaml to config.local.yaml and configure the bot.", path);
		}
		string input = EnvironmentVariablePattern.Replace(File.ReadAllText(path), (Match match) => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
		AgentConfiguration agentConfiguration = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build().Deserialize<AgentConfiguration>(input) ?? throw new InvalidDataException("Configuration '" + path + "' is empty.");
		agentConfiguration.Validate();
		return agentConfiguration;
	}

	private void Validate()
	{
		Require(Discord.Token, "discord.token");
		Require(OpenAi.ApiKey, "openAi.apiKey");
		Require(Memsem.Command, "memsem.command");
		Require(Memsem.Project, "memsem.project");
		if (Discord.ChannelId == 0)
		{
			throw new InvalidDataException("discord.channelId must be configured.");
		}
		if (OpenAi.ReasoningEffort != null)
		{
			if (!new[] { "low", "medium", "high" }.Contains(OpenAi.ReasoningEffort, StringComparer.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("openAi.reasoningEffort must be low, medium, high, or null.");
			}
		}
		if (string.IsNullOrWhiteSpace(Agent.Name))
		{
			throw new InvalidDataException("agent.name must be configured.");
		}
		Require(Agent.ReplyPrefix, "agent.replyPrefix");
		Require(Agent.PeerPrefix, "agent.peerPrefix");
		if (Agent.MemoryTopK < 1)
		{
			throw new InvalidDataException("agent.memoryTopK must be positive.");
		}
		if (Agent.MaxMessages < 1)
		{
			throw new InvalidDataException("agent.maxMessages must be positive.");
		}
	}

	private static void Require(string value, string name)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Contains("${", StringComparison.Ordinal))
		{
			throw new InvalidDataException(name + " must be configured in config.local.yaml or its environment variable.");
		}
	}
}
