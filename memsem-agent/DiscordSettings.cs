internal sealed class DiscordSettings
{
	public string Token { get; init; } = string.Empty;

	public ulong ChannelId { get; init; }

	public ulong? ParticipantUserId { get; init; }
}
