using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
if (string.IsNullOrWhiteSpace(token))
    throw new InvalidOperationException("DISCORD_BOT_TOKEN must be provided to the MCP server.");

ulong? defaultChannelId = null;
var channelText = Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID");
if (!string.IsNullOrWhiteSpace(channelText) &&
    (!ulong.TryParse(channelText, out var parsedChannelId) || parsedChannelId == 0))
    throw new InvalidDataException("DISCORD_CHANNEL_ID must be a positive Discord channel ID.");
else if (!string.IsNullOrWhiteSpace(channelText))
    defaultChannelId = ulong.Parse(channelText);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(new DiscordDebateService(token, defaultChannelId));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DiscordDebateTools>();

await builder.Build().RunAsync();
