# Starts the AgentMemory MCP host over HTTP, pinned to one owner.
#   .\run-mcp.ps1            -> writable (use only for local seeding, no tunnel running)
#   .\run-mcp.ps1 -ReadOnly  -> read-only (the mode exposed through the tunnel)
#
# By default the global tool `agent-memory-mcp` (dotnet tool install -g AgentMemory.McpHost) is used.
# If AGENT_MEMORY_MCP_HOST_PROJECT points at a checked-out tools/AgentMemory.McpHost/*.csproj, that
# project is run instead (needed while --owner is not yet in the published package).
param([switch]$ReadOnly, [string]$LogLevel)

$envFile = Join-Path $PSScriptRoot ".env"
if (-not (Test-Path $envFile)) { throw "Missing $envFile. Copy .env.example to .env and fill it in." }
Get-Content $envFile | Where-Object { $_ -match '^\s*[^#].*=' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    [Environment]::SetEnvironmentVariable($name.Trim(), $value.Trim(), 'Process')
}

$flags = @('--transport', 'http', '--url', $env:AGENT_MEMORY_MCP_URL, '--owner', $env:AGENT_MEMORY_MCP_OWNER)
if ($ReadOnly) { $flags += "--read-only" }
if ($LogLevel) { $flags += @("--log-level", $LogLevel) }

if ($env:AGENT_MEMORY_MCP_HOST_PROJECT) {
    Write-Host "dotnet run --project $env:AGENT_MEMORY_MCP_HOST_PROJECT -- $($flags -join ' ')"
    dotnet run --project $env:AGENT_MEMORY_MCP_HOST_PROJECT --no-build -- @flags
} else {
    Write-Host "agent-memory-mcp $($flags -join ' ')"
    agent-memory-mcp @flags
}
