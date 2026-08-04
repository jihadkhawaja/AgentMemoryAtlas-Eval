using Mem0Sharp;

var configurationPath = Path.Combine(AppContext.BaseDirectory, "config.local.yaml");
var configuration = AgentConfiguration.Load(configurationPath);

using var httpClient = new HttpClient
{
	BaseAddress = new Uri(configuration.OpenAi.Endpoint)
};

var openAi = new OpenAiCompatibleClient(
	httpClient,
	configuration.OpenAi.ApiKey,
	configuration.OpenAi.ChatModel,
	configuration.OpenAi.EmbeddingModel);

await using var store = new PostgresMemoryStore(new PostgresMemoryStoreOptions
{
	ConnectionString = configuration.Postgres.ConnectionString,
	EmbeddingDimensions = configuration.Postgres.EmbeddingDimensions,
	TableName = configuration.Postgres.TableName,
	CreateExtension = configuration.Postgres.CreateExtension,
	UseHnswIndex = configuration.Postgres.UseHnswIndex
});
await store.InitializeAsync();

var memory = new MemoryService(
	store: store,
	embeddings: openAi);
if (configuration.Agent.ResetMemoryOnStart)
{
	await memory.ResetAsync();
	Console.WriteLine("Reset Mem0Sharp memories and histories at startup.");
}
else
{
	Console.WriteLine("Preserving Mem0Sharp memories and histories at startup.");
}

var toolChat = new OpenAiToolClient(
	httpClient,
	configuration.OpenAi.ApiKey,
	configuration.OpenAi.ChatModel,
	configuration.OpenAi.ReasoningEffort);
using var bot = new DebateBot(configuration, memory, toolChat);
await bot.RunAsync();
