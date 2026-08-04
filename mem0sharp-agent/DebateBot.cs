using Discord;
using Discord.WebSocket;
using Mem0Sharp;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class DebateBot : IDisposable
{
    private const string SelfDebateAgent1 = "agent1";
    private const string SelfDebateAgent2 = "agent2";
    private readonly AgentConfiguration configuration;
    private readonly MemoryService memory;
    private readonly OpenAiToolClient chat;
    private readonly DiscordSocketClient client;
    private readonly SemaphoreSlim debateLock = new(1, 1);
    private ulong? topicMessageId;
    private string? topic;
    private bool readyInitializationFailed;
    private bool disposed;

    public DebateBot(AgentConfiguration configuration, MemoryService memory, OpenAiToolClient chat)
    {
        this.configuration = configuration;
        this.memory = memory;
        this.chat = chat;
        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info
        });
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        client.Log += LogAsync;
        client.Ready += HandleReadyAsync;
        client.MessageReceived += HandleMessageReceivedAsync;

        await client.LoginAsync(TokenType.Bot, configuration.Discord.Token);
        await client.StartAsync();
        Console.WriteLine($"{configuration.Agent.Name} is listening on channel {configuration.Discord.ChannelId}.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await client.StopAsync();
            await client.LogoutAsync();
        }
    }

    private async Task HandleReadyAsync()
    {
        if (topicMessageId.HasValue || readyInitializationFailed)
            return;

        try
        {
            var channel = await GetTargetChannelAsync();
            var firstMessage = await GetOldestMessageAsync(channel);
            if (firstMessage is null)
                throw new InvalidOperationException("The configured Discord channel has no first message to use as a topic.");

            topicMessageId = firstMessage.Id;
            topic = firstMessage.Content.Trim();
            if (string.IsNullOrWhiteSpace(topic))
                throw new InvalidOperationException("The channel's first message must contain the debate topic.");

            var generatedOpening = await GenerateReplyAsync(
                topic,
                "No opposing argument has been posted yet.",
                "Open the debate with your initial position and one question for the other agent. Use the memory tools to search relevant context and store any durable debate memory.",
                configuration.Agent.SelfDebate ? SelfDebateAgent1 : configuration.Agent.Name);
            var opening = generatedOpening.Text;
            var labeledOpening = $"{SelfDebateLabel(SelfDebateAgent1)} {opening}";
            var openingMessage = configuration.Agent.SelfDebate ? labeledOpening : opening;
            await channel.SendMessageAsync(FormatDiscordMessage(openingMessage, generatedOpening));
            Console.WriteLine($"Loaded topic from message {firstMessage.Id}.");

            if (configuration.Agent.SelfDebate)
                await RunSelfDebateAsync(channel, labeledOpening);
        }
        catch (Discord.Net.HttpException exception) when (exception.Message.Contains("50001", StringComparison.Ordinal))
        {
            readyInitializationFailed = true;
            Console.Error.WriteLine($"Discord denied access to channel {configuration.Discord.ChannelId} (50001 Missing Access).");
            Console.Error.WriteLine("Invite this bot to the channel's server and grant View Channel, Read Message History, and Send Messages permissions.");
            Console.Error.WriteLine($"Accessible servers for this bot: {FormatGuilds()}");
        }
        catch (Exception exception)
        {
            readyInitializationFailed = true;
            Console.Error.WriteLine($"Could not initialize the debate channel {configuration.Discord.ChannelId}: {exception.Message}");
        }
    }

    private async Task HandleMessageReceivedAsync(SocketMessage message)
    {
        if (configuration.Agent.SelfDebate)
            return;

        if (message.Channel.Id != configuration.Discord.ChannelId ||
            message.Author.Id == client.CurrentUser?.Id ||
            message.Id == topicMessageId ||
            string.IsNullOrWhiteSpace(message.Content))
            return;

        if (configuration.Discord.ParticipantUserId.HasValue &&
            message.Author.Id != configuration.Discord.ParticipantUserId.Value)
            return;

        if (topic is null)
            return;

        await debateLock.WaitAsync();
        try
        {
            var generatedReply = await GenerateReplyAsync(
                topic,
                $"{message.Author.Username}: {message.Content.Trim()}",
                "Respond to the other agent's latest argument. Challenge weak assumptions, concede valid points, and advance the discussion. Use memory tools when relevant: search before changing existing memories, add durable new memories, update stale memories, and delete memories that are clearly invalid or no longer useful.");
            var reply = generatedReply.Text;
            await message.Channel.SendMessageAsync(FormatDiscordMessage(reply, generatedReply));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not answer message {message.Id}: {exception.Message}");
        }
        finally
        {
            debateLock.Release();
        }
    }

    private async Task RunSelfDebateAsync(IMessageChannel channel, string opening)
    {
        await debateLock.WaitAsync();
        try
        {
            var messageCount = 1;
            var previousArgument = opening;
            while (messageCount < configuration.Agent.MaxMessages)
            {
                var turnNumber = messageCount + 1;
                var participant = turnNumber % 2 == 0 ? SelfDebateAgent2 : SelfDebateAgent1;
                var role = participant == SelfDebateAgent2 ? "challenger" : "advocate";
                var generatedReply = await GenerateReplyAsync(
                    topic!,
                    $"Previous turn ({messageCount}): {previousArgument}",
                    $"This is self-debate turn {turnNumber}. You are {participant}, the {role}. Respond to the previous turn, make one clear argument, and end with a point the next turn can address. Use memory tools when relevant: search before changing existing memories, add durable new memories, update stale memories, and delete memories that are clearly invalid or no longer useful.",
                    participant,
                    includeTopic: turnNumber <= 2);
                var reply = generatedReply.Text;
                var labeledReply = $"{SelfDebateLabel(participant)} {reply}";
                await channel.SendMessageAsync(FormatDiscordMessage(labeledReply, generatedReply));

                previousArgument = labeledReply;
                messageCount++;
            }

            Console.WriteLine($"Self-debate stopped after {messageCount} agent messages (max: {configuration.Agent.MaxMessages}).");
        }
        finally
        {
            debateLock.Release();
        }
    }

    private async Task<GeneratedReply> GenerateReplyAsync(string debateTopic, string latestArgument, string instruction, string? agentId = null, bool includeTopic = true)
    {
        agentId ??= configuration.Agent.Name;
        var topicContext = includeTopic ? $"Topic: {debateTopic}\n\n" : string.Empty;
        var systemContext = $"{configuration.Agent.SystemPrompt} Your identity for this turn is {agentId}. You have memory tools scoped to this Discord channel, agent, and session. Use them deliberately before producing your final response.";
        var userContext = $"{topicContext}Latest argument: {latestArgument}\n\nTask: {instruction}";
        var initialContext = $"[system]\n{systemContext}\n\n[user]\n{userContext}";
        var input = new List<OpenAiResponseInput>
        {
            new(Role: "system", Content: systemContext),
            new(Role: "user", Content: userContext)
        };
        var searchedMemories = new List<SearchResult>();
        var searchUsages = new List<MemorySearchUsage>();
        var memoryActions = new List<MemoryActionResult>();
        var surfacedMemoryIds = new HashSet<string>(StringComparer.Ordinal);
        string? previousResponseId = null;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var completion = await chat.CompleteAsync(input, MemoryTools, previousResponseId);
            if (completion.ToolCalls.Count == 0)
            {
                return new GeneratedReply(
                    string.IsNullOrWhiteSpace(completion.Text) ? "I could not form a response to that argument." : completion.Text.Trim(),
                    initialContext,
                    searchedMemories,
                    searchUsages,
                    memoryActions);
            }

            if (string.IsNullOrWhiteSpace(completion.Id))
                throw new InvalidDataException("OpenAI returned tool calls without a response ID.");
            previousResponseId = completion.Id;
            input.Clear();

            foreach (var toolCall in completion.ToolCalls)
            {
                var result = await ExecuteMemoryToolAsync(
                    toolCall,
                    agentId,
                    searchedMemories,
                    searchUsages,
                    memoryActions,
                    surfacedMemoryIds);
                input.Add(new OpenAiResponseInput(
                    Type: "function_call_output",
                    CallId: toolCall.CallId,
                    Output: JsonSerializer.Serialize(result)));
            }
        }

        return new GeneratedReply(
            "I could not complete the memory operations for this turn.",
            initialContext,
            searchedMemories,
            searchUsages,
            memoryActions);
    }

    private async Task<object> ExecuteMemoryToolAsync(
        OpenAiFunctionCall toolCall,
        string agentId,
        List<SearchResult> searchedMemories,
        List<MemorySearchUsage> searchUsages,
        List<MemoryActionResult> memoryActions,
        HashSet<string> surfacedMemoryIds)
    {
        try
        {
            var arguments = JsonNode.Parse(toolCall.Arguments)?.AsObject()
                ?? throw new InvalidDataException("Tool arguments must be a JSON object.");
            return toolCall.Name switch
            {
                "search_memories" => await SearchMemoriesAsync(arguments, agentId, searchedMemories, searchUsages, surfacedMemoryIds),
                "add_memory" => await AddMemoryAsync(arguments, agentId, memoryActions),
                "update_memory" => await UpdateMemoryAsync(arguments, agentId, memoryActions, surfacedMemoryIds),
                "delete_memory" => await DeleteMemoryAsync(arguments, agentId, memoryActions, surfacedMemoryIds),
                _ => new { success = false, error = $"Unknown memory tool '{toolCall.Name}'." }
            };
        }
        catch (Exception exception)
        {
            return new { success = false, error = exception.Message };
        }
    }

    private async Task<object> SearchMemoriesAsync(
        JsonObject arguments,
        string agentId,
        List<SearchResult> searchedMemories,
        List<MemorySearchUsage> searchUsages,
        HashSet<string> surfacedMemoryIds)
    {
        var query = RequiredString(arguments, "query");
        var topK = arguments["top_k"]?.GetValue<int>() ?? configuration.Agent.MemoryTopK;
        topK = Math.Clamp(topK, 1, 20);
        var results = await memory.SearchAsync(
            query,
            new MemoryFilter(UserId: MemoryUserId, AgentId: agentId, RunId: RunId, Scope: MemoryScope.Session),
            topK);
        searchUsages.Add(new MemorySearchUsage(query, results));
        foreach (var result in results)
        {
            if (surfacedMemoryIds.Add(result.Memory.Id))
                searchedMemories.Add(result);
        }

        return new
        {
            success = true,
            memories = results.Select(result => new
            {
                id = result.Memory.Id,
                text = result.Memory.Text,
                score = Math.Round(result.Score, 3)
            })
        };
    }

    private async Task<object> AddMemoryAsync(JsonObject arguments, string agentId, List<MemoryActionResult> memoryActions)
    {
        var text = RequiredString(arguments, "text");
        var result = await memory.AddAsync(text, new MemoryAddOptions
        {
            UserId = MemoryUserId,
            AgentId = agentId,
            RunId = RunId,
            Scope = MemoryScope.Session,
            Infer = false,
            Deduplicate = true,
            Metadata = new Dictionary<string, string> { ["source"] = "discord-memory-tool" }
        });
        RecordMemoryActions(result, memoryActions);
        return new { success = true, actions = result.Actions ?? [] };
    }

    private async Task<object> UpdateMemoryAsync(
        JsonObject arguments,
        string agentId,
        List<MemoryActionResult> memoryActions,
        HashSet<string> surfacedMemoryIds)
    {
        var memoryId = RequiredString(arguments, "memory_id");
        var text = RequiredString(arguments, "text");
        var existing = await GetToolMemoryAsync(memoryId, agentId, surfacedMemoryIds);
        if (existing is null)
            return new { success = false, error = "The memory must be returned by search_memories and belong to the current scope." };

        var updated = await memory.UpdateAsync(memoryId, text);
        var action = new MemoryActionResult(updated.Id, updated.Text, MemoryAction.Update);
        memoryActions.Add(action);
        return new { success = true, action };
    }

    private async Task<object> DeleteMemoryAsync(
        JsonObject arguments,
        string agentId,
        List<MemoryActionResult> memoryActions,
        HashSet<string> surfacedMemoryIds)
    {
        var memoryId = RequiredString(arguments, "memory_id");
        var existing = await GetToolMemoryAsync(memoryId, agentId, surfacedMemoryIds);
        if (existing is null)
            return new { success = false, error = "The memory must be returned by search_memories and belong to the current scope." };

        await memory.DeleteAsync(memoryId);
        var action = new MemoryActionResult(existing.Id, existing.Text, MemoryAction.Delete);
        memoryActions.Add(action);
        return new { success = true, action };
    }

    private async Task<Memory?> GetToolMemoryAsync(string memoryId, string agentId, HashSet<string> surfacedMemoryIds)
    {
        if (!surfacedMemoryIds.Contains(memoryId))
            return null;
        var existing = await memory.GetAsync(memoryId);
        return existing is not null &&
            existing.UserId == MemoryUserId &&
            existing.AgentId == agentId &&
            existing.RunId == RunId &&
            existing.Scope == MemoryScope.Session
            ? existing
            : null;
    }

    private static string RequiredString(JsonObject arguments, string name)
    {
        var value = arguments[name]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Tool argument '{name}' is required.")
            : value.Trim();
    }

    private static void RecordMemoryActions(AddResult result, List<MemoryActionResult> memoryActions)
    {
        if (result.Actions is not null)
        {
            memoryActions.AddRange(result.Actions.Where(action => action.Event != MemoryAction.None));
            return;
        }

        memoryActions.AddRange(result.Memories.Select(memory => new MemoryActionResult(memory.Id, memory.Text, MemoryAction.Add)));
    }

    private static string FormatMemoryMetadata(GeneratedReply reply)
    {
        var searched = reply.SearchUsages.Count == 0
            ? "none"
            : string.Join(" | ", reply.SearchUsages.Select(search =>
                $"search_memories(\"{FormatMemoryText(search.Query)}\") -> {FormatSearchResults(search.Results)}"));
        var searchedCount = reply.SearchUsages.Sum(search => search.Results.Count);
        var addedActions = reply.MemoryActions.Where(action => action.Event == MemoryAction.Add).ToArray();
        var updatedActions = reply.MemoryActions.Where(action => action.Event == MemoryAction.Update).ToArray();
        var deletedActions = reply.MemoryActions.Where(action => action.Event == MemoryAction.Delete).ToArray();

        return $"\n\n```text\n---\n[system] Mem0Sharp memory metadata\nContext:\n{FormatContext(reply.Context)}\nSearched ({searchedCount}): {searched}\nAdded ({addedActions.Length}): {FormatMemoryActions(addedActions)}\nUpdated ({updatedActions.Length}): {FormatMemoryActions(updatedActions)}\nDeleted ({deletedActions.Length}): {FormatMemoryActions(deletedActions)}\n```";
    }

    private static string FormatSearchResults(IReadOnlyList<SearchResult> results) => results.Count == 0
        ? "none"
        : string.Join(", ", results.Select(result => $"\"{FormatMemoryText(result.Memory.Text)}\" ({result.Score:F3})"));

    private static string FormatMemoryActions(IEnumerable<MemoryActionResult> actions)
    {
        var values = actions.Select(action => string.IsNullOrWhiteSpace(action.Memory)
            ? "(text unavailable)"
            : $"\"{FormatMemoryText(action.Memory)}\"");
        var formatted = string.Join(", ", values);
        return string.IsNullOrEmpty(formatted) ? "none" : formatted;
    }

    private static string FormatMemoryText(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const int maxMemoryTextLength = 240;
        return normalized.Length <= maxMemoryTextLength
            ? normalized
            : normalized[..(maxMemoryTextLength - 3)] + "...";
    }

    private static string FormatContext(string context)
    {
        var sanitized = context.Replace("```", "'''", StringComparison.Ordinal).Trim();
        const int maxContextLength = 800;
        return sanitized.Length <= maxContextLength
            ? sanitized
            : sanitized[..(maxContextLength - 3)] + "...";
    }

    private static string FormatDiscordMessage(string content, GeneratedReply reply)
    {
        var metadata = FormatMemoryMetadata(reply);
        const int discordMessageLimit = 2000;
        var contentLimit = discordMessageLimit - metadata.Length;
        if (content.Length > contentLimit)
        {
            content = content[..Math.Max(0, contentLimit - 3)] + "...";
        }

        return content + metadata;
    }

    private async Task<IMessageChannel> GetTargetChannelAsync()
    {
        var channel = client.GetChannel(configuration.Discord.ChannelId) as IMessageChannel
            ?? await client.GetChannelAsync(configuration.Discord.ChannelId) as IMessageChannel;
        return channel ?? throw new InvalidOperationException($"Discord channel {configuration.Discord.ChannelId} was not found or is not a message channel.");
    }

    private static async Task<IMessage?> GetOldestMessageAsync(IMessageChannel channel)
    {
        var page = (await channel.GetMessagesAsync(100).FlattenAsync()).ToArray();
        while (page.Length > 0)
        {
            var oldest = page.OrderBy(message => message.Timestamp).First();
            if (page.Length < 100)
                return oldest;

            var olderPage = (await channel.GetMessagesAsync(oldest.Id, Direction.Before, 100).FlattenAsync()).ToArray();
            if (olderPage.Length == 0)
                return oldest;
            page = olderPage;
        }

        return null;
    }

    private Task LogAsync(LogMessage message)
    {
        var output = $"Discord {message.Severity}: {message.Source}: {message.Message}";
        if (message.Exception is null)
            Console.WriteLine(output);
        else
            Console.Error.WriteLine($"{output} {message.Exception.Message}");
        return Task.CompletedTask;
    }

    private string FormatGuilds() => client.Guilds.Count == 0
        ? "none"
        : string.Join(", ", client.Guilds.Select(guild => $"{guild.Name} ({guild.Id})"));

    private static string SelfDebateLabel(string participant) => $"**[{participant}]**";

    private const int MaxToolRounds = 8;
    private sealed record GeneratedReply(
        string Text,
        string Context,
        IReadOnlyList<SearchResult> SearchedMemories,
        IReadOnlyList<MemorySearchUsage> SearchUsages,
        IReadOnlyList<MemoryActionResult> MemoryActions);

    private sealed record MemorySearchUsage(string Query, IReadOnlyList<SearchResult> Results);

    private static readonly IReadOnlyList<OpenAiToolDefinition> MemoryTools =
    [
        new("function",
            "search_memories",
            "Search scoped debate memories before deciding whether existing memory should be changed.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "The debate claim or question to search for." },
                    ["top_k"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20 }
                },
                ["required"] = new JsonArray("query")
            }),
        new("function",
            "add_memory",
            "Store a concise durable debate fact or conclusion. Do not store the entire generated reply.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "A concise durable memory." }
                },
                ["required"] = new JsonArray("text")
            }),
        new("function",
            "update_memory",
            "Replace an existing scoped memory only when the new debate evidence makes it stale or incorrect. Use an ID returned by search_memories.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["memory_id"] = new JsonObject { ["type"] = "string" },
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "The corrected concise memory text." }
                },
                ["required"] = new JsonArray("memory_id", "text")
            }),
        new("function",
            "delete_memory",
            "Delete an existing scoped memory only when it is clearly invalid, obsolete, or a duplicate. Use an ID returned by search_memories.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["memory_id"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("memory_id")
            })
    ];

    private string MemoryUserId => $"discord-channel-{configuration.Discord.ChannelId}";
    private string RunId => $"{configuration.Agent.Name}-{configuration.Discord.ChannelId}";

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        debateLock.Dispose();
        client.Dispose();
    }
}
