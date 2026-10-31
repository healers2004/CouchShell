using System.IO;
using System.Text;
using CouchShell.Services;

namespace CouchShell.Tests;

public class RespTests
{
    // Delivers one byte per read, the worst case TCP can produce.
    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }

    private static Task<RespValue> Read(string text) =>
        new RespReader(new OneByteStream(Encoding.UTF8.GetBytes(text))).ReadAsync(CancellationToken.None);

    [Fact]
    public async Task ReadsEveryReplyType()
    {
        Assert.Equal("OK", (await Read("+OK\r\n")).Text);
        Assert.Equal(RespKind.Error, (await Read("-ERR no\r\n")).Kind);
        Assert.Equal(-3, (await Read(":-3\r\n")).Integer);
        Assert.Equal("bar", (await Read("$3\r\nbar\r\n")).Text);
        Assert.Equal(RespKind.Null, (await Read("$-1\r\n")).Kind);
        Assert.Equal(RespKind.Null, (await Read("*-1\r\n")).Kind);
    }

    [Fact]
    public async Task BulkMayContainCrlfAndUnicode()
    {
        Assert.Equal("a\r\nb", (await Read("$4\r\na\r\nb\r\n")).Text);
        Assert.Equal("é", (await Read("$2\r\né\r\n")).Text);
    }

    [Fact]
    public async Task ReadsArraysAsDictionaries()
    {
        RespValue value = await Read("*4\r\n$1\r\na\r\n$1\r\n1\r\n$1\r\nb\r\n$1\r\n2\r\n");
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, value.ToDictionary());
    }

    [Theory]
    [InlineData("?x\r\n")]
    [InlineData(":abc\r\n")]
    [InlineData("$-2\r\n")]
    [InlineData("$99999999999\r\n")]
    [InlineData("$3\r\nfooXY")]
    [InlineData("*-5\r\n")]
    public async Task RejectsMalformedReplies(string text)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(text));
    }

    [Fact]
    public async Task ReportsAClosedConnection()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => Read("$5\r\nab"));
    }

    [Fact]
    public void EncodesCommandsWithByteLengths()
    {
        Assert.Equal("*2\r\n$3\r\nGET\r\n$2\r\né\r\n", Encoding.UTF8.GetString(RespWriter.Encode(["GET", "é"])));
    }
}
