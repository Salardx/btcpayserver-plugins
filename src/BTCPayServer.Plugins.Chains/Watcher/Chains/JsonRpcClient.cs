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

    /// <summary>
    /// Several calls in one HTTP request; the results come back in the order asked. A failed batch is retried on the
    /// next configured endpoint. A call the node answers with an error fails the batch, unless
    /// <paramref name="tolerant"/> is set: then that call's result is left undefined and the others stand.
    /// </summary>
    public async Task<JsonElement[]> CallBatchAsync(IReadOnlyList<(string Method, object?[] Parameters)> calls, CancellationToken ct, bool tolerant = false)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < urls.Length; attempt++)
        {
            var index = (preferred + attempt) % urls.Length;
            try
            {
                var results = await BatchOnceAsync(urls[index], calls, tolerant, ct);
                preferred = index;
                return results;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                logger.LogWarning("RPC batch of {Method} on {Host} failed: {Error}", calls[^1].Method, Host(urls[index]), ex.Message);
            }
        }
        throw new InvalidOperationException($"All RPC endpoints failed for a batch of {calls[^1].Method}.", lastError);
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
                throw Error(method, error);
            response.EnsureSuccessStatusCode();
            return root.GetProperty("result").Clone();
        }
    }

    async Task<JsonElement[]> BatchOnceAsync(string url, IReadOnlyList<(string Method, object?[] Parameters)> calls, bool tolerant, CancellationToken ct)
    {
        var body = calls.Select((call, id) => new { jsonrpc = "2.0", id, method = call.Method, @params = call.Parameters }).ToArray();
        using var response = await HttpClientJsonExtensions.PostAsJsonAsync(http, url, body, ct);
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
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var refused) && refused.ValueKind == JsonValueKind.Object)
                throw Error(calls[^1].Method, refused);
            response.EnsureSuccessStatusCode();
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != calls.Count)
                throw new InvalidOperationException("The node did not answer every call of the batch.");
            var results = new JsonElement[calls.Count];
            var answered = new bool[calls.Count];
            foreach (var item in root.EnumerateArray())
            {
                var id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : -1;
                if (id < 0 || id >= calls.Count || answered[id])
                    throw new InvalidOperationException("The node answered the batch with an id that was not asked.");
                answered[id] = true;
                if (item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (!tolerant)
                        throw Error(calls[id].Method, error);
                    continue;
                }
                results[id] = item.GetProperty("result").Clone();
            }
            return results;
        }
    }

    static JsonRpcException Error(string method, JsonElement error) => new(method,
        error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0,
        Text(error, "message"), Text(error, "name"),
        error.TryGetProperty("cause", out var cause) && cause.ValueKind == JsonValueKind.Object ? Text(cause, "name") : null);

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
