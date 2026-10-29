using System.Text.Json;
using DotNetEnv;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Local .env (gitignored) for development; the variables below are the whole contract.
Env.TraversePath().Load();

string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing environment variable {name}.");

var memoryUri = new Uri(Environment.GetEnvironmentVariable("MEMORY_MCP_URL") is { Length: > 0 } url
    ? url
    : "http://localhost:5233/");

var headers = new Dictionary<string, string>();
if (!memoryUri.IsLoopback)
{
    if (memoryUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("A remote memory endpoint must use https.");
    headers["X-Tunnel-Authorization"] = "tunnel " + Required("MEMORY_TUNNEL_TOKEN");
}

var command = args.FirstOrDefault()?.TrimStart('-') ?? "tools";
Console.WriteLine($"memory endpoint : {memoryUri}");
Console.WriteLine($"command         : {command}");

await using var memory = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = memoryUri,
    TransportMode = HttpTransportMode.StreamableHttp,
    ConnectionTimeout = TimeSpan.FromSeconds(20),
    AdditionalHeaders = headers,
    Name = "memory-seed-check",
}));

var tools = await memory.ListToolsAsync();
Console.WriteLine($"advertised tools ({tools.Count}): {string.Join(", ", tools.Select(t => t.Name))}");

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

switch (command)
{
    case "tools":
        return 0;

    case "seed":
    {
        if (tools.All(t => t.Name != "memory_add_preference"))
            throw new InvalidOperationException("memory_add_preference is not advertised: the host is read-only. Restart it writable for seeding.");

        var result = await memory.CallToolAsync("memory_add_preference", new Dictionary<string, object?>
        {
            ["category"] = "presentation",
            ["preferenceText"] = "For the Aster demo, I prefer Spanish slides and C# examples.",
            ["context"] = "Aster demonstration presentation",
            ["confidence"] = 1.0,
        }, cancellationToken: timeout.Token);

        var text = Print(result);
        if (result.IsError == true) throw new InvalidOperationException("Memory seed failed.");
        if (!text.Contains("\"persisted\":true", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("\"persisted\": true", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("WARNING: response does not say persisted: true. Inspect it before trusting the seed.");
        return 0;
    }

    case "check":
    {
        var result = await memory.CallToolAsync("memory_search", new Dictionary<string, object?>
        {
            ["query"] = "What presentation preferences apply to the Aster demo?",
            ["sessionId"] = "foundry-demo-check",
            ["maxResults"] = 3,
        }, cancellationToken: timeout.Token);

        var text = Print(result);
        if (result.IsError == true) throw new InvalidOperationException("Memory recall failed.");

        // A 200 with an empty context is the failure mode this tool exists to catch.
        var found = text.Contains("Spanish", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains("Aster", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine(found
            ? "PASS: recall contains the seeded Aster preference."
            : "FAIL: recall did not return the seeded preference. Check owner, schema and embedding configuration.");
        return found ? 0 : 2;
    }

    case "call":
    {
        // Raw call for diagnostics: dotnet run -- call memory_search '{"query":"..."}'
        var toolName = args.ElementAtOrDefault(1) ?? throw new InvalidOperationException("call needs a tool name.");
        var rawArgs = args.ElementAtOrDefault(2) ?? "{}";
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawArgs) ?? [];
        var started = DateTime.UtcNow;
        var result = await memory.CallToolAsync(toolName, parsed, cancellationToken: timeout.Token);
        Print(result);
        Console.WriteLine($"isError={result.IsError} elapsed={(DateTime.UtcNow - started).TotalSeconds:F1}s");
        return result.IsError == true ? 3 : 0;
    }

    case "twice":
    {
        // Diagnostics: call, wait 25 s (restart the host meanwhile), call again; print what comes back.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var r = await memory.CallToolAsync("memory_search", new Dictionary<string, object?> { ["query"] = "Aster demo presentation preferences", ["maxResults"] = 3 });
                Console.WriteLine($"attempt {attempt}: IsError={r.IsError} content={string.Join(" | ", r.Content.OfType<TextContentBlock>().Select(c => c.Text))[..Math.Min(200, string.Join(" | ", r.Content.OfType<TextContentBlock>().Select(c => c.Text)).Length)]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"attempt {attempt}: EXCEPTION {ex.GetType().FullName}: {ex.Message} | inner: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            }
            if (attempt == 1) await Task.Delay(TimeSpan.FromSeconds(25));
        }
        return 0;
    }

    default:
        Console.Error.WriteLine("usage: dotnet run -- tools | seed | check | call <tool> '<json args>'");
        return 1;
}

static string Print(CallToolResult result)
{
    var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    if (text.Length == 0) text = JsonSerializer.Serialize(result);
    Console.WriteLine(text.Length > 4000 ? text[..4000] + "\n...[truncated]" : text);
    return text;
}
