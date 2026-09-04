using Shift.Protocol.Internal.Commands;

namespace Shift.Engine.Matching;

public enum StartSessionStatus : byte
{
    Started = 1,
    AlreadyStarted = 2
}

public sealed class MatchingEngine
{
    private readonly LocalOrderBook _localOrderBook = new();

    public bool IsSessionActive { get; private set; }

    public int LiveOrderCount => _localOrderBook.Count;

    public StartSessionStatus StartSession(StartNewSession command)
    {
        if (IsSessionActive)
        {
            return StartSessionStatus.AlreadyStarted;
        }

        IsSessionActive = true;
        return StartSessionStatus.Started;
    }
}
