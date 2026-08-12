using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class MemoryRepositoryConfiguration
{
    private MemoryRepositoryConfiguration(IReadOnlyList<MemoryRepositoryDefinition> repositories)
    {
        Repositories = repositories;
    }

    public IReadOnlyList<MemoryRepositoryDefinition> Repositories { get; }

    public static MemoryRepositoryConfiguration Load()
    {
        var source = Environment.GetEnvironmentVariable("DEBATE_MEMORY_REPOSITORIES_JSON");
        var sourcePath = Environment.GetEnvironmentVariable("DEBATE_MEMORY_REPOSITORIES_FILE");
        if (string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(sourcePath))
        {
            source = File.ReadAllText(sourcePath);
        }

        if (string.IsNullOrWhiteSpace(source))
            return new MemoryRepositoryConfiguration([]);

        try
        {
            using var document = JsonDocument.Parse(source);
            var root = document.RootElement;
            var repositories = root.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<MemoryRepositoryDefinition>>(root.GetRawText(), JsonOptions.Instance)
                : root.TryGetProperty("repositories", out var property)
                    ? JsonSerializer.Deserialize<List<MemoryRepositoryDefinition>>(property.GetRawText(), JsonOptions.Instance)
                    : null;

            if (repositories is null)
                throw new InvalidDataException("Repository configuration must be an array or an object with a repositories array.");

            Validate(repositories);
            return new MemoryRepositoryConfiguration(repositories);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("DEBATE_MEMORY_REPOSITORIES_JSON is not valid JSON.", exception);
        }
    }

    private static void Validate(IReadOnlyList<MemoryRepositoryDefinition> repositories)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
        {
            if (string.IsNullOrWhiteSpace(repository.Id))
                throw new InvalidDataException("Every memory repository requires a non-empty id.");
            if (!ids.Add(repository.Id))
                throw new InvalidDataException($"Memory repository id '{repository.Id}' is duplicated.");
            if (repository.Enabled && string.IsNullOrWhiteSpace(repository.Command))
                throw new InvalidDataException($"Enabled memory repository '{repository.Id}' requires a command.");
        }
    }

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Instance = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }
}

internal sealed class MemoryRepositoryDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = "auto";
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("args")]
    public string[] Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }
    public bool Enabled { get; init; } = true;
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Tools { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class MemoryRepositoryMiddleware : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, MemoryRepositoryRegistration> repositories;

    public MemoryRepositoryMiddleware(MemoryRepositoryConfiguration configuration)
    {
        repositories = configuration.Repositories
            .Where(repository => repository.Enabled)
            .ToDictionary(
                repository => repository.Id,
                repository => new MemoryRepositoryRegistration(repository),
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<MemoryRepositoryStatus>> GetRepositoryStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = await Task.WhenAll(
            repositories.Values.Select(repository => repository.GetStatusAsync(cancellationToken)));
        return statuses.OrderBy(status => status.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DebateMemorySearchReport> SearchAsync(
        string debateId,
        string query,
        string? participantId,
        string? project,
        bool crossProject,
        int topK,
        IReadOnlyList<string>? repositoryIds,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(debateId, query);
        var selected = Select(repositoryIds);
        var scopeProject = DebateMemoryScope.Project(debateId, project);
        var results = await Task.WhenAll(selected.Select(repository => SearchRepositoryAsync(
            repository,
            debateId,
            query,
            participantId,
            scopeProject,
            crossProject,
            Math.Clamp(topK, 1, 100),
            cancellationToken)));

        return new DebateMemorySearchReport(
            debateId,
            participantId,
            query,
            results.SelectMany(result => result.Hits).OrderByDescending(hit => hit.Score ?? double.MinValue).ToArray(),
            results.Select(result => result.Status).ToArray());
    }

    public async Task<DebateMemoryWriteReport> AddAsync(
        string debateId,
        string? participantId,
        string text,
        string? turnId,
        string? project,
        string? subject,
        string? predicate,
        string? objectValue,
        string? theme,
        double importance,
        IReadOnlyList<string>? repositoryIds,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(debateId, text);
        var selected = Select(repositoryIds);
        var scopeProject = DebateMemoryScope.Project(debateId, project);
        var results = await Task.WhenAll(selected.Select(repository => AddRepositoryAsync(
            repository,
            debateId,
            participantId,
            text,
            turnId,
            scopeProject,
            subject,
            predicate,
            objectValue,
            theme,
            Math.Clamp(importance, 0, 1),
            cancellationToken)));
        return new DebateMemoryWriteReport(debateId, participantId, text, results);
    }

    public async Task<DebateTurnRecordReport> RecordTurnAsync(
        string debateId,
        string participantId,
        string turnId,
        string argument,
        string? topic,
        string? project,
        bool storeAsMemory,
        IReadOnlyList<string>? repositoryIds,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(debateId, argument);
        if (string.IsNullOrWhiteSpace(participantId))
            throw new ArgumentException("participant_id is required.", nameof(participantId));
        if (string.IsNullOrWhiteSpace(turnId))
            throw new ArgumentException("turn_id is required.", nameof(turnId));

        var selected = Select(repositoryIds);
        var scopeProject = DebateMemoryScope.Project(debateId, project);
        var results = await Task.WhenAll(selected.Select(repository => RecordRepositoryTurnAsync(
            repository,
            debateId,
            participantId,
            turnId,
            argument,
            topic,
            scopeProject,
            storeAsMemory,
            cancellationToken)));
        return new DebateTurnRecordReport(debateId, participantId, turnId, results);
    }

    private async Task<RepositorySearchResult> SearchRepositoryAsync(
        MemoryRepositoryRegistration repository,
        string debateId,
        string query,
        string? participantId,
        string scopeProject,
        bool crossProject,
        int topK,
        CancellationToken cancellationToken)
    {
        try
        {
            var tool = await repository.ResolveToolAsync("search", cancellationToken);
            var arguments = BuildSearchArguments(
                repository.Definition,
                debateId,
                query,
                participantId,
                scopeProject,
                crossProject,
                topK);
            var response = await repository.Client.CallToolAsync(tool, arguments, cancellationToken);
            if (response.IsError)
                return new RepositorySearchResult([], repository.Status(error: response.Error ?? "Memory repository returned an MCP error."));

            return new RepositorySearchResult(
                ParseHits(repository.Definition.Id, response.Payload),
                repository.Status());
        }
        catch (Exception exception)
        {
            return new RepositorySearchResult([], repository.Status(exception.Message));
        }
    }

    private async Task<RepositoryWriteResult> AddRepositoryAsync(
        MemoryRepositoryRegistration repository,
        string debateId,
        string? participantId,
        string text,
        string? turnId,
        string scopeProject,
        string? subject,
        string? predicate,
        string? objectValue,
        string? theme,
        double importance,
        CancellationToken cancellationToken)
    {
        try
        {
            var tool = await repository.ResolveToolAsync("add", cancellationToken);
            var arguments = BuildAddArguments(
                repository.Definition,
                debateId,
                participantId,
                text,
                turnId,
                scopeProject,
                subject,
                predicate,
                objectValue,
                theme,
                importance);
            var response = await repository.Client.CallToolAsync(tool, arguments, cancellationToken);
            return new RepositoryWriteResult(
                repository.Definition.Id,
                response.IsError ? null : TryFindId(response.Payload),
                !response.IsError,
                response.IsError ? response.Error : null,
                repository.Status(response.IsError ? response.Error : null));
        }
        catch (Exception exception)
        {
            return new RepositoryWriteResult(
                repository.Definition.Id,
                null,
                false,
                exception.Message,
                repository.Status(exception.Message));
        }
    }

    private async Task<DebateTurnRepositoryResult> RecordRepositoryTurnAsync(
        MemoryRepositoryRegistration repository,
        string debateId,
        string participantId,
        string turnId,
        string argument,
        string? topic,
        string scopeProject,
        bool storeAsMemory,
        CancellationToken cancellationToken)
    {
        try
        {
            var episodeTool = await repository.ResolveToolAsync("episode_add", cancellationToken, false);
            if (episodeTool is not null)
            {
                var response = await repository.Client.CallToolAsync(
                    episodeTool,
                    BuildEpisodeArguments(repository.Definition, debateId, participantId, turnId, argument, topic, scopeProject),
                    cancellationToken);
                if (response.IsError)
                    return new DebateTurnRepositoryResult(repository.Definition.Id, false, "episode", response.Error, repository.Status(response.Error));

                return new DebateTurnRepositoryResult(repository.Definition.Id, true, "episode", null, repository.Status());
            }

            if (!storeAsMemory)
                return new DebateTurnRepositoryResult(repository.Definition.Id, true, "skipped", null, repository.Status());

            var write = await AddRepositoryAsync(
                repository,
                debateId,
                participantId,
                argument,
                turnId,
                scopeProject,
                null,
                null,
                null,
                "debate",
                0.5,
                cancellationToken);
            return new DebateTurnRepositoryResult(
                repository.Definition.Id,
                write.Success,
                "memory",
                write.Error,
                write.Status);
        }
        catch (Exception exception)
        {
            return new DebateTurnRepositoryResult(
                repository.Definition.Id,
                false,
                "episode",
                exception.Message,
                repository.Status(exception.Message));
        }
    }

    private IReadOnlyList<MemoryRepositoryRegistration> Select(IReadOnlyList<string>? repositoryIds)
    {
        if (repositories.Count == 0)
            throw new InvalidOperationException("No memory repositories are configured. Set DEBATE_MEMORY_REPOSITORIES_JSON or DEBATE_MEMORY_REPOSITORIES_FILE.");

        if (repositoryIds is null || repositoryIds.Count == 0)
            return repositories.Values.ToArray();

        var selected = new List<MemoryRepositoryRegistration>(repositoryIds.Count);
        foreach (var id in repositoryIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!repositories.TryGetValue(id, out var repository))
                throw new KeyNotFoundException($"Memory repository '{id}' is not configured.");
            selected.Add(repository);
        }

        return selected;
    }

    private static void ValidateScope(string debateId, string value)
    {
        if (string.IsNullOrWhiteSpace(debateId))
            throw new ArgumentException("debate_id is required.", nameof(debateId));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The memory value cannot be empty.", nameof(value));
    }

    private static object BuildSearchArguments(
        MemoryRepositoryDefinition definition,
        string debateId,
        string query,
        string? participantId,
        string scopeProject,
        bool crossProject,
        int topK)
    {
        if (IsMem0Sharp(definition))
        {
            return new
            {
                query,
                user_id = debateId,
                agent_id = participantId,
                top_k = topK,
                threshold = 0.1,
                include_expired = false
            };
        }

        if (IsMemsem(definition))
        {
            return new
            {
                query,
                project = scopeProject,
                crossProject,
                limit = topK
            };
        }

        return new
        {
            query,
            debate_id = debateId,
            participant_id = participantId,
            project = scopeProject,
            cross_project = crossProject,
            top_k = topK
        };
    }

    private static object BuildAddArguments(
        MemoryRepositoryDefinition definition,
        string debateId,
        string? participantId,
        string text,
        string? turnId,
        string scopeProject,
        string? subject,
        string? predicate,
        string? objectValue,
        string? theme,
        double importance)
    {
        if (IsMem0Sharp(definition))
        {
            return new
            {
                text,
                user_id = debateId,
                agent_id = participantId,
                run_id = turnId,
                infer = false
            };
        }

        if (IsMemsem(definition))
        {
            return new
            {
                subject = subject ?? participantId ?? "debate",
                predicate = predicate ?? "argued",
                @object = objectValue ?? text,
                importance,
                tags = new[] { "debate", debateId, participantId ?? "unknown" },
                theme = theme ?? "debate",
                project = scopeProject,
                provenance = turnId ?? debateId,
                trust = "inferred"
            };
        }

        return new
        {
            text,
            debate_id = debateId,
            participant_id = participantId,
            turn_id = turnId,
            project = scopeProject,
            subject,
            predicate,
            @object = objectValue,
            theme,
            importance
        };
    }

    private static object BuildEpisodeArguments(
        MemoryRepositoryDefinition definition,
        string debateId,
        string participantId,
        string turnId,
        string argument,
        string? topic,
        string scopeProject)
    {
        var summary = string.IsNullOrWhiteSpace(topic)
            ? $"{participantId} turn {turnId}: {argument}"
            : $"Debate topic: {topic}. {participantId} turn {turnId}: {argument}";

        if (IsMemsem(definition))
        {
            return new
            {
                project = scopeProject,
                summary,
                provenance = $"{debateId}:{turnId}:{participantId}"
            };
        }

        return new
        {
            summary,
            debate_id = debateId,
            participant_id = participantId,
            turn_id = turnId,
            project = scopeProject,
            provenance = $"{debateId}:{turnId}:{participantId}"
        };
    }

    private static bool IsMemsem(MemoryRepositoryDefinition definition) =>
        definition.Kind.Equals("memsem", StringComparison.OrdinalIgnoreCase);

    private static bool IsMem0Sharp(MemoryRepositoryDefinition definition) =>
        definition.Kind.Equals("mem0sharp", StringComparison.OrdinalIgnoreCase);

    private static string? TryFindId(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("id", out var id))
                return id.ToString();
            if (payload.TryGetProperty("memory", out var memory) && memory.ValueKind == JsonValueKind.Object &&
                memory.TryGetProperty("id", out var nestedId))
                return nestedId.ToString();
            if (payload.TryGetProperty("memories", out var memories) && memories.ValueKind == JsonValueKind.Array && memories.GetArrayLength() > 0)
                return TryFindId(memories[0]);
        }

        if (payload.ValueKind == JsonValueKind.Array && payload.GetArrayLength() > 0)
            return TryFindId(payload[0]);

        return null;
    }

    private static IReadOnlyList<DebateMemoryHit> ParseHits(string repositoryId, JsonElement payload)
    {
        var items = payload.ValueKind == JsonValueKind.Array
            ? payload.EnumerateArray().ToArray()
            : FindArray(payload, "memories", "results", "hits");
        var hits = new List<DebateMemoryHit>(items.Length);
        foreach (var item in items)
        {
            var memory = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("memory", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : item;
            var subject = StringProperty(memory, "subject");
            var predicate = StringProperty(memory, "predicate");
            var objectValue = StringProperty(memory, "object");
            var text = StringProperty(memory, "text") ??
                (subject is not null && predicate is not null && objectValue is not null
                    ? $"{subject} {predicate} {objectValue}"
                    : null);
            hits.Add(new DebateMemoryHit(
                repositoryId,
                StringProperty(memory, "id") ?? StringProperty(item, "id"),
                text,
                subject,
                predicate,
                objectValue,
                NumberProperty(item, "score") ?? NumberProperty(memory, "score"),
                StringProperty(memory, "project"),
                StringProperty(memory, "provenance"),
                item.Clone()));
        }

        return hits;
    }

    private static JsonElement[] FindArray(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return [];
        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().ToArray();
        }
        return [];
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static double? NumberProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(repositories.Values.Select(repository => repository.DisposeAsync().AsTask()));
    }
}

internal sealed class MemoryRepositoryRegistration
{
    private readonly SemaphoreSlim toolLock = new(1, 1);
    private IReadOnlyList<string>? tools;

    public MemoryRepositoryRegistration(MemoryRepositoryDefinition definition)
    {
        Definition = definition;
        Client = new McpRepositoryClient(definition);
    }

    public MemoryRepositoryDefinition Definition { get; }
    public McpRepositoryClient Client { get; }

    public async Task<MemoryRepositoryStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var availableTools = await GetToolsAsync(cancellationToken);
            return Status(tools: availableTools);
        }
        catch (Exception exception)
        {
            return Status(error: exception.Message);
        }
    }

    public async Task<string> ResolveToolAsync(
        string operation,
        CancellationToken cancellationToken,
        bool required = true)
    {
        var availableTools = await GetToolsAsync(cancellationToken);
        if (Definition.Tools.TryGetValue(operation, out var configuredTool) && availableTools.Contains(configuredTool, StringComparer.Ordinal))
            return configuredTool;

        var candidates = operation switch
        {
            "search" => new[] { "memory_search", "search_memories", "search_memory" },
            "add" => new[] { "memory_add", "add_memory" },
            "episode_add" => new[] { "memory_episode_add", "add_episode", "record_episode" },
            _ => Array.Empty<string>()
        };
        var match = candidates.FirstOrDefault(candidate => availableTools.Contains(candidate, StringComparer.Ordinal));
        if (match is not null)
            return match;
        if (!required)
            return null!;
        throw new InvalidOperationException(
            $"Memory repository '{Definition.Id}' does not advertise a tool for '{operation}'. Available tools: {string.Join(", ", availableTools)}.");
    }

    public MemoryRepositoryStatus Status(string? error = null, IReadOnlyList<string>? tools = null) =>
        new(
            Definition.Id,
            Definition.Kind,
            error is null,
            error,
            tools ?? this.tools ?? []);

    private async Task<IReadOnlyList<string>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (tools is not null)
            return tools;

        await toolLock.WaitAsync(cancellationToken);
        try
        {
            tools ??= await Client.ListToolsAsync(cancellationToken);
            return tools;
        }
        finally
        {
            toolLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        toolLock.Dispose();
    }
}

internal sealed class McpRepositoryClient : IAsyncDisposable
{
    private readonly MemoryRepositoryDefinition definition;
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private readonly StringBuilder standardError = new();
    private Process? process;
    private StreamWriter? input;
    private StreamReader? output;
    private Task? errorDrain;
    private long nextRequestId;
    private bool initialized;

    public McpRepositoryClient(MemoryRepositoryDefinition definition)
    {
        this.definition = definition;
    }

    public async Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken)
    {
        await requestLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var result = await SendRequestAsync("tools/list", new { }, cancellationToken);
            if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                return [];
            return tools.EnumerateArray()
                .Where(tool => tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                .Select(tool => tool.GetProperty("name").GetString()!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            await StopProcessAsync();
            throw;
        }
        finally
        {
            requestLock.Release();
        }
    }

    public async Task<McpToolResponse> CallToolAsync(
        string toolName,
        object arguments,
        CancellationToken cancellationToken)
    {
        await requestLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var result = await SendRequestAsync(
                "tools/call",
                new { name = toolName, arguments },
                cancellationToken);
            var isError = result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
            return new McpToolResponse(ExtractPayload(result), isError, isError ? ExtractText(result) : null);
        }
        catch
        {
            await StopProcessAsync();
            throw;
        }
        finally
        {
            requestLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized && process is { HasExited: false })
            return;

        await StopProcessAsync();
        var startInfo = new ProcessStartInfo
        {
            FileName = definition.Command,
            WorkingDirectory = string.IsNullOrWhiteSpace(definition.WorkingDirectory)
                ? Environment.CurrentDirectory
                : definition.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in definition.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var variable in definition.Environment)
            startInfo.Environment[variable.Key] = variable.Value;

        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start memory repository '{definition.Id}'.");
        input = process.StandardInput;
        output = process.StandardOutput;
        input.AutoFlush = true;
        errorDrain = DrainErrorAsync(process.StandardError);

        await SendRequestAsync(
            "initialize",
            new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "discord-debate-mcp", version = "1.0.0" }
            },
            cancellationToken);
        await SendNotificationAsync("notifications/initialized", null, cancellationToken);
        initialized = true;
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        if (input is null || output is null)
            throw new InvalidOperationException($"Memory repository '{definition.Id}' is not running.");

        var requestId = Interlocked.Increment(ref nextRequestId);
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method,
            @params = parameters
        });
        await input.WriteLineAsync(request.AsMemory(), cancellationToken);

        while (true)
        {
            var line = await output.ReadLineAsync(cancellationToken);
            if (line is null)
                throw new InvalidOperationException($"Memory repository '{definition.Id}' closed stdout. {ErrorText()}");
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || !IsMatchingId(responseId, requestId))
                continue;
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"Memory repository '{definition.Id}' returned an MCP error: {ExtractText(error)}");
            if (!root.TryGetProperty("result", out var result))
                throw new InvalidOperationException($"Memory repository '{definition.Id}' returned no result.");
            return result.Clone();
        }
    }

    private async Task SendNotificationAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        if (input is null)
            throw new InvalidOperationException($"Memory repository '{definition.Id}' is not running.");
        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
        await input.WriteLineAsync(notification.AsMemory(), cancellationToken);
    }

    private async Task DrainErrorAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (standardError)
                {
                    if (standardError.Length < 4000)
                        standardError.AppendLine(line);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task StopProcessAsync()
    {
        var current = process;
        process = null;
        input = null;
        output = null;
        initialized = false;
        if (current is null)
            return;

        try
        {
            if (!current.HasExited)
                current.Kill(entireProcessTree: true);
            await current.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            current.Dispose();
        }

        if (errorDrain is not null)
        {
            try
            {
                await errorDrain;
            }
            catch
            {
            }
            errorDrain = null;
        }
    }

    private string ErrorText()
    {
        lock (standardError)
        {
            return standardError.Length == 0 ? string.Empty : $"stderr: {standardError}";
        }
    }

    private static bool IsMatchingId(JsonElement value, long requestId) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var number) && number == requestId,
            JsonValueKind.String => string.Equals(value.GetString(), requestId.ToString(), StringComparison.Ordinal),
            _ => false
        };

    private static JsonElement ExtractPayload(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out var structured))
            return structured.Clone();
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return result.Clone();

        var texts = content.EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "text" && item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString()!)
            .ToArray();
        if (texts.Length == 1)
        {
            try
            {
                using var document = JsonDocument.Parse(texts[0]);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return JsonSerializer.SerializeToElement(texts[0]);
            }
        }

        return JsonSerializer.SerializeToElement(texts);
    }

    private static string ExtractText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("message", out var message))
            return message.ToString();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            return string.Join(" ", content.EnumerateArray().Select(item => item.TryGetProperty("text", out var text) ? text.ToString() : item.ToString()));
        return element.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await requestLock.WaitAsync();
        try
        {
            await StopProcessAsync();
        }
        finally
        {
            requestLock.Release();
            requestLock.Dispose();
        }
    }
}

internal static class DebateMemoryScope
{
    public static string Project(string debateId, string? project) =>
        string.IsNullOrWhiteSpace(project) ? $"debate:{debateId}" : project;
}

internal sealed record McpToolResponse(JsonElement Payload, bool IsError, string? Error);

internal sealed record MemoryRepositoryStatus(
    string Id,
    string Kind,
    bool Available,
    string? Error,
    IReadOnlyList<string> Tools);

internal sealed record DebateMemoryHit(
    string RepositoryId,
    string? MemoryId,
    string? Text,
    string? Subject,
    string? Predicate,
    string? Object,
    double? Score,
    string? Project,
    string? Provenance,
    JsonElement Raw);

internal sealed record DebateMemorySearchReport(
    string DebateId,
    string? ParticipantId,
    string Query,
    IReadOnlyList<DebateMemoryHit> Memories,
    IReadOnlyList<MemoryRepositoryStatus> Repositories);

internal sealed record RepositorySearchResult(
    IReadOnlyList<DebateMemoryHit> Hits,
    MemoryRepositoryStatus Status);

internal sealed record RepositoryWriteResult(
    string RepositoryId,
    string? MemoryId,
    bool Success,
    string? Error,
    MemoryRepositoryStatus Status);

internal sealed record DebateMemoryWriteReport(
    string DebateId,
    string? ParticipantId,
    string Text,
    IReadOnlyList<RepositoryWriteResult> Repositories);

internal sealed record DebateTurnRepositoryResult(
    string RepositoryId,
    bool Success,
    string Operation,
    string? Error,
    MemoryRepositoryStatus Status);

internal sealed record DebateTurnRecordReport(
    string DebateId,
    string ParticipantId,
    string TurnId,
    IReadOnlyList<DebateTurnRepositoryResult> Repositories);