using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace HostedMcpTools;

/// <summary>
/// Connection to the AgentMemory MCP host that survives a host restart. The hosted agent lives for
/// days while the MCP host on the presenter's PC may be restarted; a streamable-HTTP session does not
/// survive that ("Session not found"). Every call goes through here: on failure the client is thrown
/// away, recreated, and the call retried once.
/// </summary>
internal sealed class MemoryConnection(Uri endpoint, Dictionary<string, string> headers, string clientName)
    : IAsyncDisposable
{
    private static readonly TimeSpan FirstAttemptTimeout = TimeSpan.FromSeconds(12);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;

    public Uri Endpoint { get; } = endpoint;

    /// <summary>Reads MEMORY_MCP_URL and, for a non-loopback https endpoint, MEMORY_TUNNEL_TOKEN.</summary>
    public static MemoryConnection FromEnvironment(string clientName)
    {
        static string Required(string name) =>
            System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"{name} is not set.");

        var uri = new Uri(Required("MEMORY_MCP_URL"));
        var headers = new Dictionary<string, string>();
        if (!uri.IsLoopback)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Remote memory must use https.");
            // Dev Tunnels authenticate non-interactive clients with the connect token in this header.
            headers["X-Tunnel-Authorization"] = "tunnel " + Required("MEMORY_TUNNEL_TOKEN");
        }

        return new MemoryConnection(uri, headers, clientName);
    }

    public Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        WithRetryAsync((client, ct) => client.ListToolsAsync(cancellationToken: ct), cancellationToken);

    public Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        WithRetryAsync((client, ct) => client.CallToolAsync(toolName, arguments, cancellationToken: ct), cancellationToken);

    private async Task<T> WithRetryAsync<T>(Func<McpClient, CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        // A call on a session the host no longer knows ends in TaskCanceledException from the SDK,
        // not an HTTP error, so the first attempt gets a short cap and any failure that the caller
        // did not cause leads to one reconnect-and-retry.
        try
        {
            using var firstAttempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstAttempt.CancelAfter(FirstAttemptTimeout);
            return await operation(await GetClientAsync(firstAttempt.Token), firstAttempt.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"Memory call failed ({ex.GetType().Name}: {ex.Message}); reconnecting once.");
            await ResetAsync();
            return await operation(await GetClientAsync(cancellationToken), cancellationToken);
        }
    }

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { } existing) return existing;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is { } raced) return raced;

            var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(20),
                AdditionalHeaders = headers,
                Name = clientName,
            }), cancellationToken: cancellationToken);

            Console.WriteLine($"Connected to AgentMemory MCP host at {Endpoint}.");
            return _client = client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var old = _client;
            _client = null;
            if (old is not null)
            {
                try { await old.DisposeAsync(); } catch { /* the session is already gone */ }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }
}
