using Shift.Engine.Matching;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Engine.Tests;

public sealed class MatchingEngineTests
{
    [Fact]
    public void InitializesInactiveWithEmptyLocalOrderBook()
    {
        MatchingEngine engine = new();

        Assert.False(engine.IsSessionActive);
        Assert.Equal(0, engine.LiveOrderCount);
    }

    [Fact]
    public void StartSessionActivatesOnlyOnce()
    {
        MatchingEngine engine = new();

        StartSessionStatus started = engine.StartSession(new StartNewSession());
        StartSessionStatus repeated = engine.StartSession(new StartNewSession());

        Assert.Equal(StartSessionStatus.Started, started);
        Assert.Equal(StartSessionStatus.AlreadyStarted, repeated);
        Assert.True(engine.IsSessionActive);
        Assert.Equal(0, engine.LiveOrderCount);
    }
}
