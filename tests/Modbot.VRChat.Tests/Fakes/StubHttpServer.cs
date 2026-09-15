using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// A minimal HTTP server on loopback, for the tests that have to exercise the real SDK.
/// </summary>
/// <remarks>
/// A raw socket rather than <c>HttpListener</c>, which on Windows needs a URL reservation and
/// therefore an administrator, and a suite that only runs for administrators is a suite that
/// stops running.
/// </remarks>
public sealed class StubHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<string, (int Status, string Body)> _respond;

    public StubHttpServer(Func<string, (int Status, string Body)> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Request lines the server saw, so a test can assert what was sent.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Each request's line and headers, so a test can assert what was sent with it.</summary>
    public List<string> Heads { get; } = [];

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            await using var stream = client.GetStream();

            var request = new StringBuilder();
            var buffer = new byte[1024];

            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, _stopping.Token);
                if (read == 0)
                    return;

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var requestLine = request.ToString().Split("\r\n")[0];
            lock (Requests)
            {
                Requests.Add(requestLine);
                Heads.Add(request.ToString());
            }

            var (status, body) = _respond(requestLine);
            var payload = Encoding.UTF8.GetBytes(body);

            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {payload.Length}\r\n"
                + "X-Stub-Server: modbot-tests\r\n"
                + "Connection: close\r\n\r\n");

            await stream.WriteAsync(head, _stopping.Token);
            await stream.WriteAsync(payload, _stopping.Token);
            await stream.FlushAsync(_stopping.Token);
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        401 => "Unauthorized",
        403 => "Forbidden",
        429 => "Too Many Requests",
        _ => "Status",
    };
}
