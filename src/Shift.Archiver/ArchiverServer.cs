using System.Buffers;
using System.Buffers.Binary;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Shift.Protocol.Internal.Control;

namespace Shift.Archiver;

public sealed class ArchiverServer(string archiveRoot) : IDisposable
{
    private const int MaximumBatchBytes = 1024 * 1024;
    private const int MaximumBatchFrameCount = MaximumBatchBytes / FrameCodec.MinimumFrameSize;

    private readonly string _archiveRoot = archiveRoot;
    private SessionLog? _sessionLog;
    private long _lastSequence;
    private RecoveredSession _handshake = new(
        Guid.Empty,
        0,
        RecoveredSessionStatus.Idle,
        []);

    public async Task RunAsync(
        UnixStreamSocket sequencer,
        CancellationToken cancellationToken = default)
    {
        RecoverFromDisk();
        await SendHandshakeAsync(sequencer, cancellationToken);
        byte[] prefix = new byte[sizeof(uint)];

        while (true)
        {
            await sequencer.ReceiveExactlyAsync(prefix, cancellationToken);
            uint frameCount = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (frameCount is 0 or > MaximumBatchFrameCount)
            {
                throw new InvalidDataException(
                    $"A batch must contain between 1 and {MaximumBatchFrameCount} frames.");
            }

            List<byte[]> frames = new((int)frameCount);
            int batchBytes = 0;
            for (uint index = 0; index < frameCount; index++)
            {
                await sequencer.ReceiveExactlyAsync(prefix, cancellationToken);
                uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
                if (frameLength is < FrameCodec.MinimumFrameSize
                    or > UnixDatagramReceiver.MaximumDatagramSize)
                {
                    throw new InvalidDataException(
                        $"Frame length must be between {FrameCodec.MinimumFrameSize} and {UnixDatagramReceiver.MaximumDatagramSize} bytes.");
                }

                int length = (int)frameLength;
                if (length > MaximumBatchBytes - batchBytes)
                {
                    throw new InvalidDataException(
                        $"A batch cannot exceed {MaximumBatchBytes} canonical frame bytes.");
                }

                byte[] frame = new byte[length];
                prefix.CopyTo(frame, 0);
                await sequencer.ReceiveExactlyAsync(frame.AsMemory(sizeof(uint)), cancellationToken);
                frames.Add(frame);
                batchBytes += length;
            }

            bool sessionOpen = _sessionLog is not null;
            Guid sessionId = Guid.Empty;
            bool endsSession = false;
            long batchFirstSequence = 0;
            long batchLastSequence = 0;

            for (int index = 0; index < frames.Count; index++)
            {
                byte[] frame = frames[index];
                OperationStatus status = FrameCodec.TryDecode(
                    frame,
                    out FrameHeader header,
                    out ReadOnlySpan<byte> payload);
                if (status != OperationStatus.Done)
                {
                    throw new InvalidDataException("Batch contains an invalid frame.");
                }

                if (header.ProducerId == FrameCodec.ControlProducerId || header.ProducerSequence == 0)
                {
                    throw new InvalidDataException("Candidate producer identity must not be zero.");
                }

                if (header.MessageType is MessageType.CommitThrough or MessageType.RecoveredSession)
                {
                    throw new InvalidDataException("Control frames are not candidate frames.");
                }

                if (index == 0)
                {
                    batchFirstSequence = header.SequenceId;
                }
                else if (header.SequenceId != batchFirstSequence + index)
                {
                    throw new InvalidDataException("Batch sequences must be contiguous.");
                }

                if (header.MessageType == MessageType.StartNewSession)
                {
                    if (index != 0
                        || !StartNewSessionCodec.TryDecode(payload, out StartNewSession command)
                        || command.SessionId == Guid.Empty)
                    {
                        throw new InvalidDataException(
                            "StartNewSession must be first in its batch and carry a nonempty session ID.");
                    }

                    sessionId = command.SessionId;
                }

                if (header.MessageType == MessageType.EndCurrentSession)
                {
                    if (!payload.IsEmpty)
                    {
                        throw new InvalidDataException("EndCurrentSession payload must be empty.");
                    }

                    if (index != frames.Count - 1)
                    {
                        throw new InvalidDataException("EndCurrentSession must be the final frame in its batch.");
                    }

                    endsSession = true;
                }

                batchLastSequence = header.SequenceId;
            }

            bool duplicateBatch = sessionOpen
                && batchLastSequence == _lastSequence
                && batchFirstSequence > 0
                && batchFirstSequence <= _lastSequence;
            if (duplicateBatch)
            {
                await SendAcknowledgementAsync(sequencer, _lastSequence, cancellationToken);
                continue;
            }

            if (sessionOpen)
            {
                if (batchFirstSequence != _lastSequence + 1)
                {
                    throw new InvalidDataException(
                        $"Expected sequence {_lastSequence + 1}, received {batchFirstSequence}.");
                }

                if (sessionId != Guid.Empty)
                {
                    throw new InvalidDataException("A session is already active.");
                }
            }
            else if (sessionId == Guid.Empty || batchFirstSequence != 1)
            {
                throw new InvalidDataException(
                    "An inactive Archiver batch must begin with StartNewSession at sequence 1.");
            }

            if (_sessionLog is null)
            {
                string path = Path.Combine(_archiveRoot, $"{sessionId:N}.shiftlog");
                _sessionLog = new SessionLog(path);
            }

            foreach (byte[] frame in frames)
            {
                _sessionLog.Append(frame);
            }

            long committedThrough = _sessionLog.Commit();
            _lastSequence = committedThrough;

            if (endsSession)
            {
                _sessionLog.Dispose();
                _sessionLog = null;
                _lastSequence = 0;
            }

            await SendAcknowledgementAsync(sequencer, committedThrough, cancellationToken);
        }
    }

    public void Dispose()
    {
        _sessionLog?.Dispose();
    }

    private void RecoverFromDisk()
    {
        SessionLogState? open = null;
        string? openPath = null;
        SessionLogState? ended = null;
        DateTime endedWriteTime = DateTime.MinValue;

        foreach (string path in Directory.GetFiles(_archiveRoot, "*.shiftlog"))
        {
            SessionLogState state = SessionLog.Repair(path);
            if (!state.HasCommittedData)
            {
                File.Delete(path);
                continue;
            }

            if (!state.Ended)
            {
                if (open is not null)
                {
                    throw new InvalidDataException("Multiple open session logs exist.");
                }

                open = state;
                openPath = path;
                continue;
            }

            DateTime writeTime = File.GetLastWriteTimeUtc(path);
            if (ended is null || writeTime >= endedWriteTime)
            {
                ended = state;
                endedWriteTime = writeTime;
            }
        }

        if (open is not null)
        {
            _sessionLog = SessionLog.Open(openPath!);
            _lastSequence = open.Value.CommittedThrough;
            _handshake = new RecoveredSession(
                open.Value.SessionId,
                open.Value.CommittedThrough,
                RecoveredSessionStatus.Active,
                open.Value.Producers);
            return;
        }

        if (ended is not null)
        {
            _handshake = new RecoveredSession(
                ended.Value.SessionId,
                ended.Value.CommittedThrough,
                RecoveredSessionStatus.Ended,
                ended.Value.Producers);
        }
    }

    private async Task SendHandshakeAsync(UnixStreamSocket sequencer, CancellationToken cancellationToken)
    {
        RecoveredProducerWatermark[] producers = _handshake.Producers ?? [];
        int payloadLength = RecoveredSessionCodec.GetEncodedLength(producers.Length);
        byte[] payload = new byte[payloadLength];
        RecoveredSessionCodec.Encode(_handshake with { Producers = producers }, payload);
        byte[] frame = new byte[FrameCodec.MinimumFrameSize + payloadLength];
        FrameCodec.Encode(
            MessageType.RecoveredSession,
            FrameCodec.ControlProducerId,
            0,
            0,
            payload,
            frame);
        await sequencer.SendExactlyAsync(frame, cancellationToken);
    }

    private static async Task SendAcknowledgementAsync(
        UnixStreamSocket sequencer,
        long committedThrough,
        CancellationToken cancellationToken)
    {
        byte[] acknowledgement = new byte[FrameCodec.MinimumFrameSize];
        FrameCodec.Encode(
            MessageType.CommitThrough,
            FrameCodec.ControlProducerId,
            0,
            committedThrough,
            ReadOnlySpan<byte>.Empty,
            acknowledgement);
        await sequencer.SendExactlyAsync(acknowledgement, cancellationToken);
    }
}
