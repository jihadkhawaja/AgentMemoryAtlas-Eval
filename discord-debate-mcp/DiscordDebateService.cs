using Discord;
using Discord.Rest;

internal sealed class DiscordDebateService : IAsyncDisposable
{
    private readonly string token;
    private readonly ulong? defaultChannelId;
    private readonly DiscordRestClient client = new(new DiscordRestConfig
    {
        LogLevel = LogSeverity.Warning
    });
    private bool loggedIn;

    public DiscordDebateService(string token, ulong? defaultChannelId)
    {
        this.token = token;
        this.defaultChannelId = defaultChannelId;
    }

    public async Task<IReadOnlyList<DiscordMessageSnapshot>> GetMessagesAsync(
        ulong? channelId,
        int limit,
        CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(channelId);
        var messages = new List<IMessage>();
        IMessage[] page = (await channel.GetMessagesAsync(100).FlattenAsync()).ToArray();

        while (page.Length > 0 && messages.Count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            messages.AddRange(page);
            if (page.Length < 100 || messages.Count >= limit)
                break;

            var oldest = page.MinBy(message => message.Timestamp)!.Id;
            page = (await channel.GetMessagesAsync(oldest, Direction.Before, 100).FlattenAsync()).ToArray();
        }

        return messages
            .DistinctBy(message => message.Id)
            .OrderBy(message => message.Timestamp)
            .Take(limit)
            .Select(message => new DiscordMessageSnapshot(
                message.Id,
                message.Timestamp,
                message.Author.Id,
                message.Author.Username,
                message.Author.IsBot,
                message.Content))
            .ToArray();
    }

    private async Task<IMessageChannel> GetChannelAsync(ulong? channelId)
    {
        var resolvedChannelId = channelId ?? defaultChannelId
            ?? throw new InvalidOperationException("Provide channel_id or configure DISCORD_CHANNEL_ID.");
        if (!loggedIn)
        {
            await client.LoginAsync(TokenType.Bot, token);
            loggedIn = true;
        }

        var channel = await client.GetChannelAsync(resolvedChannelId) as IMessageChannel;
        return channel ?? throw new InvalidOperationException(
            $"Discord channel {resolvedChannelId} was not found or is not a message channel.");
    }

    public async ValueTask DisposeAsync()
    {
        if (loggedIn)
            await client.LogoutAsync();
        client.Dispose();
    }
}

internal sealed record DiscordMessageSnapshot(
    ulong Id,
    DateTimeOffset Timestamp,
    ulong AuthorId,
    string AuthorName,
    bool AuthorIsBot,
    string Content);
