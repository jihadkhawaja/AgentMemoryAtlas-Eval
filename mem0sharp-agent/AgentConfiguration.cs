using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

internal sealed class AgentConfiguration
{
    public DiscordSettings Discord { get; init; } = new();
    public OpenAiSettings OpenAi { get; init; } = new();
    public PostgresSettings Postgres { get; init; } = new();
    public AgentSettings Agent { get; init; } = new();

    public static AgentConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Copy config.example.yaml to config.local.yaml and configure the bot.",
                path);
        }

        var yaml = EnvironmentVariablePattern.Replace(
            File.ReadAllText(path),
            match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
        var configuration = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Deserialize<AgentConfiguration>(yaml)
            ?? throw new InvalidDataException($"Configuration '{path}' is empty.");

        configuration.Validate();
        return configuration;
    }

    private void Validate()
    {
        Require(Discord.Token, "discord.token");
        Require(OpenAi.ApiKey, "openAi.apiKey");
        Require(Postgres.ConnectionString, "postgres.connectionString");
        if (Discord.ChannelId == 0) throw new InvalidDataException("discord.channelId must be configured.");
        if (OpenAi.EmbeddingDimensions is < 1) throw new InvalidDataException("openAi.embeddingDimensions must be positive.");
        if (Postgres.EmbeddingDimensions is < 1) throw new InvalidDataException("postgres.embeddingDimensions must be positive.");
        if (OpenAi.EmbeddingDimensions != Postgres.EmbeddingDimensions)
            throw new InvalidDataException("openAi.embeddingDimensions and postgres.embeddingDimensions must match.");
        if (OpenAi.ReasoningEffort is not null &&
            !new[] { "low", "medium", "high" }.Contains(OpenAi.ReasoningEffort, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("openAi.reasoningEffort must be low, medium, high, or null.");
        if (string.IsNullOrWhiteSpace(Agent.Name)) throw new InvalidDataException("agent.name must be configured.");
        if (Agent.MaxMessages < 1) throw new InvalidDataException("agent.maxMessages must be positive.");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("${", StringComparison.Ordinal))
            throw new InvalidDataException($"{name} must be configured in config.local.yaml or its environment variable.");
    }

    private static readonly Regex EnvironmentVariablePattern = new(
        @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}

internal sealed class DiscordSettings
{
    public string Token { get; init; } = string.Empty;
    public ulong ChannelId { get; init; }
    public ulong? ParticipantUserId { get; init; }
}

internal sealed class OpenAiSettings
{
    public string Endpoint { get; init; } = "https://api.openai.com/";
    public string ApiKey { get; init; } = string.Empty;
    public string ChatModel { get; init; } = "gpt-4o-mini";
    public string? ReasoningEffort { get; init; } = "medium";
    public string EmbeddingModel { get; init; } = "text-embedding-3-small";
    public int EmbeddingDimensions { get; init; } = 1536;
}

internal sealed class PostgresSettings
{
    public string ConnectionString { get; init; } = string.Empty;
    public string TableName { get; init; } = "mem0sharp_agent_memories";
    public int EmbeddingDimensions { get; init; } = 1536;
    public bool CreateExtension { get; init; } = true;
    public bool UseHnswIndex { get; init; } = true;
}

internal sealed class AgentSettings
{
    public string Name { get; init; } = "mem0sharp-agent";
    public string SystemPrompt { get; init; } = "You are a rigorous, curious debate partner. Address the other agent's strongest point, distinguish facts from assumptions, and keep replies concise enough for Discord.";
    public int MemoryTopK { get; init; } = 8;
    public bool SelfDebate { get; init; }
    public int MaxMessages { get; init; } = 10;
}
