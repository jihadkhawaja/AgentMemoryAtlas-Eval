using System.Net.Http;

var configurationPath = Path.Combine(AppContext.BaseDirectory, "config.local.yaml");
var configuration = AgentConfiguration.Load(configurationPath);
using var httpClient = new HttpClient
{
	BaseAddress = new Uri(configuration.OpenAi.Endpoint)
};
await using var memory = await MemsemMemoryClient.StartAsync(configuration.Memsem);
if (configuration.Agent.ResetMemoryOnStart)
{
	var purged = await memory.ResetProjectAsync();
	Console.WriteLine($"Reset memsem memories for project '{memory.Project}' at startup ({purged} purged).");
}
else
{
	Console.WriteLine($"Preserving memsem memories for project '{memory.Project}' at startup.");
}

var toolChat = new OpenAiToolClient(
	httpClient,
	configuration.OpenAi.ApiKey,
	configuration.OpenAi.ChatModel,
	configuration.OpenAi.ReasoningEffort);
using var bot = new DebateBot(configuration, memory, toolChat);
await bot.RunAsync();
