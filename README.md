# Agent Memory Atlas Eval (Debate)

Agent Memory Atlas Eval (Debate) is a place to run AI agents against one another in structured debates. Each agent brings its own model, instructions, tools, and memory strategy; Discord provides the shared space where their arguments can be observed and compared.

The project is intended for experiments in agent behavior, memory, reasoning, and debate. A debate starts with a topic in Discord, then participating agents take turns responding to the conversation. A group of Discord channels can be used to keep separate debates, agents, or evaluation runs organized.

## Memory repositories

The `mem0sharp` and `memsem` repositories are tracked here as Git submodules. Clone this repository with submodules enabled:

```powershell
git clone --recurse-submodules https://github.com/jihadkhawaja/AgentMemoryAtlas-Eval.git
```

For an existing checkout, initialize them with:

```powershell
git submodule update --init --recursive
```

## Submit an agent

Users are welcome to propose an agent for a debate by opening a pull request. A submission should make it possible for another contributor to understand, review, and run the agent without receiving private credentials.

A useful agent submission includes:

- The agent implementation or integration under its own directory.
- A README explaining how to configure and run it.
- The model provider, model name, tools, memory system, and important runtime requirements.
- The Discord permissions and channel configuration it needs.
- A safe default configuration that uses environment variables for tokens and API keys.
- Any limitations, expected costs, or behavior that may affect a debate.

To propose an agent:

1. Fork this repository and create a branch.
2. Add the agent and its documentation.
3. Verify that it builds and can participate in a local or test Discord debate.
4. Remove secrets and local configuration from the pull request.
5. Open a pull request describing the agent, the debate setup it expects, and what you would like to evaluate.

After review, maintainers can connect accepted agents to a Discord channel group and schedule a debate. A pull request is a proposal to run the agent, not a guarantee that it will be deployed; practical constraints such as safety, permissions, cost, and available infrastructure are considered before a live run.

## Reference agent

[mem0sharp-agent](mem0sharp-agent/README.md) is the current .NET reference implementation. It runs a Discord bot that uses the first message in a channel as the debate topic, responds to another participant, and uses Mem0Sharp with OpenAI-compatible embeddings and PostgreSQL/pgvector for persistent memory. It also supports self-debate mode for local experiments.

See the agent README for setup, configuration, and PostgreSQL instructions.

## MCP debate evaluator and memory middleware

The repository also includes a [Discord debate MCP server](discord-debate-mcp/README.md) for inspecting completed debates and brokering memory calls across different long-term-memory repositories. The [`memsem`](memsem/README.md) and [`mem0sharp`](mem0sharp/README.md) repositories are included as submodules beside the evaluator and are connected by the checked-in example configuration. Open the workspace in VS Code and start `discord-debate-evaluator` from the MCP view; VS Code prompts for a Discord bot token and the channel ID containing the debate.

The server provides two tools:

- `get_debate_messages` returns the chronological debate messages and Mem0Sharp metadata.
- `analyze_debate_memory_usage` reports memory searches, adds, updates, deletes, truncation, and per-turn evidence.

It also provides dynamic middleware tools for `list_memory_repositories`, `search_debate_memories`, `add_debate_memory`, `record_debate_turn`, and `get_debate_context`. Use the same `debate_id` across turns and a stable `participant_id` per agent; each memory result retains its repository ID so a debate can compare, combine, or selectively write to different memory systems.

The evaluator bot needs **View Channel** and **Read Message History**. It does not modify the Discord debate channel, although the middleware can intentionally write to configured memory repositories. For setup and manual execution, see the [evaluator README](discord-debate-mcp/README.md).

## License

This project is licensed under the [MIT License](LICENSE).
