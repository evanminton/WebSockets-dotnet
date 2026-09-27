namespace WebSockets.Tests;

public class BlobTests
{
    [Fact]
    public async Task Parts_are_concatenated_and_strings_are_utf8()
    {
        var inner = new Blob("xy"u8);
        var blob = new Blob(["hé", new byte[] { 1, 2 }, new ReadOnlyMemory<byte>([3]), inner], "Text/Plain");

        Assert.Equal(3 + 2 + 1 + 2, blob.Size);
        Assert.Equal("text/plain", blob.Type);
        Assert.Equal(new byte[] { 0x68, 0xC3, 0xA9, 1, 2, 3, (byte)'x', (byte)'y' }, await blob.ArrayBufferAsync());
    }

    [Fact]
    public void Type_with_non_printable_characters_becomes_empty()
    {
        Assert.Equal("", new Blob(ReadOnlySpan<byte>.Empty, "text/é").Type);
        Assert.Equal("", new Blob(ReadOnlySpan<byte>.Empty, "a\nb").Type);
    }

    [Theory]
    [InlineData(null, null, "abcdef")]
    [InlineData(2L, null, "cdef")]
    [InlineData(-2L, null, "ef")]
    [InlineData(1L, -1L, "bcde")]
    [InlineData(4L, 2L, "")]
    [InlineData(-100L, 100L, "abcdef")]
    public async Task Slice_follows_the_file_api_rules(long? start, long? end, string expected)
    {
        var blob = new Blob("abcdef"u8);
        Assert.Equal(expected, await blob.Slice(start, end).TextAsync());
    }

    [Fact]
    public async Task Text_strips_a_bom_and_stream_reads_bytes()
    {
        var blob = new Blob([new byte[] { 0xEF, 0xBB, 0xBF }, "hi"]);
        Assert.Equal("hi", await blob.TextAsync());

        using var copy = new MemoryStream();
        await blob.Stream().CopyToAsync(copy);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' }, copy.ToArray());
    }
}
