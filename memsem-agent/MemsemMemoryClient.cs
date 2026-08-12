using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal sealed class MemsemMemoryClient : IAsyncDisposable
{
	private readonly McpClient client;

	public string Project { get; }

	private MemsemMemoryClient(McpClient client, string project)
	{
		this.client = client;
		Project = project;
	}

	public static async Task<MemsemMemoryClient> StartAsync(MemsemSettings settings, CancellationToken cancellationToken = default(CancellationToken))
	{
		string databasePath = Path.GetFullPath(Path.IsPathRooted(settings.DatabasePath) ? settings.DatabasePath : Path.Combine(AppContext.BaseDirectory, settings.DatabasePath));
		Directory.CreateDirectory(Path.GetDirectoryName(databasePath));
		Dictionary<string, string?> environment = new Dictionary<string, string>
		{
			["MEMORY_DB_PATH"] = databasePath,
			["MEMORY_PROJECT"] = settings.Project,
			["MEMSEM_INDEX_PATH"] = Path.Combine(Path.GetDirectoryName(databasePath), "memory-index.md")
		};
		StdioClientTransport transport = new StdioClientTransport(new StdioClientTransportOptions
		{
			Name = "memsem",
			Command = settings.Command,
			Arguments = settings.Arguments,
			EnvironmentVariables = environment
		});
		McpClient client = await McpClient.CreateAsync(transport, null, null, cancellationToken);
		Console.WriteLine($"Started memsem MCP server ({settings.Command} {string.Join(' ', settings.Arguments)}) with database {databasePath}.");
		return new MemsemMemoryClient(client, settings.Project);
	}

	public async Task<IReadOnlyList<MemsemSearchResult>> SearchAsync(string query, int limit, bool relax, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!(await CallToolAsync("memory_search", new Dictionary<string, object>
		{
			["query"] = query,
			["project"] = Project,
			["limit"] = limit,
			["relax"] = relax
		}, cancellationToken) is JsonArray hits))
		{
			return Array.Empty<MemsemSearchResult>();
		}
		List<MemsemSearchResult> results = new List<MemsemSearchResult>(hits.Count);
		foreach (JsonObject hit in hits.OfType<JsonObject>())
		{
			MemsemMemory memory = ParseMemory(hit);
			if ((object)memory != null)
			{
				results.Add(new MemsemSearchResult(memory, hit["score"]?.GetValue<double>() ?? 0.0));
			}
		}
		return results;
	}

	public async Task<MemsemAddOutcome> AddAsync(string subject, string predicate, string @object, double? importance, string? theme, IReadOnlyList<string>? tags, string? provenance, CancellationToken cancellationToken = default(CancellationToken))
	{
		Dictionary<string, object?> arguments = new Dictionary<string, object>
		{
			["subject"] = subject,
			["predicate"] = predicate,
			["object"] = @object,
			["project"] = Project
		};
		if (importance.HasValue)
		{
			arguments["importance"] = importance.Value;
		}
		if (!string.IsNullOrWhiteSpace(theme))
		{
			arguments["theme"] = theme;
		}
		if (tags != null && tags.Count > 0)
		{
			arguments["tags"] = tags.ToArray();
		}
		if (!string.IsNullOrWhiteSpace(provenance))
		{
			arguments["provenance"] = provenance;
		}
		if (!(await CallToolAsync("memory_add", arguments, cancellationToken) is JsonObject added))
		{
			throw new InvalidDataException("memsem memory_add returned no result object.");
		}
		if (added["rejected"]?.GetValue<bool>() ?? false)
		{
			throw new InvalidDataException("memsem rejected the write: " + (added["rejectionReason"]?.GetValue<string>() ?? "suppressed") + ".");
		}
		return new MemsemAddOutcome(added["id"]?.GetValue<int>() ?? 0, added["created"]?.GetValue<bool>() ?? false, added["conflict"]?.GetValue<bool>() ?? false, ReadIdArray(added["faded"]), ReadIdArray(added["archived"]));
	}

	public async Task<bool> ForgetAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await CallToolAsync("memory_forget", new Dictionary<string, object> { ["id"] = id }, cancellationToken) is JsonObject forgotten && (forgotten["forgotten"]?.GetValue<bool>() ?? false);
	}

	public async Task<int> ResetProjectAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		int purged = 0;
		while (true)
		{
			JsonArray hits = (await CallToolAsync("memory_list", new Dictionary<string, object>
			{
				["project"] = Project,
				["limit"] = 100
			}, cancellationToken)) as JsonArray;
			if (hits == null || hits.Count == 0)
			{
				break;
			}
			foreach (JsonObject hit in hits.OfType<JsonObject>())
			{
				int? id = hit["id"]?.GetValue<int>();
				if (id.HasValue)
				{
					await CallToolAsync("memory_purge", new Dictionary<string, object>
					{
						["id"] = id.Value,
						["confirm"] = true,
						["reason"] = "eval-reset"
					}, cancellationToken);
					purged++;
				}
			}
		}
		return purged;
	}

	private async Task<JsonNode?> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
	{
		CallToolResult result = await client.CallToolAsync(toolName, arguments, null, null, cancellationToken);
		string text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
		if (result.IsError == true)
		{
			throw new InvalidDataException("memsem tool '" + toolName + "' failed: " + (text ?? "unknown error"));
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		return JsonNode.Parse(text);
	}

	private static MemsemMemory? ParseMemory(JsonObject hit)
	{
		int? num = hit["id"]?.GetValue<int>();
		string text = hit["subject"]?.GetValue<string>();
		string text2 = hit["predicate"]?.GetValue<string>();
		string text3 = hit["object"]?.GetValue<string>();
		if (!num.HasValue || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2) || text3 == null)
		{
			return null;
		}
		string[] tags = ((hit["tags"] is JsonArray source) ? (from tag in source
			select tag?.GetValue<string>() into tag
			where !string.IsNullOrWhiteSpace(tag)
			select tag).Cast<string>().ToArray() : Array.Empty<string>());
		return new MemsemMemory(num.Value, text, text2, text3, tags, hit["theme"]?.GetValue<string>(), hit["confidence"]?.GetValue<double>() ?? 0.0, hit["importance"]?.GetValue<double>() ?? 0.0);
	}

	private static int[] ReadIdArray(JsonNode? node)
	{
		return (node is JsonArray source) ? (from item in source
			select item?.GetValue<int>() ?? 0 into idValue
			where idValue > 0
			select idValue).ToArray() : Array.Empty<int>();
	}

	public async ValueTask DisposeAsync()
	{
		await client.DisposeAsync();
	}
}
