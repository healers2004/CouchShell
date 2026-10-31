using System.IO;
using System.Net;
using System.Net.Sockets;
using CouchShell.Services;

namespace CouchShell.Tests;

// Runs the real client against a small scripted server on a real socket.
public class CouchClientTests
{
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<TcpClient> _clients = [];
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }
        public TaskCompletionSource<NetworkStream> Subscriber { get; private set; } = new();

        public FakeServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    lock (_clients) _clients.Add(client);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        // A command is an array of bulk strings, so the reply reader can parse it too.
        private async Task ServeAsync(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            var reader = new RespReader(stream);
            try
            {
                while (true)
                {
                    RespValue command = await reader.ReadAsync(_stop.Token);
                    string name = command.ItemsOrEmpty[0].Text!;
                    string reply = name switch
                    {
                        "SUBSCRIBE" => "*3\r\n$9\r\nsubscribe\r\n$1\r\nc\r\n:1\r\n",
                        "FAIL" => "-ERR nope\r\n",
                        _ => $"+{name}\r\n",
                    };
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply));
                    if (name == "SUBSCRIBE") Subscriber.TrySetResult(stream);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        public void DropClients()
        {
            Subscriber = new TaskCompletionSource<NetworkStream>();
            lock (_clients)
            {
                foreach (TcpClient client in _clients) client.Dispose();
                _clients.Clear();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            DropClients();
        }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<bool> NextConnectionState(Queue<bool> seen, SemaphoreSlim signal)
    {
        Assert.True(await signal.WaitAsync(Timeout), "timed out waiting for a connection change");
        lock (seen) return seen.Dequeue();
    }

    [Fact]
    public async Task SendsCommandsReceivesMessagesAndReconnects()
    {
        using var server = new FakeServer();
        var states = new Queue<bool>();
        var stateSignal = new SemaphoreSlim(0);
        var message = new TaskCompletionSource<(string, string)>();

        using var client = new CouchClient("127.0.0.1", server.Port, ["c"], action => action());
        client.ConnectionChanged += state =>
        {
            lock (states) states.Enqueue(state);
            stateSignal.Release();
        };
        client.MessageReceived += (channel, payload) => message.TrySetResult((channel, payload));
        client.Start();

        Assert.True(await NextConnectionState(states, stateSignal));
        Assert.Equal("PING", (await client.SendAsync("PING")).Text);
        await Assert.ThrowsAsync<CouchException>(() => client.SendAsync("FAIL"));

        NetworkStream subscriber = await server.Subscriber.Task.WaitAsync(Timeout);
        await subscriber.WriteAsync("*3\r\n$7\r\nmessage\r\n$1\r\nc\r\n$2\r\nhi\r\n"u8.ToArray());
        Assert.Equal(("c", "hi"), await message.Task.WaitAsync(Timeout));

        server.DropClients();
        Assert.False(await NextConnectionState(states, stateSignal));
        Assert.True(await NextConnectionState(states, stateSignal));
        Assert.Equal("PING", (await client.SendAsync("PING")).Text);
    }

    [Fact]
    public async Task SendFailsCleanlyWhenNotConnected()
    {
        using var client = new CouchClient("127.0.0.1", 1, ["c"], action => action());
        await Assert.ThrowsAsync<CouchException>(() => client.SendAsync("PING"));
    }
}
