using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XamlG.Agents.OpenAI;

/// <summary>Small, temporary HTTP/1.1 callback listener. It binds the real ephemeral port before
/// publishing a launch link. The IDE receives only a one-use loopback ticket, never an ID-token hint.</summary>
internal sealed class ChatGptLoopbackSignIn : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly string _ticket = ChatGptOAuthProtocol.RandomValue();
    private readonly string _state;
    private readonly Func<IReadOnlyDictionary<string, string>, Task<bool>> _complete;
    private readonly SemaphoreSlim _slots = new(4);
    private string? _authorizationUrl;
    private int _launched, _used, _disposed;
    internal ChatGptLoopbackSignIn(string state, Func<IReadOnlyDictionary<string, string>, Task<bool>> complete,
        CancellationToken owner, CancellationToken lifetime)
    {
        _state = state; _complete = complete;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(owner, lifetime); _stop.CancelAfter(TimeSpan.FromMinutes(10));
        _listener.Start(8);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Authority = "127.0.0.1:" + port;
        RedirectUri = "http://" + Authority + "/auth/callback";
        LaunchUrl = "http://" + Authority + "/auth/start?ticket=" + _ticket;
    }
    private string Authority { get; }
    internal string RedirectUri { get; }
    internal string LaunchUrl { get; }
    internal CancellationToken CancellationToken => _stop.Token;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal void Start(string authorizationUrl) { _authorizationUrl = authorizationUrl; Completion = ListenAsync(); }
    internal void Cancel() { if (Volatile.Read(ref _disposed) == 0) try { _stop.Cancel(); } catch (ObjectDisposedException) { } }
    private async Task ListenAsync()
    {
        var clients = new List<Task>();
        try
        {
            // A normal flow uses two requests. Bound attempts and concurrent connections so a
            // malformed local caller cannot retain unbounded tasks or monopolize all reads.
            for (var count = 0; count < 128; count++)
            {
                await _slots.WaitAsync(_stop.Token);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { _slots.Release(); throw; }
                clients.Add(ServeAsync(client));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally
        {
            _stop.Cancel(); _listener.Stop();
            await Task.WhenAll(clients);
            _authorizationUrl = null;
        }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5)); var stream = client.GetStream();
            try
            {
                var bytes = new byte[16384]; var count = 0; string? header = null;
                while (count < bytes.Length)
                {
                    var received = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token); if (received == 0) return;
                    count += received; var text = Encoding.ASCII.GetString(bytes, 0, count);
                    var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end >= 0) { header = text[..end]; break; }
                }
                if (header == null) { await ReplyAsync(stream, 431, "Request too large.", timeout.Token); return; }
                var lines = header.Split("\r\n"); var request = lines[0].Split(' ');
                if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" || !request[1].StartsWith('/') || request[1].StartsWith("//", StringComparison.Ordinal))
                { await ReplyAsync(stream, 400, "Invalid request.", timeout.Token); return; }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var separator = line.IndexOf(':');
                    if (separator < 1 || !headers.TryAdd(line[..separator], line[(separator + 1)..].Trim()))
                    { await ReplyAsync(stream, 400, "Invalid headers.", timeout.Token); return; }
                }
                if (headers.GetValueOrDefault("Host") != Authority || headers.ContainsKey("Transfer-Encoding") || headers.GetValueOrDefault("Content-Length", "0") != "0")
                { await ReplyAsync(stream, 403, "Request rejected.", timeout.Token); return; }
                var uri = new Uri("http://" + Authority + request[1]);
                if (uri.Fragment.Length != 0) { await ReplyAsync(stream, 400, "Invalid request.", timeout.Token); return; }
                var query = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = part.IndexOf('='); if (separator < 1) throw new ArgumentException();
                    var name = Uri.UnescapeDataString(part[..separator].Replace('+', ' '));
                    var value = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
                    if (!query.TryAdd(name, value)) throw new ArgumentException();
                }
                if (uri.AbsolutePath == "/auth/start")
                {
                    if (!ChatGptOAuthProtocol.Equal(query.GetValueOrDefault("ticket", ""), _ticket) || Interlocked.CompareExchange(ref _launched, 1, 0) != 0)
                    { await ReplyAsync(stream, 403, "This sign-in link has expired or was already opened.", timeout.Token); return; }
                    await ReplyAsync(stream, 302, "Continue in the OpenAI sign-in page.", timeout.Token, _authorizationUrl); return;
                }
                if (uri.AbsolutePath != "/auth/callback") { await ReplyAsync(stream, 404, "Not found.", timeout.Token); return; }
                if (!ChatGptOAuthProtocol.Equal(query.GetValueOrDefault("state", ""), _state) || Interlocked.CompareExchange(ref _used, 1, 0) != 0)
                { await ReplyAsync(stream, 400, "This callback does not match the pending sign-in.", timeout.Token); return; }
                // Token exchange has its own bounded timeout and is not cancelled by a browser
                // closing this TCP connection after the code has been accepted.
                var success = await _complete(query);
                using var reply = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ReplyAsync(stream, success ? 200 : 400, success ? "Sign-in completed. Return to XamlG Studio." : "Sign-in did not complete. Return to XamlG Studio for details.", reply.Token);
                _stop.Cancel();
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ArgumentException or FormatException or ObjectDisposedException) { }
            finally { _slots.Release(); }
        }
    }
    private static async Task ReplyAsync(NetworkStream stream, int status, string message, CancellationToken token, string? redirect = null)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>XamlG Studio sign-in</title><p>" + WebUtility.HtmlEncode(message) + "</p></html>");
        var headers = $"HTTP/1.1 {status} Response\r\nConnection: close\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nX-Content-Type-Options: nosniff\r\n";
        if (redirect != null) headers += "Location: " + redirect + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "\r\n"), token); await stream.WriteAsync(body, token);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await Completion; return; }
        _stop.Cancel(); _listener.Stop(); await Completion; _stop.Dispose(); _slots.Dispose();
    }
}
