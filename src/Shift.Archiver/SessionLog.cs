using System.Buffers;
using System.Buffers.Binary;
using Shift.Protocol;
using Shift.Protocol.Framing;

namespace Shift.Archiver;

/// <summary>
/// Archives frame history in an inefficient manner, current rtt ~5ms from request -> flush
/// </summary>
public sealed class SessionLog : IDisposable
{
    private const uint CommitMarkerSentinel = 0;
    private const int CommitMarkerChecksumSize = sizeof(uint);
    private const int CommitMarkerSize = sizeof(uint) + sizeof(long) + CommitMarkerChecksumSize;

    private readonly FileStream _stream;
    private long _committedThrough;
    private long _lastSequence;
    private bool _faulted;

    public SessionLog(string path)
    {
        _stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
    }

    public void Append(ReadOnlySpan<byte> frame)
    {
        ThrowIfFaulted();

        OperationStatus status = FrameCodec.TryDecode(frame, out FrameHeader header, out _);
        if (status != OperationStatus.Done)
        {
            throw new InvalidDataException("Frame must contain exactly one valid encoded frame.");
        }

        if (header.MessageType == MessageType.CommitThrough)
        {
            throw new InvalidDataException("CommitThrough cannot be appended to the session log.");
        }

        long expectedSequence = _lastSequence + 1;
        if (header.SequenceId != expectedSequence)
        {
            throw new InvalidDataException(
                $"Expected sequence {expectedSequence}, received {header.SequenceId}.");
        }

        try
        {
            _stream.Write(frame);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _lastSequence = header.SequenceId;
    }

    public long Commit()
    {
        ThrowIfFaulted();

        if (_lastSequence == _committedThrough)
        {
            throw new InvalidOperationException("There are no pending frames to commit.");
        }

        Span<byte> marker = stackalloc byte[CommitMarkerSize];

        BinaryPrimitives.WriteUInt32BigEndian(marker, CommitMarkerSentinel);
        BinaryPrimitives.WriteInt64BigEndian(marker[sizeof(uint)..], _lastSequence);

        Span<byte> markerWithoutChecksum = marker[..^CommitMarkerChecksumSize];
        uint checksum = Crc32C.Compute(markerWithoutChecksum);
        BinaryPrimitives.WriteUInt32BigEndian(
            marker[^CommitMarkerChecksumSize..],
            checksum);

        try
        {
            _stream.Write(marker);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _committedThrough = _lastSequence;
        return _committedThrough;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The session log cannot continue after an I/O failure.");
        }
    }
}
