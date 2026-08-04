# Discord debate MCP server

This read-only MCP server pulls a Discord debate channel and turns the Mem0Sharp metadata messages into analysis-ready data. It is intended for evaluating the `mem0sharp-agent` self-debate transcript after both agents have finished.

## VS Code setup

Open the `AgentMemoryAtlas-Eval` workspace and start the `discord-debate-evaluator` server from the MCP view. VS Code prompts for the Discord bot token and channel ID from `.vscode/mcp.json`.

The bot needs **View Channel** and **Read Message History**. The token is passed only to the local MCP process and is not stored in the repository.

Available tools:

- `get_debate_messages`: returns chronological raw Discord messages, including the labeled arguments and metadata boxes.
- `analyze_debate_memory_usage`: pairs each labeled argument with its metadata and reports search coverage, empty searches, adds, updates, deletes, truncation, and per-turn evidence.

The report measures memory behavior and surfaces the text needed for qualitative judgment. Search or mutation counts alone do not establish that an action was relevant or correct.

## Manual run

```powershell
dotnet run --project .\discord-debate-mcp\DiscordDebateMcp.csproj
```

Set `DISCORD_BOT_TOKEN` and optionally `DISCORD_CHANNEL_ID` before running manually. The MCP process communicates over stdout, so do not write diagnostic output there.
