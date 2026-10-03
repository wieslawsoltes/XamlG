using System.Text;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class ConnectionTests
{
    [Fact]
    public async Task FramingCountsUtf8BytesAndPreservesAdjacentMessages()
    {
        using var transport = new MemoryStream();
        await using (var writer = new LspConnection(Stream.Null, transport))
        {
            await writer.WriteAsync(new { jsonrpc = "2.0", id = 1, result = "😀ą" });
            await writer.WriteAsync(new { jsonrpc = "2.0", id = 2, result = "next" });
        }
        transport.Position = 0;
        await using var reader = new LspConnection(transport, Stream.Null);
        using var first = await reader.ReadAsync();
        using var second = await reader.ReadAsync();
        Assert.Equal("😀ą", first!.RootElement.GetProperty("result").GetString());
        Assert.Equal(2, second!.RootElement.GetProperty("id").GetInt32());
        Assert.Null(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: 999999999\r\n\r\n")]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}")]
    [InlineData("Other: 2\r\n\r\n{}")]
    public async Task MalformedHeadersAreRejected(string input)
    {
        await using var connection = new LspConnection(new MemoryStream(Encoding.ASCII.GetBytes(input)), Stream.Null);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await connection.ReadAsync());
    }

    [Fact]
    public async Task TruncatedPayloadIsNotAccepted()
    {
        await using var connection = new LspConnection(new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 10\r\n\r\n{}")), Stream.Null);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await connection.ReadAsync());
    }
}
