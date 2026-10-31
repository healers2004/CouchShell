using System.IO;
using System.Net.Sockets;

namespace CouchShell.Services;

// A command failed: not connected, timed out, or the server replied with an error.
public class CouchException(string message, Exception? inner = null) : Exception(message, inner);

public interface ICouchClient
{
    // True when connected. Raised through the `post` action given to the client.
    event Action<bool>? ConnectionChanged;

    // A pub/sub message: channel, payload. Raised through the `post` action.
    event Action<string, string>? MessageReceived;

    // Sends one command and returns the reply. Throws CouchException on any failure.
    Task<RespValue> SendAsync(params string[] args);
}

// Talks to Tachyon over two connections, because a subscribed connection can't run normal
// commands. Reconnects by itself with a growing delay, so Tachyon can restart at any time.
public sealed class CouchClient(
    string host,
    int port,
    IReadOnlyList<string> channels,
    Action<Action> post) : ICouchClient, IDisposable
{
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);  // one command on the wire at a time
    private Task? _loop;
    private TcpClient? _commandSocket;
    private TcpClient? _subscribeSocket;
    private RespReader? _commandReader;

    public event Action<bool>? ConnectionChanged;
    public event Action<string, string>? MessageReceived;

    public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

    public async Task<RespValue> SendAsync(params string[] args)
    {
        await _commandGate.WaitAsync();
        try
        {
            TcpClient socket = _commandSocket ?? throw new CouchException("Not connected to Tachyon.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(CommandTimeout);
            NetworkStream stream = socket.GetStream();
            await stream.WriteAsync(RespWriter.Encode(args), timeout.Token);
            RespValue reply = await _commandReader!.ReadAsync(timeout.Token);
            return reply.Kind == RespKind.Error ? throw new CouchException(reply.Text ?? "Server error.") : reply;
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException
                                      or InvalidDataException or ObjectDisposedException
                                      or InvalidOperationException)
        {
            // The reply stream can't be trusted now. Dropping both connections makes RunAsync reconnect.
            CloseSockets();
            throw new CouchException("Lost the connection to Tachyon.", e);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        TimeSpan delay = MinRetryDelay;
        while (!token.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                TcpClient subscribeSocket = await ConnectAsync(token);
                _subscribeSocket = subscribeSocket;
                TcpClient commandSocket = await ConnectAsync(token);
                NetworkStream subscribeStream = subscribeSocket.GetStream();
                var subscribeReader = new RespReader(subscribeStream);

                await subscribeStream.WriteAsync(RespWriter.Encode(["SUBSCRIBE", .. channels]), token);
                for (int i = 0; i < channels.Count; i++)
                {
                    await subscribeReader.ReadAsync(token);  // one confirmation per channel
                }

                _commandReader = new RespReader(commandSocket.GetStream());
                _commandSocket = commandSocket;
                connected = true;
                delay = MinRetryDelay;
                post(() => ConnectionChanged?.Invoke(true));

                while (true)
                {
                    RespValue message = await subscribeReader.ReadAsync(token);
                    if (message.ItemsOrEmpty is [{ Text: "message" }, { Text: string channel }, { Text: string payload }])
                    {
                        post(() => MessageReceived?.Invoke(channel, payload));
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidDataException
                                          or OperationCanceledException or ObjectDisposedException
                                          or InvalidOperationException)
            {
                // Tachyon isn't there or went away; retry below.
            }

            await _commandGate.WaitAsync(CancellationToken.None);
            CloseSockets();
            _commandGate.Release();
            if (connected) post(() => ConnectionChanged?.Invoke(false));

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    private async Task<TcpClient> ConnectAsync(CancellationToken token)
    {
        var socket = new TcpClient { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void CloseSockets()
    {
        _commandSocket?.Dispose();
        _subscribeSocket?.Dispose();  // wakes RunAsync's blocked read
        _commandSocket = null;
        _subscribeSocket = null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        CloseSockets();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop ended with an error; there's nothing left to stop.
        }
        _stop.Dispose();
    }
}
