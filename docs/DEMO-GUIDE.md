# Demo guide: MAF, Foundry & AgentMemory

Three agents run in **Microsoft Foundry**. Their memory lives in **Neo4j on this PC**, behind the
[AgentMemory for .NET](https://github.com/joslat/agent-memory-dotnet) MCP host and an authenticated
Dev Tunnel. A new conversation, in any of the three, can recall what another one saved.

```
Foundry (cloud)                                    This PC
┌──────────────────────────┐    HTTPS + token    ┌──────────────────────────┐
│ 02 prompt-agent          │ ──────────────────▶ │ devtunnel  :5233         │
│ 01 hosted-mcp-tools (C#) │   Dev Tunnel        │   └─ agent-memory-mcp    │
│ 03 hosted-memory-context │ ◀────────────────── │        └─ Neo4j :7687    │
└──────────────────────────┘      JSON           └──────────────────────────┘
```

## Links

| What | Where |
|---|---|
| Foundry project | https://ai.azure.com → your resource → your project → Build → Agents |
| 02 · prompt-agent | Build → Agents → **prompt-agent** → Playground |
| 01 · hosted-mcp-tools playground | Build → Agents → **hosted-mcp-tools** → Playground (`azd ai agent show` prints the direct link) |
| 03 · hosted-memory-context playground | Build → Agents → **hosted-memory-context** → Playground (`azd ai agent show` prints the direct link) |
| Neo4j Browser (the evidence) | http://localhost:7474 · user `neo4j`, password in `local-memory/.env` |
| Tunnel inspector (every request Foundry sends) | `https://<tunnel>-inspect.<region>.devtunnels.ms` (printed by `devtunnel host`) |
| Tunnel root URL (configured in Foundry) | `https://<tunnel>.<region>.devtunnels.ms/` (printed by `devtunnel host`) |
| Scenario 02 portal configuration + agent instructions | [scenarios/02-prompt-agent/README.md](../scenarios/02-prompt-agent/README.md) |
| Scenario 01 source | [src/hosted-mcp-tools/Program.cs](../src/hosted-mcp-tools/Program.cs) |
| Scenario 03 source (the provider) | [src/hosted-memory-context/RemoteMemoryContextProvider.cs](../src/hosted-memory-context/RemoteMemoryContextProvider.cs) |
| What each scenario is and how it works | [SCENARIOS.md](SCENARIOS.md) |
| Microsoft sample this started from | https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/04-hosting/FoundryHostedAgents/responses/Hosted-McpTools |
| Foundry: connect agents to MCP servers | https://learn.microsoft.com/azure/ai-foundry/agents/how-to/tools/model-context-protocol |
| Dev Tunnels CLI | https://learn.microsoft.com/azure/developer/dev-tunnels/cli-commands |

## Before the talk (10 minutes)

Three terminals on this PC must stay open. Keep a private copy of this file with your live links (gitignored as `DEMO-GUIDE.local.md`). Disable sleep.

```powershell
# Terminal A: database + memory host (writable, owner pinned to foundry-demo)
cd C:\git\joslat\MAF-Foundry-AgentMemory
docker compose -f local-memory/docker-compose.yml up -d
.\local-memory\run-mcp.ps1

# Terminal B: tunnel (the tunnel lives 30 days; a connect token about 24 hours)
devtunnel host agentmemory-demo

# Terminal C: prove recall through the public URL, no model involved
cd src\memory-seed-check
dotnet run -- check          # must print PASS
```

If `check` prints 401, the connect token expired. Mint a new one and update the three places that hold it:

```powershell
devtunnel token agentmemory-demo --scopes connect      # copy the Token line
# 1. src\memory-seed-check\.env            -> MEMORY_TUNNEL_TOKEN=<token>
# 2. Foundry: project connection used by the agentmemory tool -> value "tunnel <token>"
# 3. both hosted agents:
cd src\hosted-mcp-tools;        azd env set MEMORY_TUNNEL_TOKEN "<token>"; azd deploy
cd ..\hosted-memory-context;    azd env set MEMORY_TUNNEL_TOKEN "<token>"; azd deploy
```

Checklist:

- [ ] `dotnet run -- check` passes through the tunnel
- [ ] Neo4j Browser open on `MATCH (n) WHERE NOT n:MemoryReadAudit RETURN n`
- [ ] Three playgrounds open in tabs, each on a fresh conversation
- [ ] Terminal A visible on a second screen (it logs every call that arrives)
- [ ] Secrets off screen: never show `.env`, `.azure`, or `azd env get-values`

## Scenario 02 · Prompt Agent + local MCP (main demo, 3 minutes)

**Story:** a no-code Foundry agent that remembers across conversations, because its memory tool
is a server on my laptop.

1. Show the tool configuration: Build → Agents → prompt-agent → Tools → **agentmemory**. Point at
   the tunnel URL and the three allowed tools: `memory_search`, `memory_add_preference`, `memory_add_fact`.
2. New conversation. Type:
   > Remember that I live in Zurich and that for the Aster demo I want a dark theme.

   Expect two tool calls (`memory_add_fact`, `memory_add_preference`) and a one-line confirmation.
   Terminal A shows both calls arriving. Neo4j Browser: re-run the query, two new nodes.
3. **New conversation** (this is the point). Type:
   > Where do I live, and what theme should the Aster demo use?

   Expect one `memory_search` call and an answer quoting both memories.
4. Negative control, same conversation:
   > What font do I prefer for the Aster demo?

   Expect "nothing stored about that", no invention.

Say: "This agent runs in Foundry. Its memory is a graph on my laptop. A new conversation can
retrieve what an earlier one saved, through an authenticated tunnel, with nothing persisted in Azure."

## Scenario 01 · Hosted MAF agent in C# (2 minutes)

**Story:** the same memory, from my own code deployed to Foundry as a Hosted Agent.

1. Open the hosted-mcp-tools playground. New conversation:
   > Which language should my Aster slides be in?

   Expect a `memory_search` function call in the transcript and the Spanish-slides preference.
2. Show [Program.cs](../src/hosted-mcp-tools/Program.cs): one `AIFunction` named `memory_search`
   over an MCP client, a read-only tool list, `AddFoundryResponses`. About 90 lines.
3. Optional, terminal:
   ```powershell
   cd src\hosted-mcp-tools
   azd ai agent invoke --new-conversation "What theme should the Aster demo use?"
   ```

## Scenario 03 · Automatic recall with AIContextProvider (2 minutes)

**Story:** no tool for the model to choose. The agent framework calls memory before every model
call and injects the result as context.

1. Open the hosted-memory-context playground. New conversation:
   > Where do I live?

   Expect the answer "Zurich" with **no tool call** in the transcript.
2. Show the proof in the container log:
   ```powershell
   cd src\hosted-memory-context
   azd ai agent monitor --tail 20
   ```
   Look for `Automatic recall completed: 1 items for the current question.`
3. Show [RemoteMemoryContextProvider.cs](../src/hosted-memory-context/RemoteMemoryContextProvider.cs):
   `ProvideAIContextAsync` takes the last user message, calls `memory_search`, returns bounded JSON
   wrapped as untrusted reference context.

Say: "Scenario 01 lets the model decide to remember. Scenario 03 makes recall a property of the
agent, not a decision of the model."

## If something breaks on stage

| Symptom | Likely cause | Do this |
|---|---|---|
| Agent says memory search failed, Terminal A shows nothing | Tunnel down or token expired | Check Terminal B; `dotnet run -- check`; mint a new token (see above) |
| Agent calls a tool but answers "nothing stored" | Query too vague, or wrong tool chosen | Ask the exact scripted question; confirm allowed tools are only the three above |
| Hosted agent answers "memory search failed" once, then works | Memory host was restarted; the agent reconnects on the next call | Ask again |
| Playground shows an approval prompt | Approval still enabled on the tool | Approve; or turn approval off in the tool settings before the talk |
| Nothing works, no network | Venue connectivity | Run scenario 01 locally: `cd src\hosted-mcp-tools; dotnet run`, then post to http://localhost:8088/responses as in the README; say it is the local build of the same architecture |


## After the talk

```powershell
# stop the tunnel (Ctrl+C in Terminal B) and the host (Ctrl+C in Terminal A)
devtunnel delete agentmemory-demo          # optional; tokens die with it
docker compose -f local-memory/docker-compose.yml down   # keep volumes = keep memories
```

The hosted agents stay in Foundry. They will fail to start once the token is gone, which is fine.
To remove them: `azd ai agent delete` in each `src/hosted-*` folder.
