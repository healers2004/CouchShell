using System.Globalization;
using System.IO;
using System.Text;

namespace CouchShell.Services;

public enum RespKind { Simple, Error, Integer, Bulk, Null, Array }

// One reply from the server. Which fields are set depends on Kind.
public sealed record RespValue(
    RespKind Kind,
    string? Text = null,
    long Integer = 0,
    IReadOnlyList<RespValue>? Items = null)
{
    public static readonly RespValue Null = new(RespKind.Null);

    public IReadOnlyList<RespValue> ItemsOrEmpty => Items ?? [];

    // HGETALL replies with field, value, field, value...
    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>();
        for (int i = 0; i + 1 < ItemsOrEmpty.Count; i += 2)
        {
            result[ItemsOrEmpty[i].Text ?? ""] = ItemsOrEmpty[i + 1].Text ?? "";
        }
        return result;
    }
}

public static class RespWriter
{
    // A command is an array of length-prefixed strings: *2\r\n$3\r\nGET\r\n$3\r\nfoo\r\n
    public static byte[] Encode(IReadOnlyList<string> args)
    {
        using var output = new MemoryStream();
        Append(output, $"*{args.Count}\r\n");
        foreach (string arg in args)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(arg);
            Append(output, $"${bytes.Length}\r\n");
            output.Write(bytes);
            Append(output, "\r\n");
        }
        return output.ToArray();
    }

    private static void Append(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));
}

// Reads replies from a stream. TCP can deliver them in any number of pieces, so all reads go
// through a buffer that refills only when it's empty.
public sealed class RespReader(Stream stream)
{
    private const int MaxBulkBytes = 16 * 1024 * 1024;
    private const int MaxArrayItems = 1024 * 1024;
    private const int MaxLineBytes = 64 * 1024;
    private const int MaxDepth = 8;

    private readonly byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    public Task<RespValue> ReadAsync(CancellationToken token) => ReadValueAsync(0, token);

    private async Task<RespValue> ReadValueAsync(int depth, CancellationToken token)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Reply is nested too deeply.");

        string line = await ReadLineAsync(token);
        if (line.Length == 0) throw new InvalidDataException("Empty reply line.");
        string rest = line[1..];
        switch (line[0])
        {
            case '+':
                return new RespValue(RespKind.Simple, rest);
            case '-':
                return new RespValue(RespKind.Error, rest);
            case ':':
                return new RespValue(RespKind.Integer, Integer: ParseNumber(rest));
            case '$':
                long size = ParseNumber(rest);
                if (size == -1) return RespValue.Null;
                if (size < 0 || size > MaxBulkBytes) throw new InvalidDataException("Bad bulk length.");
                return new RespValue(RespKind.Bulk, await ReadBulkAsync((int)size, token));
            case '*':
                long count = ParseNumber(rest);
                if (count == -1) return RespValue.Null;
                if (count < 0 || count > MaxArrayItems) throw new InvalidDataException("Bad array length.");
                var items = new List<RespValue>((int)Math.Min(count, 1024));
                for (long i = 0; i < count; i++)
                {
                    items.Add(await ReadValueAsync(depth + 1, token));
                }
                return new RespValue(RespKind.Array, Items: items);
            default:
                throw new InvalidDataException($"Unknown reply type '{line[0]}'.");
        }
    }

    private static long ParseNumber(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
            ? value
            : throw new InvalidDataException($"Bad number '{text}'.");

    private async Task<string> ReadLineAsync(CancellationToken token)
    {
        var line = new List<byte>();
        while (true)
        {
            byte current = await ReadByteAsync(token);
            if (current == (byte)'\n' && line.Count > 0 && line[^1] == (byte)'\r')
            {
                line.RemoveAt(line.Count - 1);
                return Encoding.UTF8.GetString(line.ToArray());
            }
            line.Add(current);
            if (line.Count > MaxLineBytes) throw new InvalidDataException("Reply line is too long.");
        }
    }

    private async Task<string> ReadBulkAsync(int size, CancellationToken token)
    {
        var bytes = new byte[size];
        for (int filled = 0; filled < size;)
        {
            if (_start == _end) await FillAsync(token);
            int count = Math.Min(size - filled, _end - _start);
            Array.Copy(_buffer, _start, bytes, filled, count);
            _start += count;
            filled += count;
        }
        if (await ReadByteAsync(token) != (byte)'\r' || await ReadByteAsync(token) != (byte)'\n')
        {
            throw new InvalidDataException("Bulk string isn't followed by CRLF.");
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private async ValueTask<byte> ReadByteAsync(CancellationToken token)
    {
        if (_start == _end) await FillAsync(token);
        return _buffer[_start++];
    }

    private async ValueTask FillAsync(CancellationToken token)
    {
        _start = 0;
        _end = await stream.ReadAsync(_buffer, token);
        if (_end == 0) throw new EndOfStreamException("The server closed the connection.");
    }
}
