namespace Shift.Protocol.Framing;

public readonly record struct FrameHeader(
    uint FrameLength,
    byte Version,
    MessageType MessageType,
    Guid SessionId,
    ushort ProducerId,
    ulong ProducerSequence,
    long SequenceId
);

public enum MessageType : ushort
{
    // Commands
    StartNewSession = 1,
    EndCurrentSession = 2,
    NextSimulationStep = 3,
    PlaceOrder = 4,
    CancelOrder = 5,

    // Engine
    OrderUpdated = 6, // Modify in place, cancel, ect.
    TradeExecuted = 7, // Match

    // Archiver & Control
    CommitThrough = 8, // Archiver has durably synced through this sequence

    // Reference commands
    UpdateReferenceQuote = 9
}
