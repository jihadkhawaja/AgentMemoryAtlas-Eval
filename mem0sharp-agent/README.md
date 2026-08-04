# mem0sharp-agent

A .NET 10 Discord bot that uses the first message in one Discord channel as a debate topic. It posts an opening position, then replies to the other agent's messages in the same channel.

The bot is named `mem0sharp-agent`. It gives the topic and every debate exchange to the agent, which decides what durable information to store through Mem0Sharp using OpenAI embeddings and PostgreSQL/pgvector for persistent memory.

## Setup

1. Create a Discord bot, enable the **Message Content Intent**, and invite it to the server with permission to read message history and send messages.
2. Start PostgreSQL with pgvector:

   ```powershell
   docker compose -f .\mem0sharp-agent\compose.yaml up -d
   ```

3. Copy `config.example.yaml` to `config.local.yaml`.
4. Set `DISCORD_BOT_TOKEN` and `OPENAI_API_KEY` in the process environment, then set `discord.channelId` to the target channel ID. The channel's oldest message must be the topic.
5. Set `discord.participantUserId` to the other agent's user ID to filter replies, or leave it `null` to respond to every external message.
6. Run the app:

   ```powershell
   dotnet run --project .\mem0sharp-agent\mem0sharp-agent.csproj
   ```

`config.local.yaml` supports `${ENVIRONMENT_VARIABLE}` placeholders. The local file is ignored by Git so credentials do not enter source control.

`openAi.reasoningEffort` defaults to `medium` for the tool-calling chat model. Set it to `low` or `high` as supported by the model, or `null` to omit `reasoning_effort` for models without reasoning support.

`agent.memorySearchThreshold` controls the minimum semantic relevance score for memories returned to the model. It defaults to `0.35`; raise it for stricter matches or lower it when broader recall is needed. Valid values are between `0` and `1`.

The bot clears all Mem0Sharp memories and memory histories at startup by default so every run begins as a new debate. Set `agent.resetMemoryOnStart: false` when the run must retain data in the configured PostgreSQL database. The setting applies before the bot connects to Discord, and the process logs whether it reset or preserved the memory store.

With reset disabled, memories use a stable namespace for the configured agent and are shared across its debate channels and sessions. This is what makes a fresh run-2 channel able to retrieve facts from run 1; it also means those memories accumulate until a reset-enabled start clears the store.

To measure memory carryover between conversations instead of only retrieval within one conversation:

1. Set `agent.resetMemoryOnStart: false` and run the bot on topic A, allowing the run to finish with its memories retained.
2. Start the bot again in a fresh Discord channel on topic B, where answering well depends on facts established in topic A.
3. Score only run 2. Use its per-reply `[system]` metadata boxes to count memories carried over from run 1 and to identify stale memories that run 2 should have corrected or ignored.

Use a fresh channel for each run because the channel's oldest message supplies the topic. Return `agent.resetMemoryOnStart` to `true` for the default clean single-debate comparison. Do not use the carryover protocol while data in the configured PostgreSQL database must be retained for another purpose.

## Self-debate mode

Set these values under `agent` to make the bot debate itself in alternating advocate and challenger turns:

```yaml
agent:
   selfDebate: true
   maxMessages: 10
```

The opening message counts toward `maxMessages`, so `10` produces at most ten bot messages in the channel. Messages are labeled **`[agent1]`** and **`[agent2]`** in alternating turns. Each label is also used as the Mem0Sharp `AgentId`, so each debating identity retrieves its own stored memories. Debate turns expose real OpenAI Responses API function tools named `search_memories`, `add_memory`, `update_memory`, and `delete_memory`. The model can call those tools before producing its response; update and delete calls are accepted only for memories returned by a scoped search. Every reply ends with a code-generated `[system]` metadata box showing the initial agent context, each search query and its returned memory text/score values, plus the text added, updated, or deleted for that message. In self-debate mode, both agents see the topic on their first turn, then each later turn receives only the other agent's latest message. Self-debate ignores external messages for that run and stops after the configured limit.

Stop the database with:

```powershell
docker compose -f .\mem0sharp-agent\compose.yaml down
```

## PostgreSQL notes

The default OpenAI embedding model returns 1536 dimensions, and the YAML files configure PostgreSQL accordingly. On first start, Mem0Sharp creates the memory and history tables and the pgvector extension when the database user has permission. Set `createExtension: false` when an administrator has already installed the extension.
