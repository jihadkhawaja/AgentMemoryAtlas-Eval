# Discord debate MCP server

This MCP server reads a Discord debate and can broker long-term memory calls between multiple MCP-compatible memory repositories. Discord access remains read-only; memory writes are opt-in through the middleware tools. The checked-in example connects the `memsem` and `mem0sharp` submodules as separate backends.

## VS Code setup

Open the `AgentMemoryAtlas-Eval` workspace and start the `discord-debate-evaluator` server from the MCP view. VS Code prompts for the Discord bot token and channel ID from `.vscode/mcp.json`. The launch entry also loads `memory-repositories.example.json`; child memory servers are started lazily when a memory tool is called.

The Discord bot needs **View Channel** and **Read Message History**. The token is passed only to the local MCP process and is not stored in the repository. Memory repository processes communicate over their MCP stdio streams; their diagnostics stay on stderr.

Available tools:

- `get_debate_messages`: returns chronological raw Discord messages, including the labeled arguments and metadata boxes.
- `analyze_debate_memory_usage`: pairs each labeled argument with its metadata and reports search coverage, empty searches, adds, updates, deletes, truncation, and per-turn evidence.
- `list_memory_repositories`: discovers configured repositories and their advertised MCP tools.
- `search_debate_memories`: searches all selected repositories with a stable `debate_id` and optional `participant_id`, returning repository-tagged hits.
- `add_debate_memory`: writes a scoped durable memory to one or more selected repositories. The middleware translates the request to `memsem` or Mem0Sharp tool arguments.
- `record_debate_turn`: records a turn as an episode when supported, or optionally falls back to a semantic memory.
- `get_debate_context`: combines the current Discord transcript window with scoped memory results for a multi-turn response.

The analysis report measures memory behavior and surfaces the text needed for qualitative judgment. Search or mutation counts alone do not establish that an action was relevant or correct. Use one stable `debate_id` for every turn in a debate and one stable `participant_id` for each agent. The default repository scope is `debate:<debate_id>`; Mem0Sharp receives that value as `user_id` and the participant as `agent_id`, while memsem receives it as `project` and keeps participant information in the stored fact.

## Configure repositories

Initialize the memory repositories if this checkout was cloned without submodules:

```powershell
git submodule update --init --recursive
```

The example requires Node.js 22.13 or newer for the memsem submodule server. Build it once from the workspace root:

```powershell
Push-Location .\memsem
npm.cmd ci
.\node_modules\.bin\tsc.cmd
Copy-Item .\src\plugin.ts .\opencode-plugin\memsem-extract.ts
Pop-Location
```

The upstream npm build script uses the Unix `cp` command, so the explicit Windows commands above perform the same build without changing the submodule checkout.

The default `memory-repositories.example.json` launches:

- `memsem-local`: `node dist/index.js` from the `memsem` submodule.
- `mem0sharp-local`: the sibling `mem0sharp` MCP sample through `dotnet run`.

For a private setup, copy the example to `memory-repositories.local.json`, edit the commands, working directories, environment variables, or tool mappings, and point `DEBATE_MEMORY_REPOSITORIES_FILE` at it. The local file is ignored by Git. You can also provide the JSON directly through `DEBATE_MEMORY_REPOSITORIES_JSON`.

Each entry has this shape:

```json
{
	"id": "my-memory",
	"kind": "auto",
	"command": "node",
	"args": ["server.js"],
	"workingDirectory": "..\\my-memory-repo",
	"environment": {},
	"tools": {
		"search": "search_memory",
		"add": "add_memory",
		"episode_add": "add_episode"
	}
}
```

`kind` may be `memsem`, `mem0sharp`, or `auto`. Auto repositories use the first advertised tool matching `memory_search`/`search_memories`, `memory_add`/`add_memory`, and the corresponding episode names. This lets another repository participate without changing the debate MCP, provided its MCP tools accept the generic debate fields documented by the tool descriptions.

## Manual run

```powershell
dotnet run --project .\discord-debate-mcp\DiscordDebateMcp.csproj
```

Set `DISCORD_BOT_TOKEN` and optionally `DISCORD_CHANNEL_ID` before running manually. The MCP process communicates over stdout, so do not write diagnostic output there.

To enable the example repositories in a manual run:

```powershell
$env:DEBATE_MEMORY_REPOSITORIES_FILE = (Resolve-Path .\discord-debate-mcp\memory-repositories.example.json)
dotnet run --project .\discord-debate-mcp\DiscordDebateMcp.csproj
```
