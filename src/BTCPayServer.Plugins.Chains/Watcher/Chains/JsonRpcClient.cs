using System.Text.Json;

namespace NeoPaymentWatcher.Chains;

public sealed class JsonRpcException(string method, int code, string? message, string? name = null, string? cause = null)
    : Exception($"RPC {method} failed with code {code}: {message}{(cause is null ? "" : $" ({cause})")}")
{
    public string Method { get; } = method;
    public int Code { get; } = code;
    /// <summary>The error kind NEAR nodes add ("HANDLER_ERROR", "REQUEST_VALIDATION_ERROR"); null on other chains.</summary>
    public string? Name { get; } = name;
    /// <summary>The error cause NEAR nodes add ("UNKNOWN_BLOCK", "UNKNOWN_ACCOUNT", "GARBAGE_COLLECTED_BLOCK"); null on other chains.</summary>
    public string? Cause { get; } = cause;
}

/// <summary>Plain JSON-RPC 2.0 over HTTP. A failed call is retried on the next configured endpoint, and the one that worked is preferred afterwards.</summary>
public sealed class JsonRpcClient(string[] urls, ILogger logger)
{
    readonly HttpClient http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    int preferred;

    public Task<JsonElement> CallAsync(string method, object?[] parameters, CancellationToken ct) => CallAsync(method, (object)parameters, ct);

    /// <summary>Parameters are positional (an array) on most chains and named (an object) on NEAR; they go out as given.</summary>
    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < urls.Length; attempt++)
        {
            var index = (preferred + attempt) % urls.Length;
            try
            {
                var result = await CallOnceAsync(urls[index], method, parameters, ct);
                preferred = index;
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                logger.LogWarning("RPC {Method} on {Host} failed: {Error}", method, Host(urls[index]), ex.Message);
            }
        }
        throw new InvalidOperationException($"All RPC endpoints failed for {method}.", lastError);
    }

    /// <summary>The JSON-RPC error behind a failed call, if that is what it was, wherever it sits in the chain of causes.</summary>
    public static JsonRpcException? RpcError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is JsonRpcException rpcError)
                return rpcError;
        return null;
    }

    /// <summary>Only the host goes into the log: a keyed endpoint carries its key in the path or query.</summary>
    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>
    /// Reads the JSON-RPC error even when it travels on a non-2xx status: NEAR nodes and their proxies answer a
    /// handler error or a throttled call that way, and the error's cause is what the caller needs.
    /// </summary>
    async Task<JsonElement> CallOnceAsync(string url, string method, object parameters, CancellationToken ct)
    {
        using var response = await HttpClientJsonExtensions.PostAsJsonAsync(http, url, new { jsonrpc = "2.0", id = 1, method, @params = parameters }, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var cause = error.TryGetProperty("cause", out var c) && c.ValueKind == JsonValueKind.Object ? Text(c, "name") : null;
                throw new JsonRpcException(method,
                    error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0,
                    Text(error, "message"), Text(error, "name"), cause);
            }
            response.EnsureSuccessStatusCode();
            return root.GetProperty("result").Clone();
        }
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
