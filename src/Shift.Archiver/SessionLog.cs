using System.Buffers;
using System.Buffers.Binary;
using Shift.Protocol;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Shift.Protocol.Internal.Control;

namespace Shift.Archiver;

public readonly record struct SessionLogState(
    Guid SessionId,
    long CommittedThrough,
    bool Ended,
    RecoveredProducerWatermark[] Producers,
    bool HasCommittedData);

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
        : this(path, FileMode.CreateNew)
    {
    }

    private SessionLog(string path, FileMode mode)
    {
        FileAccess access = mode == FileMode.CreateNew ? FileAccess.Write : FileAccess.ReadWrite;
        _stream = new FileStream(path, mode, access, FileShare.Read);
        if (mode == FileMode.Open)
        {
            SessionLogState state = Repair(_stream);
            _committedThrough = state.CommittedThrough;
            _lastSequence = state.CommittedThrough;
        }
    }

    public long CommittedThrough => _committedThrough;

    public static SessionLog Open(string path)
    {
        return new SessionLog(path, FileMode.Open);
    }

    public static SessionLogState Repair(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        return Repair(stream);
    }

    public void Append(ReadOnlySpan<byte> frame)
    {
        ThrowIfFaulted();

        OperationStatus status = FrameCodec.TryDecode(frame, out FrameHeader header, out _);
        if (status != OperationStatus.Done)
        {
            throw new InvalidDataException("Frame must contain exactly one valid encoded frame.");
        }

        if (header.MessageType is MessageType.CommitThrough or MessageType.RecoveredSession)
        {
            throw new InvalidDataException("Control frames cannot be appended to the session log.");
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

    private static SessionLogState Repair(FileStream stream)
    {
        long lastCommitEnd = 0;
        long committedThrough = 0;
        long lastSequence = 0;
        Guid sessionId = Guid.Empty;
        bool ended = false;
        Dictionary<ushort, ulong> committedProducers = [];
        Dictionary<ushort, ulong> pendingProducers = [];
        bool pendingEnded = false;
        long position = 0;
        byte[] lengthBytes = new byte[sizeof(uint)];
        byte[] marker = new byte[CommitMarkerSize];

        while (position < stream.Length)
        {
            long remaining = stream.Length - position;
            if (remaining < sizeof(uint))
            {
                break;
            }

            stream.Position = position;
            stream.ReadExactly(lengthBytes);
            uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
            if (declaredLength == CommitMarkerSentinel)
            {
                if (remaining < CommitMarkerSize)
                {
                    break;
                }

                stream.Position = position;
                stream.ReadExactly(marker);
                uint encodedChecksum = BinaryPrimitives.ReadUInt32BigEndian(
                    marker.AsSpan(CommitMarkerSize - CommitMarkerChecksumSize, CommitMarkerChecksumSize));
                uint computedChecksum = Crc32C.Compute(marker.AsSpan(0, CommitMarkerSize - CommitMarkerChecksumSize));
                long markedSequence = BinaryPrimitives.ReadInt64BigEndian(marker.AsSpan(sizeof(uint), sizeof(long)));
                if (encodedChecksum != computedChecksum || markedSequence != lastSequence || lastSequence == 0)
                {
                    break;
                }

                foreach ((ushort producerId, ulong producerSequence) in pendingProducers)
                {
                    committedProducers[producerId] = producerSequence;
                }

                pendingProducers.Clear();
                committedThrough = markedSequence;
                lastCommitEnd = position + CommitMarkerSize;
                ended = pendingEnded;
                pendingEnded = false;
                position = lastCommitEnd;
                continue;
            }

            if (declaredLength < FrameCodec.MinimumFrameSize || remaining < declaredLength)
            {
                break;
            }

            byte[] frame = new byte[declaredLength];
            stream.Position = position;
            stream.ReadExactly(frame);
            OperationStatus status = FrameCodec.TryDecode(frame, out FrameHeader header, out ReadOnlySpan<byte> payload);
            if (status != OperationStatus.Done || header.SequenceId != lastSequence + 1)
            {
                break;
            }

            if (lastSequence == 0)
            {
                if (header.MessageType != MessageType.StartNewSession
                    || !StartNewSessionCodec.TryDecode(payload, out StartNewSession command)
                    || command.SessionId == Guid.Empty)
                {
                    break;
                }

                sessionId = command.SessionId;
            }

            pendingProducers[header.ProducerId] = header.ProducerSequence;
            pendingEnded = header.MessageType == MessageType.EndCurrentSession;
            lastSequence = header.SequenceId;
            position += declaredLength;
        }

        if (stream.Length != lastCommitEnd)
        {
            stream.SetLength(lastCommitEnd);
            stream.Flush(flushToDisk: true);
        }

        stream.Position = lastCommitEnd;
        RecoveredProducerWatermark[] producers = committedProducers
            .OrderBy(entry => entry.Key)
            .Select(entry => new RecoveredProducerWatermark(entry.Key, entry.Value))
            .ToArray();
        return new SessionLogState(
            sessionId,
            committedThrough,
            ended,
            producers,
            HasCommittedData: lastCommitEnd > 0);
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The session log cannot continue after an I/O failure.");
        }
    }
}
