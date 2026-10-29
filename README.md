# MAF, Foundry & AgentMemory

Three scenarios that put a **Microsoft Agent Framework (MAF)** agent in **Microsoft Foundry** and
give it persistent memory that lives in **Neo4j on your own PC**, through
[AgentMemory for .NET](https://github.com/joslat/agent-memory-dotnet).

| # | Scenario | Where the agent runs | How it reaches memory | Status |
|---|----------|----------------------|-----------------------|--------|
| 01 | Hosted MAF agent | Foundry Hosted Agent (C#, source deploy) | Client-side MCP from the hosted process | must ship |
| 02 | Prompt Agent + local MCP | Foundry Prompt Agent (portal) | Remote MCP tool over an authenticated Dev Tunnel | main memory demo |
| 03 | Hosted MAF + context provider | Foundry Hosted Agent (C#) | `AIContextProvider` calls memory before inference | stretch |

All three share one local memory service: the `agent-memory-mcp` HTTP host, pinned to owner
`foundry-demo`, in front of a dedicated Neo4j. Start with [docs/SCENARIOS.md](docs/SCENARIOS.md) for what
each scenario is and [docs/DEMO-GUIDE.md](docs/DEMO-GUIDE.md) for the stage script.

## Layout

| Path | Purpose |
|------|---------|
| `docs/` | [SCENARIOS.md](docs/SCENARIOS.md) (what each scenario is and how it works, simplest first), [DEMO-GUIDE.md](docs/DEMO-GUIDE.md) (stage script with links) |
| `local-memory/` | Dedicated Neo4j compose file, `.env.example`, and `run-mcp.ps1` to start the MCP host (writable or read-only). |
| `src/memory-seed-check/` | Plain MCP client: `seed` one preference, `check` recall, `tools` list, `call <tool> <json>` raw diagnostics. Works against localhost or the tunnel. |
| `src/hosted-mcp-tools/` | Scenario 01: the hosted MAF agent, scaffolded from Microsoft's `Hosted-McpTools` sample, pointed at local memory. |
| `scenarios/02-prompt-agent/` | Scenario 02: portal configuration of the Prompt Agent and its MCP tool. No secrets. |
| `src/hosted-memory-context/` | Scenario 03: hosted MAF agent with `RemoteMemoryContextProvider`. |

## Prerequisites

- .NET 10 SDK, Docker, Azure CLI, `azd` 1.37+ with the `azure.ai.agents` extension, `devtunnel`.
- An existing Foundry project with a chat model deployment and an embedding deployment.
- `dotnet tool install -g AgentMemory.McpHost` (provides `agent-memory-mcp`).

## Quick start: local memory

```powershell
# 1. Neo4j (127.0.0.1 only)
docker compose -f local-memory/docker-compose.yml up -d

# 2. Configure: copy local-memory/.env.example to local-memory/.env and fill in the Azure OpenAI key.

# 3. Writable host for seeding (no tunnel yet)
.\local-memory\run-mcp.ps1

# 4. Seed one preference, then verify recall with no chat model involved
cd src/memory-seed-check
dotnet run -- seed
dotnet run -- check

# 5. For the Prompt Agent demo the host stays writable so the agent can remember.
#    (memory_add_preference / memory_add_fact). Use -ReadOnly when you only need recall.
.\local-memory\run-mcp.ps1
```

## Run a hosted agent locally

Each hosted project has an `.env.example`. Copy it to `.env`, start the agent with `dotnet run`
(it listens on port 8088) and post to its Responses endpoint:

```powershell
cd src/hosted-mcp-tools        # or src/hosted-memory-context
copy .env.example .env
dotnet run
# other terminal
Invoke-RestMethod -Method Post -Uri http://localhost:8088/responses -ContentType 'application/json' `
  -Body '{"input":"What presentation preferences apply to the Aster demo?","stream":false}'
```

Scenario 01 shows a `function_call` to `memory_search` in the output. Scenario 03 shows none: its
console logs `Automatic recall completed` before the model answers.

## Deploy a hosted agent to Foundry (scenarios 01 and 03)

Both `azure.yaml` files pin the **existing** project through `ai-project.endpoint`, read from the azd
environment variable `FOUNDRY_PROJECT_ENDPOINT`. Without that line `azd provision` silently creates a
new Foundry account. Always preview first.

```powershell
cd src/hosted-mcp-tools            # or src/hosted-memory-context
azd config set auth.useAzCliAuth true
azd env new hosted-mcp-tools-dev   # once; or run `azd ai agent init -t .zure.yaml -p <project resource id> -d gpt-5-mini`
azd env set FOUNDRY_PROJECT_ENDPOINT "https://<your-account>.services.ai.azure.com/api/projects/<your-project>"
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME <your-chat-model-deployment>
azd env set MEMORY_MCP_URL "https://<tunnel>.devtunnels.ms/"
azd env set MEMORY_TUNNEL_TOKEN "<connect token>"     # stays in the gitignored .azure folder
azd provision --preview            # must say: Using existing Foundry project; nothing to provision
azd provision
azd deploy
azd ai agent invoke --new-conversation "What presentation preferences apply to the Aster demo?"
```

The hosted process connects to the tunnel at startup, so the MCP host and `devtunnel host` must be
running before `azd deploy`, or the agent never becomes ready.

## Fork this

Five values are yours to set; nothing else in the repo is environment-specific.

| Value | Where |
|---|---|
| Foundry project endpoint | `azd env set FOUNDRY_PROJECT_ENDPOINT ...` in each `src/hosted-*`; `FOUNDRY_PROJECT_ENDPOINT` in their `.env` for local runs |
| Chat model deployment (must support tools) | `AZURE_AI_MODEL_DEPLOYMENT_NAME` / `FOUNDRY_MODEL`; `AZURE_OPENAI_DEPLOYMENT` in `local-memory/.env` |
| Embedding deployment | `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` in `local-memory/.env` (dimensions must match the Neo4j vector index; 1536 for text-embedding-3-small) |
| Azure OpenAI key | `AZURE_OPENAI_API_KEY` in `local-memory/.env` |
| Neo4j password | `local-memory/docker-compose.yml` and `local-memory/.env` (default is a local-only demo value) |

Before any deploy: `dotnet run -- check` in `src/memory-seed-check` must pass, and
`azd provision --preview` must print "Using existing Foundry project; nothing to provision".

## Security notes

- The MCP host has no authentication of its own. It listens on localhost; the Dev Tunnel connect
  token (`X-Tunnel-Authorization: tunnel <token>`) is the only boundary. Never add `--allow-anonymous`.
- Only the MCP port is tunnelled. Neo4j is never exposed.
- The host is writable only because scenario 02 demonstrates remembering. The hosted C# agents never
  expose a write tool. Run the host with `-ReadOnly` when you do not need writes, and stop the tunnel
  after the talk.
- The hosted agents keep one MCP session to the host. `MemoryConnection` recreates it and retries once
  when a call fails, so restarting the host on the PC does not require redeploying the agents.
- Tokens live in `.env` files and the `azd` environment, both gitignored. Never in READMEs or screenshots.
