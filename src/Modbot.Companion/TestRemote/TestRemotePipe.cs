using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Modbot.Companion.TestRemote;

/// <summary>
/// The test remote's listening end: one line of text in, one line of JSON out, per connection.
/// </summary>
/// <remarks>
/// <para><strong>Only in a test copy started in debug mode.</strong> Built only when
/// <see cref="TestRemoteSwitch.PipeName(Startup.DataFolder, bool)"/> gives a name; any other copy
/// has no listener and runs none of this.</para>
/// <para><strong>What this reads, and from where.</strong> One named pipe on this PC, opened so
/// that only the same Windows account can connect to it (on Linux, a Unix socket only the same
/// user can open). What comes through is one short line, bounded in size and read once per
/// connection. Nothing is read from disk here and nothing is written to it.</para>
/// <para><strong>Nothing leaves the machine.</strong> A named pipe never crosses the network, and
/// there is no TCP port. The answer goes back down the same pipe to the program that asked.</para>
/// </remarks>
public sealed class TestRemoteServer
{
    /// <summary>A command is a few words; the bound keeps a misbehaving neighbour small.</summary>
    public const int MaxLineBytes = 4 * 1024;

    /// <summary>How long a connected caller gets to send its line.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;

    public TestRemoteServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    /// <summary>
    /// Answers lines until cancelled. Returns quietly if the pipe cannot be made, which means
    /// another copy using the same folder already owns it.
    /// </summary>
    /// <param name="answer">One line in, one line of JSON out.</param>
    public async Task ListenAsync(Func<string, Task<string>> answer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);

        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        await using (server.ConfigureAwait(false))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    Disconnect(server);
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var line = await ReadLineAsync(server, ReadTimeout, cancellationToken).ConfigureAwait(false);
                    var reply = line is null
                        ? RemoteAnswers.Error("The command was too long, or did not arrive.")
                        : await answer(line).ConfigureAwait(false);

                    var bytes = Encoding.UTF8.GetBytes(reply.ReplaceLineEndings(" ") + "\n");
                    await server.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await server.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (OperatingSystem.IsWindows())
                        server.WaitForPipeDrain();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException)
                {
                    // The caller went away before its answer was written.
                }
                finally
                {
                    Disconnect(server);
                }
            }
        }
    }

    private static void Disconnect(NamedPipeServerStream server)
    {
        try
        {
            server.Disconnect();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Already disconnected; the next wait starts clean.
        }
    }

    /// <summary>Everything up to the first line break, bounded; null when it is too long or stalls.</summary>
    internal static async Task<string?> ReadLineAsync(Stream pipe, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        var buffer = new byte[MaxLineBytes + 1];
        var total = 0;

        try
        {
            while (total < buffer.Length)
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(total, 1), limit.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (buffer[total] == (byte)'\n')
                    return Encoding.UTF8.GetString(buffer, 0, total).Trim();

                total += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return total > MaxLineBytes ? null : Encoding.UTF8.GetString(buffer, 0, total).Trim();
    }
}

/// <summary>The asking end, for the command line tool and the tests.</summary>
public static class TestRemoteClient
{
    /// <summary>
    /// Sends one line and waits for the one line that comes back. Null when nothing is listening
    /// on that pipe, or nothing came back in time.
    /// </summary>
    public static async Task<string?> AskAsync(
        string pipeName,
        string line,
        TimeSpan connectTimeout,
        TimeSpan answerTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(line);

        var bytes = Encoding.UTF8.GetBytes(line.ReplaceLineEndings(" ") + "\n");
        if (bytes.Length > TestRemoteServer.MaxLineBytes)
            return null;

        try
        {
            var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await using (client.ConfigureAwait(false))
            {
                await client.ConnectAsync((int)connectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                await client.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);

                return await TestRemoteServer.ReadLineAsync(client, answerTimeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// One line in, one line of JSON out: the line read as a command, handed to whatever carries it
/// out, and the answer written with <c>ok</c> true or false.
/// </summary>
public static class RemoteAnswers
{
    /// <param name="handle">Carries a command out; <c>wait</c> never reaches it.</param>
    public static async Task<string> AnswerAsync(
        string line,
        Func<RemoteCommand, Task<JsonObject>> handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var parsed = RemoteCommands.Parse(line);
        if (parsed.Command is not { } command)
            return Error(parsed.Error ?? "Not a command.");

        try
        {
            if (command is RemoteCommand.Wait wait)
            {
                await Task.Delay(wait.Milliseconds, cancellationToken).ConfigureAwait(false);
                return Ok(new JsonObject { ["waited"] = wait.Milliseconds });
            }

            return Ok(await handle(command).ConfigureAwait(false));
        }
        catch (RemoteRefusal refused)
        {
            return Error(refused.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>An answer that worked: <c>ok</c> true, then whatever the command said.</summary>
    public static string Ok(JsonObject said)
    {
        ArgumentNullException.ThrowIfNull(said);

        var answer = new JsonObject { ["ok"] = true };
        foreach (var (key, value) in said.ToList())
        {
            said.Remove(key);
            answer[key] = value;
        }

        return answer.ToJsonString();
    }

    /// <summary>An answer that did not: <c>ok</c> false and why.</summary>
    public static string Error(string why) => new JsonObject { ["ok"] = false, ["error"] = why }.ToJsonString();
}

/// <summary>A command the remote will not carry out, with why, said back as the answer's error.</summary>
public sealed class RemoteRefusal(string why) : Exception(why);
