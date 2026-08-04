using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

[McpServerToolType]
internal sealed class DiscordDebateTools(DiscordDebateService discord)
{
    [McpServerTool(Name = "get_debate_messages", ReadOnly = true)]
    [Description("Read the chronological Discord messages for a debate channel, including the agent replies and Mem0Sharp metadata boxes.")]
    public async Task<IReadOnlyList<DiscordMessageSnapshot>> GetDebateMessagesAsync(
        [Description("Optional Discord channel ID. Uses the configured channel when omitted.")] ulong? channel_id = null,
        [Description("Maximum number of messages to return, from 1 to 5000.")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 5000);
        return await discord.GetMessagesAsync(channel_id, limit, cancellationToken);
    }

    [McpServerTool(Name = "analyze_debate_memory_usage", ReadOnly = true)]
    [Description("Pull a Discord debate and produce an analysis-ready report of Mem0Sharp searches and memory mutations for labeled agent turns.")]
    public async Task<DebateMemoryUsageReport> AnalyzeDebateMemoryUsageAsync(
        [Description("Optional Discord channel ID. Uses the configured channel when omitted.")] ulong? channel_id = null,
        [Description("Label used by the first debating agent, including formatting such as **[agent1]**.")] string agent1_label = "**[agent1]**",
        [Description("Label used by the second debating agent, including formatting such as **[agent2]**.")] string agent2_label = "**[agent2]**",
        [Description("Maximum number of messages to inspect, from 1 to 5000.")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var messages = await discord.GetMessagesAsync(channel_id, limit, cancellationToken);
        var turns = new List<DebateTurnMemoryUsage>();
        var metadataCount = 0;
        var truncatedMetadataCount = 0;

        for (var index = 0; index < messages.Count; index++)
        {
            var metadata = ParseMetadata(messages[index].Content);
            if (metadata is null)
                continue;

            metadataCount++;
            if (metadata.IsTruncated)
                truncatedMetadataCount++;

            var argument = FindPreviousArgument(messages, index, agent1_label, agent2_label);
            turns.Add(new DebateTurnMemoryUsage(
                argument?.Agent,
                argument?.MessageId,
                argument?.Timestamp,
                argument?.Text,
                messages[index].Id,
                messages[index].Timestamp,
                metadata.SearchCount,
                metadata.EmptySearchCount,
                metadata.AddedCount,
                metadata.UpdatedCount,
                metadata.DeletedCount,
                metadata.SearchSummary,
                metadata.AddedSummary,
                metadata.UpdatedSummary,
                metadata.DeletedSummary,
                metadata.IsTruncated));
        }

        var analyzedTurns = turns.Count;
        var searchedTurns = turns.Count(turn => turn.SearchCount > 0);
        var emptySearches = turns.Sum(turn => turn.EmptySearchCount);
        var mutations = turns.Sum(turn => turn.AddedCount + turn.UpdatedCount + turn.DeletedCount);
        var agentSummaries = new[] { agent1_label, agent2_label }
            .Distinct(StringComparer.Ordinal)
            .Select(label => SummarizeAgent(label, turns))
            .ToArray();

        return new DebateMemoryUsageReport(
            messages.Count,
            metadataCount,
            turns.Count(turn => turn.Agent is null),
            truncatedMetadataCount,
            analyzedTurns == 0 ? 0 : Math.Round((double)searchedTurns / analyzedTurns, 3),
            analyzedTurns == 0 ? 0 : Math.Round((double)mutations / analyzedTurns, 3),
            emptySearches,
            mutations,
            agentSummaries,
            turns);
    }

    private static AgentMemoryUsageSummary SummarizeAgent(
        string label,
        IReadOnlyList<DebateTurnMemoryUsage> turns)
    {
        var agentTurns = turns.Where(turn => string.Equals(turn.Agent, label, StringComparison.Ordinal)).ToArray();
        return new AgentMemoryUsageSummary(
            label,
            agentTurns.Length,
            agentTurns.Count(turn => turn.SearchCount > 0),
            agentTurns.Sum(turn => turn.SearchCount),
            agentTurns.Sum(turn => turn.AddedCount),
            agentTurns.Sum(turn => turn.UpdatedCount),
            agentTurns.Sum(turn => turn.DeletedCount));
    }

    private static ArgumentMessage? FindPreviousArgument(
        IReadOnlyList<DiscordMessageSnapshot> messages,
        int metadataIndex,
        string agent1Label,
        string agent2Label)
    {
        for (var index = metadataIndex - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (ParseMetadata(message.Content) is not null)
                continue;

            var agent = message.Content.StartsWith(agent1Label, StringComparison.Ordinal)
                ? agent1Label
                : message.Content.StartsWith(agent2Label, StringComparison.Ordinal)
                    ? agent2Label
                    : null;
            return new ArgumentMessage(
                agent,
                message.Id,
                message.Timestamp,
                message.Content);
        }

        return null;
    }

    private static ParsedMetadata? ParseMetadata(string content)
    {
        const string marker = "[system] Mem0Sharp memory metadata";
        if (!content.Contains(marker, StringComparison.Ordinal))
            return null;

        var searchLine = FindCountedLine(content, "Searched");
        var addedLine = FindCountedLine(content, "Added");
        var updatedLine = FindCountedLine(content, "Updated");
        var deletedLine = FindCountedLine(content, "Deleted");
        return new ParsedMetadata(
            searchLine.Count,
            CountEmptySearches(searchLine.Summary),
            addedLine.Count,
            updatedLine.Count,
            deletedLine.Count,
            searchLine.Summary,
            addedLine.Summary,
            updatedLine.Summary,
            deletedLine.Summary,
            !content.TrimEnd().EndsWith("```", StringComparison.Ordinal));
    }

    private static CountedLine FindCountedLine(string content, string name)
    {
        var match = Regex.Match(
            content,
            $"^{Regex.Escape(name)} \\((?<count>\\d+)\\): (?<summary>.*)$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success
            ? new CountedLine(int.Parse(match.Groups["count"].Value), match.Groups["summary"].Value.Trim())
            : new CountedLine(0, "unavailable");
    }

    private static int CountEmptySearches(string summary) =>
        summary.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? 1
            : Regex.Matches(summary, "-> none", RegexOptions.CultureInvariant).Count;

    private sealed record ArgumentMessage(string? Agent, ulong MessageId, DateTimeOffset Timestamp, string Text);
    private sealed record CountedLine(int Count, string Summary);
    private sealed record ParsedMetadata(
        int SearchCount,
        int EmptySearchCount,
        int AddedCount,
        int UpdatedCount,
        int DeletedCount,
        string SearchSummary,
        string AddedSummary,
        string UpdatedSummary,
        string DeletedSummary,
        bool IsTruncated);
}

internal sealed record DebateMemoryUsageReport(
    int MessageCount,
    int MetadataMessageCount,
    int UnlabeledMetadataCount,
    int TruncatedMetadataCount,
    double TurnsWithSearchCoverage,
    double AverageMutationsPerTurn,
    int EmptySearchCount,
    int TotalMutationCount,
    IReadOnlyList<AgentMemoryUsageSummary> Agents,
    IReadOnlyList<DebateTurnMemoryUsage> Turns);

internal sealed record AgentMemoryUsageSummary(
    string Label,
    int TurnCount,
    int TurnsWithSearch,
    int SearchCount,
    int AddedCount,
    int UpdatedCount,
    int DeletedCount);

internal sealed record DebateTurnMemoryUsage(
    string? Agent,
    ulong? ArgumentMessageId,
    DateTimeOffset? ArgumentTimestamp,
    string? Argument,
    ulong MetadataMessageId,
    DateTimeOffset MetadataTimestamp,
    int SearchCount,
    int EmptySearchCount,
    int AddedCount,
    int UpdatedCount,
    int DeletedCount,
    string SearchSummary,
    string AddedSummary,
    string UpdatedSummary,
    string DeletedSummary,
    bool MetadataWasTruncated);
