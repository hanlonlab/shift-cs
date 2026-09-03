using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Protocol.Tests;

public class SessionCommandCodecTests
{
    [Fact]
    public void StartNewSessionRoundTrips()
    {
        var command = new StartNewSession();
        byte[] payload = [];

        int bytesWritten = StartNewSessionCodec.Encode(command, payload);

        Assert.Equal(0, bytesWritten);
        Assert.True(StartNewSessionCodec.TryDecode(payload, out StartNewSession decoded));
        Assert.Equal(command, decoded);
    }

    [Fact]
    public void StartNewSessionDecodeRejectsPayload()
    {
        Assert.False(StartNewSessionCodec.TryDecode([0x00], out StartNewSession command));
        Assert.Equal(default, command);
    }

    [Fact]
    public void EndCurrentSessionRoundTrips()
    {
        var command = new EndCurrentSession();
        byte[] payload = [];

        int bytesWritten = EndCurrentSessionCodec.Encode(command, payload);

        Assert.Equal(0, bytesWritten);
        Assert.True(EndCurrentSessionCodec.TryDecode(payload, out EndCurrentSession decoded));
        Assert.Equal(command, decoded);
    }

    [Fact]
    public void EndCurrentSessionDecodeRejectsPayload()
    {
        Assert.False(EndCurrentSessionCodec.TryDecode([0x00], out EndCurrentSession command));
        Assert.Equal(default, command);
    }
}
