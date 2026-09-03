namespace Shift.Protocol.Framing;

public readonly struct CanonicalFrame
{
    internal CanonicalFrame(
        ReadOnlyMemory<byte> bytes,
        FrameHeader header,
        ReadOnlyMemory<byte> payload)
    {
        Bytes = bytes;
        Header = header;
        Payload = payload;
    }

    public ReadOnlyMemory<byte> Bytes { get; }

    public FrameHeader Header { get; }

    public ReadOnlyMemory<byte> Payload { get; }
}
