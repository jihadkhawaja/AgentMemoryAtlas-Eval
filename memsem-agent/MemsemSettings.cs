internal sealed class MemsemSettings
{
	public string Command { get; init; } = "cmd.exe";

	public string[] Arguments { get; init; } = new string[4] { "/c", "npx", "-y", "memsem" };

	public string DatabasePath { get; init; } = "./memsem-memory.db";

	public string Project { get; init; } = "memsem-agent";

	public bool RelaxSearch { get; init; }
}
