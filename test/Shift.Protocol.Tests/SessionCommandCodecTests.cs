using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Protocol.Tests;

public class SessionCommandCodecTests
{
    [Fact]
    public void StartNewSessionRoundTrips()
    {
        var command = new StartNewSession(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        byte[] payload = new byte[16];

        int bytesWritten = StartNewSessionCodec.Encode(command, payload);

        Assert.Equal(payload.Length, bytesWritten);
        Assert.True(StartNewSessionCodec.TryDecode(payload, out StartNewSession decoded));
        Assert.Equal(command, decoded);
    }

    [Fact]
    public void StartNewSessionEncodeRejectsEmptySessionId()
    {
        byte[] payload = new byte[16];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StartNewSessionCodec.Encode(new StartNewSession(Guid.Empty), payload));
    }

    [Fact]
    public void StartNewSessionDecodeRejectsEmptySessionId()
    {
        Assert.False(StartNewSessionCodec.TryDecode(new byte[16], out StartNewSession command));
        Assert.Equal(default, command);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void StartNewSessionDecodeRejectsIncorrectPayloadLength(int length)
    {
        Assert.False(StartNewSessionCodec.TryDecode(new byte[length], out _));
    }

    [Fact]
    public void EndCurrentSessionAcceptsOnlyEmptyPayload()
    {
        Assert.True(EndCurrentSessionCodec.IsValidPayload([]));
        Assert.False(EndCurrentSessionCodec.IsValidPayload([0x00]));
    }
}
