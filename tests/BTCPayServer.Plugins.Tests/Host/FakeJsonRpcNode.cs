using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tests.Host;

/// <summary>A JSON-RPC 2.0 server on a loopback port, answering from whatever blocks the test has added.</summary>
internal abstract class FakeJsonRpcNode : IAsyncDisposable
{
    readonly HttpListener listener = new();
    readonly Task worker;
    volatile bool stopping;

    protected readonly object Gate = new();

    public string Url { get; }

    /// <summary>When set, every request fails with HTTP 429, like a throttling public node.</summary>
    public bool Throttle { get; set; }

    public int Requests => Volatile.Read(ref requests);
    int requests;

    protected FakeJsonRpcNode()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        Url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(Url);
        listener.Start();
        worker = Run();
    }

    protected abstract JToken? Reply(string method, JArray parameters);

    protected sealed class RpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    protected static string Hash(string seed) => "0x" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    async Task Run()
    {
        while (!stopping)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stopping)
            {
                break;
            }
            try
            {
                Interlocked.Increment(ref requests);
                if (Throttle)
                {
                    context.Response.StatusCode = 429;
                    continue;
                }
                using var reader = new StreamReader(context.Request.InputStream);
                var request = JObject.Parse(await reader.ReadToEndAsync());
                var reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"] };
                try
                {
                    reply["result"] = Reply(request["method"]!.Value<string>()!, request["params"] as JArray ?? []) ?? JValue.CreateNull();
                }
                catch (RpcError e)
                {
                    reply["error"] = new JObject { ["code"] = e.Code, ["message"] = e.Message };
                }
                var bytes = Encoding.UTF8.GetBytes(reply.ToString());
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception) when (stopping)
            {
                break;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true;
        listener.Stop();
        listener.Close();
        try
        {
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // The accept loop ends with the listener; nothing else to release.
        }
    }
}
