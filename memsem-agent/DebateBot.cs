using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Net;
using Discord.WebSocket;

internal sealed class DebateBot : IDisposable
{
	private sealed record GeneratedReply(string Text, string Context, IReadOnlyList<MemsemSearchResult> SearchedMemories, IReadOnlyList<MemorySearchUsage> SearchUsages, IReadOnlyList<MemsemActionResult> MemoryActions);

	private sealed record MemorySearchUsage(string Query, IReadOnlyList<MemsemSearchResult> Results);

	private const string SelfDebateAgent1 = "agent1";

	private const string SelfDebateAgent2 = "agent2";

	private readonly AgentConfiguration configuration;

	private readonly MemsemMemoryClient memory;

	private readonly OpenAiToolClient chat;

	private readonly DiscordSocketClient client;

	private readonly SemaphoreSlim debateLock = new SemaphoreSlim(1, 1);

	private ulong? topicMessageId;

	private string? topic;

	private bool readyInitializationFailed;

	private bool disposed;

	private const int MaxToolRounds = 8;

	private static readonly IReadOnlyList<OpenAiToolDefinition> MemoryTools;

	public DebateBot(AgentConfiguration configuration, MemsemMemoryClient memory, OpenAiToolClient chat)
	{
		this.configuration = configuration;
		this.memory = memory;
		this.chat = chat;
		client = new DiscordSocketClient(new DiscordSocketConfig
		{
			GatewayIntents = (GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent),
			LogLevel = LogSeverity.Info
		});
	}

	public async Task RunAsync(CancellationToken cancellationToken = default(CancellationToken))
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
		{
			return;
		}
		try
		{
			IMessageChannel channel = await GetTargetChannelAsync();
			IMessage firstMessage = await GetOldestMessageAsync(channel);
			if (firstMessage == null)
			{
				throw new InvalidOperationException("The configured Discord channel has no first message to use as a topic.");
			}
			topicMessageId = firstMessage.Id;
			topic = firstMessage.Content.Trim();
			if (string.IsNullOrWhiteSpace(topic))
			{
				throw new InvalidOperationException("The channel's first message must contain the debate topic.");
			}
			IMessage[] recentMessages = await GetRecentMessagesAsync(channel);
			if (!configuration.Agent.ResetMemoryOnStart && recentMessages.Length > 1)
			{
				IMessage latestDebateMessage = (configuration.Agent.SelfDebate ? recentMessages.LastOrDefault((IMessage message) => IsSelfDebateMessage(message.Content)) : recentMessages[^1]);
				if (latestDebateMessage != null)
				{
					await ContinueExistingDebateAsync(channel, latestDebateMessage);
					Console.WriteLine($"Resumed the debate from message {latestDebateMessage.Id}.");
				}
				return;
			}
			GeneratedReply generatedOpening = await GenerateReplyAsync(topic, "No opposing argument has been posted yet.", "Open the debate with your initial position and one question for the other agent. Use the memory tools to search relevant context and store any durable debate memory.", configuration.Agent.SelfDebate ? "agent1" : configuration.Agent.Name);
			string opening = generatedOpening.Text;
			string labeledOpening = SelfDebateLabel("agent1") + " " + opening;
			string openingMessage = (configuration.Agent.SelfDebate ? labeledOpening : opening);
			await SendDiscordReplyAsync(channel, openingMessage, generatedOpening);
			Console.WriteLine($"Loaded topic from message {firstMessage.Id}.");
			if (configuration.Agent.SelfDebate)
			{
				await RunSelfDebateAsync(channel, labeledOpening);
			}
		}
		catch (HttpException ex) when (ex.Message.Contains("50001", StringComparison.Ordinal))
		{
			readyInitializationFailed = true;
			Console.Error.WriteLine($"Discord denied access to channel {configuration.Discord.ChannelId} (50001 Missing Access).");
			Console.Error.WriteLine("Invite this bot to the channel's server and grant View Channel, Read Message History, and Send Messages permissions.");
			Console.Error.WriteLine("Accessible servers for this bot: " + FormatGuilds());
		}
		catch (Exception ex2)
		{
			readyInitializationFailed = true;
			Console.Error.WriteLine($"Could not initialize the debate channel {configuration.Discord.ChannelId}: {ex2.Message}");
		}
	}

	private async Task HandleMessageReceivedAsync(SocketMessage message)
	{
		if (configuration.Agent.SelfDebate || message.Channel.Id != configuration.Discord.ChannelId || message.Id == topicMessageId || string.IsNullOrWhiteSpace(message.Content) || (configuration.Discord.ParticipantUserId.HasValue && message.Author.Id != configuration.Discord.ParticipantUserId.Value) || topic == null)
		{
			return;
		}
		bool isCurrentBotMessage = message.Author.Id == client.CurrentUser?.Id;
		if (isCurrentBotMessage && !HasPrefix(message.Content, configuration.Agent.PeerPrefix))
		{
			return;
		}
		await debateLock.WaitAsync();
		try
		{
			GeneratedReply generatedReply = await GenerateReplyAsync(topic, message.Author.Username + ": " + message.Content.Trim(), "Respond to the other agent's latest argument. Challenge weak assumptions, concede valid points, and advance the discussion. Use memory tools when relevant: search before changing existing memories, add durable new memories, update stale memories, and delete memories that are clearly invalid or no longer useful.");
			await SendDiscordReplyAsync(content: generatedReply.Text, channel: message.Channel, reply: generatedReply);
		}
		catch (Exception ex)
		{
			Exception exception = ex;
			Console.Error.WriteLine($"Could not answer message {message.Id}: {exception.Message}");
		}
		finally
		{
			debateLock.Release();
		}
	}

	private async Task ContinueExistingDebateAsync(IMessageChannel channel, IMessage latestMessage)
	{
		string previousArgument = ExtractDebateArgument(latestMessage.Content);
		if (configuration.Agent.SelfDebate)
		{
			await RunSelfDebateAsync(channel, previousArgument, 0, includeTopic: false);
			return;
		}
		string participant = configuration.Agent.Name;
		GeneratedReply generatedReply = await GenerateReplyAsync(topic, latestMessage.Author.Username + ": " + previousArgument, "Continue the existing debate from the latest argument. Do not reopen the debate or restate an opening position. Build on the established context and use memory tools when relevant: search before changing existing memories, add durable new memories, update stale memories, and delete memories that are clearly invalid or no longer useful.", participant, includeTopic: false);
		string reply = (configuration.Agent.SelfDebate ? (SelfDebateLabel(participant) + " " + generatedReply.Text) : generatedReply.Text);
		await SendDiscordReplyAsync(channel, reply, generatedReply);
	}

	private async Task RunSelfDebateAsync(IMessageChannel channel, string previousArgument, int messageCount = 1, bool includeTopic = true)
	{
		await debateLock.WaitAsync();
		try
		{
			string participant = NextSelfDebateParticipant(previousArgument);
			while (messageCount < configuration.Agent.MaxMessages)
			{
				int turnNumber = messageCount + 1;
				string role = ((participant == "agent2") ? "challenger" : "advocate");
				GeneratedReply generatedReply = await GenerateReplyAsync(topic, $"Previous turn ({messageCount}): {previousArgument}", $"This is self-debate turn {turnNumber}. You are {participant}, the {role}. Respond to the previous turn, make one clear argument, and end with a point the next turn can address. Use memory tools when relevant: search before changing existing memories, add durable new memories, update stale memories, and delete memories that are clearly invalid or no longer useful.", participant, includeTopic && turnNumber <= 2);
				string labeledReply = string.Concat(str2: generatedReply.Text, str0: SelfDebateLabel(participant), str1: " ");
				await SendDiscordReplyAsync(channel, labeledReply, generatedReply);
				previousArgument = labeledReply;
				participant = ((participant == "agent1") ? "agent2" : "agent1");
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
		if (agentId == null)
		{
			agentId = configuration.Agent.Name;
		}
		string topicContext = (includeTopic ? ("Topic: " + debateTopic + "\n\n") : string.Empty);
		string memoryScopeDescription = (configuration.Agent.ResetMemoryOnStart ? ("the memsem project '" + memory.Project + "' for this debate run") : ("the persistent memsem project '" + memory.Project + "' shared across this agent's debate channels and sessions"));
		string systemContext = $"{configuration.Agent.SystemPrompt} Your identity for this turn is {agentId}. You have memory tools backed by memsem scoped to {memoryScopeDescription}. memsem stores atomic facts as subject → predicate → object triples (for example: opponent → conceded → 'correlation is not causation'), so write memories as short triples rather than full sentences. Use the tools deliberately before producing your final response.";
		string userContext = $"{topicContext}Latest argument: {latestArgument}\n\nTask: {instruction}";
		string initialContext = "[system]\n" + systemContext + "\n\n[user]\n" + userContext;
		List<OpenAiResponseInput> input = new List<OpenAiResponseInput>
		{
			new OpenAiResponseInput("system", systemContext),
			new OpenAiResponseInput("user", userContext)
		};
		List<MemsemSearchResult> searchedMemories = new List<MemsemSearchResult>();
		List<MemorySearchUsage> searchUsages = new List<MemorySearchUsage>();
		List<MemsemActionResult> memoryActions = new List<MemsemActionResult>();
		Dictionary<int, MemsemMemory> surfacedMemories = new Dictionary<int, MemsemMemory>();
		string previousResponseId = null;
		for (int round = 0; round < 8; round++)
		{
			OpenAiToolCompletion completion = await chat.CompleteAsync(input, MemoryTools, previousResponseId);
			if (completion.ToolCalls.Count == 0)
			{
				return new GeneratedReply(string.IsNullOrWhiteSpace(completion.Text) ? "I could not form a response to that argument." : completion.Text.Trim(), initialContext, searchedMemories, searchUsages, memoryActions);
			}
			if (string.IsNullOrWhiteSpace(completion.Id))
			{
				throw new InvalidDataException("OpenAI returned tool calls without a response ID.");
			}
			previousResponseId = completion.Id;
			input.Clear();
			foreach (OpenAiFunctionCall toolCall in completion.ToolCalls)
			{
				input.Add(new OpenAiResponseInput(Output: JsonSerializer.Serialize(await ExecuteMemoryToolAsync(toolCall, agentId, searchedMemories, searchUsages, memoryActions, surfacedMemories)), Role: null, Content: null, Type: "function_call_output", CallId: toolCall.CallId));
			}
		}
		return new GeneratedReply("I could not complete the memory operations for this turn.", initialContext, searchedMemories, searchUsages, memoryActions);
	}

	private async Task<object> ExecuteMemoryToolAsync(OpenAiFunctionCall toolCall, string agentId, List<MemsemSearchResult> searchedMemories, List<MemorySearchUsage> searchUsages, List<MemsemActionResult> memoryActions, Dictionary<int, MemsemMemory> surfacedMemories)
	{
		try
		{
			JsonObject arguments = JsonNode.Parse(toolCall.Arguments)?.AsObject() ?? throw new InvalidDataException("Tool arguments must be a JSON object.");
			string name = toolCall.Name;
			if (1 == 0)
			{
			}
			object result = name switch
			{
				"search_memories" => await SearchMemoriesAsync(arguments, searchedMemories, searchUsages, surfacedMemories), 
				"add_memory" => await AddMemoryAsync(arguments, agentId, memoryActions, surfacedMemories), 
				"update_memory" => await UpdateMemoryAsync(arguments, agentId, memoryActions, surfacedMemories), 
				"delete_memory" => await DeleteMemoryAsync(arguments, memoryActions, surfacedMemories), 
				_ => new
				{
					success = false,
					error = "Unknown memory tool '" + toolCall.Name + "'."
				}, 
			};
			if (1 == 0)
			{
			}
			return result;
		}
		catch (Exception ex)
		{
			Exception exception = ex;
			return new
			{
				success = false,
				error = exception.Message
			};
		}
	}

	private async Task<object> SearchMemoriesAsync(JsonObject arguments, List<MemsemSearchResult> searchedMemories, List<MemorySearchUsage> searchUsages, Dictionary<int, MemsemMemory> surfacedMemories)
	{
		string query = RequiredString(arguments, "query");
		int topK = arguments["top_k"]?.GetValue<int>() ?? configuration.Agent.MemoryTopK;
		topK = Math.Clamp(topK, 1, 20);
		IReadOnlyList<MemsemSearchResult> results = await memory.SearchAsync(query, topK, configuration.Memsem.RelaxSearch);
		searchUsages.Add(new MemorySearchUsage(query, results));
		foreach (MemsemSearchResult result in results)
		{
			if (!surfacedMemories.ContainsKey(result.Memory.Id))
			{
				searchedMemories.Add(result);
			}
			surfacedMemories[result.Memory.Id] = result.Memory;
		}
		return new
		{
			success = true,
			memories = results.Select((MemsemSearchResult memsemSearchResult) => new
			{
				id = memsemSearchResult.Memory.Id,
				subject = memsemSearchResult.Memory.Subject,
				predicate = memsemSearchResult.Memory.Predicate,
				@object = memsemSearchResult.Memory.Object,
				score = Math.Round(memsemSearchResult.Score, 3)
			})
		};
	}

	private async Task<object> AddMemoryAsync(JsonObject arguments, string agentId, List<MemsemActionResult> memoryActions, Dictionary<int, MemsemMemory> surfacedMemories)
	{
		string subject = RequiredString(arguments, "subject");
		string predicate = RequiredString(arguments, "predicate");
		string @object = RequiredString(arguments, "object");
		double? importance = arguments["importance"]?.GetValue<double>();
		string theme = arguments["theme"]?.GetValue<string>();
		List<string> tags = ReadTags(arguments);
		if (!tags.Contains<string>(agentId, StringComparer.OrdinalIgnoreCase))
		{
			tags.Add(agentId);
		}
		MemsemAddOutcome outcome = await memory.AddAsync(subject, predicate, @object, importance, theme, tags, agentId);
		memoryActions.Add(new MemsemActionResult(Memory: $"{subject} → {predicate} → {@object}", Id: outcome.Id, Event: MemsemAction.Add));
		surfacedMemories[outcome.Id] = new MemsemMemory(outcome.Id, subject, predicate, @object, tags.ToArray(), theme, 0.0, importance ?? 0.5);
		return new
		{
			success = true,
			id = outcome.Id,
			created = outcome.Created,
			conflict = outcome.Conflict,
			faded = outcome.Faded,
			archived = outcome.Archived
		};
	}

	private async Task<object> UpdateMemoryAsync(JsonObject arguments, string agentId, List<MemsemActionResult> memoryActions, Dictionary<int, MemsemMemory> surfacedMemories)
	{
		int memoryId = RequiredInt(arguments, "memory_id");
		if (!surfacedMemories.TryGetValue(memoryId, out MemsemMemory existing))
		{
			return new
			{
				success = false,
				error = "The memory must be returned by search_memories in this turn."
			};
		}
		string subject = OptionalString(arguments, "subject") ?? existing.Subject;
		string predicate = OptionalString(arguments, "predicate") ?? existing.Predicate;
		string @object = RequiredString(arguments, "object");
		double? importance = arguments["importance"]?.GetValue<double>();
		string theme = OptionalString(arguments, "theme") ?? existing.Theme;
		List<string> tags = ReadTags(arguments);
		string[] tags2 = existing.Tags;
		foreach (string tag in tags2)
		{
			if (!tags.Contains<string>(tag, StringComparer.OrdinalIgnoreCase))
			{
				tags.Add(tag);
			}
		}
		if (!tags.Contains<string>(agentId, StringComparer.OrdinalIgnoreCase))
		{
			tags.Add(agentId);
		}
		MemsemAddOutcome outcome = await memory.AddAsync(subject, predicate, @object, importance, theme, tags, agentId);
		memoryActions.Add(new MemsemActionResult(Memory: $"{subject} → {predicate} → {@object}", Id: outcome.Id, Event: MemsemAction.Update));
		surfacedMemories[outcome.Id] = new MemsemMemory(outcome.Id, subject, predicate, @object, tags.ToArray(), theme, 0.0, importance ?? existing.Importance);
		return new
		{
			success = true,
			id = outcome.Id,
			supersedes = memoryId,
			conflict = outcome.Conflict,
			faded = outcome.Faded,
			archived = outcome.Archived
		};
	}

	private async Task<object> DeleteMemoryAsync(JsonObject arguments, List<MemsemActionResult> memoryActions, Dictionary<int, MemsemMemory> surfacedMemories)
	{
		int memoryId = RequiredInt(arguments, "memory_id");
		if (!surfacedMemories.TryGetValue(memoryId, out MemsemMemory existing))
		{
			return new
			{
				success = false,
				error = "The memory must be returned by search_memories in this turn."
			};
		}
		if (!(await memory.ForgetAsync(memoryId)))
		{
			return new
			{
				success = false,
				error = $"memsem could not forget memory {memoryId}."
			};
		}
		memoryActions.Add(new MemsemActionResult(existing.Id, existing.Text, MemsemAction.Delete));
		surfacedMemories.Remove(memoryId);
		return new
		{
			success = true,
			id = memoryId,
			forgotten = true
		};
	}

	private static string RequiredString(JsonObject arguments, string name)
	{
		string text = arguments[name]?.GetValue<string>();
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}
		throw new InvalidDataException("Tool argument '" + name + "' is required.");
	}

	private static string? OptionalString(JsonObject arguments, string name)
	{
		string text = arguments[name]?.GetValue<string>();
		return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
	}

	private static int RequiredInt(JsonObject arguments, string name)
	{
		JsonNode jsonNode = arguments[name];
		if (jsonNode == null)
		{
			throw new InvalidDataException("Tool argument '" + name + "' is required.");
		}
		return jsonNode.GetValue<int>();
	}

	private static List<string> ReadTags(JsonObject arguments)
	{
		return (arguments["tags"] is JsonArray source) ? (from tag in source
			select tag?.GetValue<string>() into tag
			where !string.IsNullOrWhiteSpace(tag)
			select tag.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
	}

	private string FormatMemoryMetadata(GeneratedReply reply)
	{
		string value = ((reply.SearchUsages.Count == 0) ? "none" : string.Join(" | ", reply.SearchUsages.Select((MemorySearchUsage search) => "search_memories(\"" + FormatMemoryText(search.Query) + "\") -> " + FormatSearchResults(search.Results))));
		int value2 = reply.SearchUsages.Sum((MemorySearchUsage search) => search.Results.Count);
		MemsemActionResult[] array = reply.MemoryActions.Where((MemsemActionResult action) => action.Event == MemsemAction.Add).ToArray();
		MemsemActionResult[] array2 = reply.MemoryActions.Where((MemsemActionResult action) => action.Event == MemsemAction.Update).ToArray();
		MemsemActionResult[] array3 = reply.MemoryActions.Where((MemsemActionResult action) => action.Event == MemsemAction.Delete).ToArray();
		string text = $"```text\n---\n[system] Memsem memory metadata\nProvider: OpenAI\nModel: {configuration.OpenAi.ChatModel}\nThinking effort: {configuration.OpenAi.ReasoningEffort ?? "none"}\nMemory top K: {configuration.Agent.MemoryTopK}\nProject: {memory.Project}\nRelax search: {configuration.Memsem.RelaxSearch}\nContext:\n{FormatContext(reply.Context)}\nSearched ({value2}): {value}\nAdded ({array.Length}): {FormatMemoryActions(array)}\nUpdated ({array2.Length}): {FormatMemoryActions(array2)}\nDeleted ({array3.Length}): {FormatMemoryActions(array3)}\n```";
		return (text.Length <= 2000) ? text : (text.Substring(0, 2000 - "...\n```".Length) + "...\n```");
	}

	private static string FormatSearchResults(IReadOnlyList<MemsemSearchResult> results)
	{
		return (results.Count == 0) ? "none" : string.Join(", ", results.Select((MemsemSearchResult result) => $"\"{FormatMemoryText(result.Memory.Text)}\" ({result.Score:F3})"));
	}

	private static string FormatMemoryActions(IEnumerable<MemsemActionResult> actions)
	{
		IEnumerable<string> values = actions.Select((MemsemActionResult action) => string.IsNullOrWhiteSpace(action.Memory) ? "(text unavailable)" : ("\"" + FormatMemoryText(action.Memory) + "\""));
		string text = string.Join(", ", values);
		return string.IsNullOrEmpty(text) ? "none" : text;
	}

	private static string FormatMemoryText(string text)
	{
		string text2 = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		return (text2.Length <= 240) ? text2 : (text2.Substring(0, 237) + "...");
	}

	private static string FormatContext(string context)
	{
		string text = context.Replace("```", "'''", StringComparison.Ordinal).Trim();
		return (text.Length <= 800) ? text : (text.Substring(0, 797) + "...");
	}

	private static string ExtractDebateArgument(string content)
	{
		int num = content.IndexOf("\n\n```text\n---\n[system]", StringComparison.Ordinal);
		return ((num < 0) ? content : content.Substring(0, num)).Trim();
	}

	private static string FormatDiscordContent(string content)
	{
		if (content.Length > 2000)
		{
			content = content.Substring(0, 1997) + "...";
		}
		content = CloseUnterminatedCodeBlock(content);
		return content;
	}

	private async Task SendDiscordReplyAsync(IMessageChannel channel, string content, GeneratedReply reply)
	{
		await channel.SendMessageAsync(FormatDiscordContent(PrefixReply(content)));
		await channel.SendMessageAsync(FormatMemoryMetadata(reply));
	}

	private string PrefixReply(string content)
	{
		return configuration.Agent.SelfDebate || HasPrefix(content, configuration.Agent.ReplyPrefix)
			? content
			: $"{configuration.Agent.ReplyPrefix} {content}";
	}

	private static bool HasPrefix(string content, string prefix)
	{
		return content.TrimStart().StartsWith(prefix, StringComparison.Ordinal);
	}

	private static string CloseUnterminatedCodeBlock(string content)
	{
		int num = 0;
		int startIndex = 0;
		while (true)
		{
			int num2 = content.IndexOf("```", startIndex, StringComparison.Ordinal);
			if (num2 < 0)
			{
				break;
			}
			num++;
			startIndex = num2 + 3;
		}
		return (num % 2 == 0) ? content : (content + "\n```");
	}

	private async Task<IMessageChannel> GetTargetChannelAsync()
	{
		IMessageChannel messageChannel = client.GetChannel(configuration.Discord.ChannelId) as IMessageChannel;
		IMessageChannel messageChannel2 = messageChannel;
		if (messageChannel2 == null)
		{
			messageChannel2 = (await client.GetChannelAsync(configuration.Discord.ChannelId)) as IMessageChannel;
		}
		IMessageChannel channel = messageChannel2;
		return channel ?? throw new InvalidOperationException($"Discord channel {configuration.Discord.ChannelId} was not found or is not a message channel.");
	}

	private static async Task<IMessage?> GetOldestMessageAsync(IMessageChannel channel)
	{
		IMessage[] page = (await channel.GetMessagesAsync().FlattenAsync()).ToArray();
		while (page.Length != 0)
		{
			IMessage oldest = page.OrderBy((IMessage message) => message.Timestamp).First();
			if (page.Length < 100)
			{
				return oldest;
			}
			IMessage[] olderPage = (await channel.GetMessagesAsync(oldest.Id, Direction.Before).FlattenAsync()).ToArray();
			if (olderPage.Length == 0)
			{
				return oldest;
			}
			page = olderPage;
		}
		return null;
	}

	private static async Task<IMessage[]> GetRecentMessagesAsync(IMessageChannel channel)
	{
		return (await channel.GetMessagesAsync(2).FlattenAsync()).OrderBy((IMessage message) => message.Timestamp).ToArray();
	}

	private Task LogAsync(LogMessage message)
	{
		string text = $"Discord {message.Severity}: {message.Source}: {message.Message}";
		if (message.Exception == null)
		{
			Console.WriteLine(text);
		}
		else
		{
			Console.Error.WriteLine(text + " " + message.Exception.Message);
		}
		return Task.CompletedTask;
	}

	private string FormatGuilds()
	{
		return (client.Guilds.Count == 0) ? "none" : string.Join(", ", client.Guilds.Select((SocketGuild guild) => $"{guild.Name} ({guild.Id})"));
	}

	private static string SelfDebateLabel(string participant)
	{
		return "**[" + participant + "]**";
	}

	private static bool IsSelfDebateMessage(string content)
	{
		return content.StartsWith(SelfDebateLabel("agent1"), StringComparison.Ordinal) || content.StartsWith(SelfDebateLabel("agent2"), StringComparison.Ordinal);
	}

	private static string NextSelfDebateParticipant(string latestArgument)
	{
		if (latestArgument.StartsWith(SelfDebateLabel("agent1"), StringComparison.Ordinal))
		{
			return "agent2";
		}
		if (latestArgument.StartsWith(SelfDebateLabel("agent2"), StringComparison.Ordinal))
		{
			return "agent1";
		}
		return "agent1";
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			debateLock.Dispose();
			client.Dispose();
		}
	}

	static DebateBot()
	{
		OpenAiToolDefinition[] array = new OpenAiToolDefinition[4];
		JsonObject obj = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["query"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The debate claim or question to search for."
				},
				["top_k"] = new JsonObject
				{
					["type"] = "integer",
					["minimum"] = 1,
					["maximum"] = 20
				}
			}
		};
		obj["required"] = new JsonArray("query");
		array[0] = new OpenAiToolDefinition("function", "search_memories", "Search the scoped debate memories (memsem triple store) before deciding whether existing memory should be changed.", obj);
		JsonObject obj2 = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["subject"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The entity the fact is about, e.g. opponent, topic, agent1."
				},
				["predicate"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The relation, e.g. claimed, conceded, refuted."
				},
				["object"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The concise value of the fact."
				},
				["importance"] = new JsonObject
				{
					["type"] = "number",
					["minimum"] = 0,
					["maximum"] = 1,
					["description"] = "Intrinsic importance 0..1; 0.9+ marks a critical fact."
				},
				["theme"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Optional hierarchical routing theme, e.g. debate/logic."
				},
				["tags"] = new JsonObject
				{
					["type"] = "array",
					["items"] = new JsonObject { ["type"] = "string" },
					["description"] = "Optional keywords for lexical search."
				}
			}
		};
		obj2["required"] = new JsonArray("subject", "predicate", "object");
		array[1] = new OpenAiToolDefinition("function", "add_memory", "Store a concise durable debate fact as an atomic triple (subject → predicate → object), for example subject 'opponent', predicate 'conceded', object 'correlation is not causation'. Do not store the entire generated reply.", obj2);
		JsonObject obj3 = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["memory_id"] = new JsonObject { ["type"] = "integer" },
				["subject"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Defaults to the existing fact's subject."
				},
				["predicate"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Defaults to the existing fact's predicate."
				},
				["object"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "The corrected concise value of the fact."
				},
				["importance"] = new JsonObject
				{
					["type"] = "number",
					["minimum"] = 0,
					["maximum"] = 1
				},
				["theme"] = new JsonObject { ["type"] = "string" },
				["tags"] = new JsonObject
				{
					["type"] = "array",
					["items"] = new JsonObject { ["type"] = "string" }
				}
			}
		};
		obj3["required"] = new JsonArray("memory_id", "object");
		array[2] = new OpenAiToolDefinition("function", "update_memory", "Correct an existing scoped memory when new debate evidence makes it stale or incorrect. Use an ID returned by search_memories. The corrected fact supersedes the old one (memsem keeps the history).", obj3);
		JsonObject obj4 = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["memory_id"] = new JsonObject { ["type"] = "integer" } }
		};
		obj4["required"] = new JsonArray("memory_id");
		array[3] = new OpenAiToolDefinition("function", "delete_memory", "Archive an existing scoped memory only when it is clearly invalid, obsolete, or a duplicate. Use an ID returned by search_memories.", obj4);
		MemoryTools = array;
	}
}
