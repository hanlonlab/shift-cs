using System.Buffers;
using System.Buffers.Binary;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;

namespace Shift.Archiver;

public sealed class ArchiverServer(string archiveRoot) : IDisposable
{
    private const int MaximumBatchBytes = 1024 * 1024;
    private const int MaximumBatchFrameCount = MaximumBatchBytes / FrameCodec.MinimumFrameSize;

    private readonly string _archiveRoot = archiveRoot;
    private SessionLog? _sessionLog;
    private long _lastSequence;

    public async Task RunAsync(
        UnixStreamSocket sequencer,
        CancellationToken cancellationToken = default)
    {
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

            bool sessionActive = _sessionLog is not null;
            long highWater = _lastSequence;
            Guid sessionId = Guid.Empty;
            bool endsSession = false;

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

                if (header.MessageId == Guid.Empty)
                {
                    throw new InvalidDataException("Candidate message ID must not be empty.");
                }

                if (header.MessageType == MessageType.CommitThrough)
                {
                    throw new InvalidDataException("CommitThrough is not a candidate frame.");
                }

                long expectedSequence = checked(highWater + 1);
                if (header.SequenceId != expectedSequence)
                {
                    throw new InvalidDataException(
                        $"Expected sequence {expectedSequence}, received {header.SequenceId}.");
                }

                if (header.MessageType == MessageType.StartNewSession)
                {
                    if (sessionActive)
                    {
                        throw new InvalidDataException("A session is already active.");
                    }

                    if (index != 0
                        || !StartNewSessionCodec.TryDecode(payload, out StartNewSession command)
                        || command.SessionId == Guid.Empty)
                    {
                        throw new InvalidDataException(
                            "An inactive Archiver batch must begin with a valid StartNewSession.");
                    }

                    sessionId = command.SessionId;
                    sessionActive = true;
                }
                else if (!sessionActive)
                {
                    throw new InvalidDataException(
                        "An inactive Archiver batch must begin with StartNewSession.");
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

                highWater = header.SequenceId;
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

            byte[] acknowledgement = new byte[FrameCodec.MinimumFrameSize];
            FrameCodec.Encode(
                MessageType.CommitThrough,
                Guid.Empty,
                committedThrough,
                ReadOnlySpan<byte>.Empty,
                acknowledgement);
            await sequencer.SendExactlyAsync(acknowledgement, cancellationToken);
        }
    }

    public void Dispose()
    {
        _sessionLog?.Dispose();
    }
}
