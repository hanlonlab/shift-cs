using System.Buffers;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;

namespace Shift.Sequencer;

public enum SubmissionStatus
{
    Accepted,
    PendingDuplicate,
    CommittedDuplicate,
    BatchFull,
}

public readonly record struct SubmissionResult(
    SubmissionStatus Status,
    ReadOnlyMemory<byte> Frame,
    bool ForceCommit
);

public sealed class SequencerState
{
    public const int MaximumPendingBytes = 1024 * 1024;

    private readonly Dictionary<Guid, Message> _messages = [];
    private readonly Queue<Message> _pending = [];
    private long _lastAcceptedSequence;
    private int _pendingBytes;
    private bool _ending;
    private bool _faulted;
    private bool _sessionActive;

    public long LastAcceptedSequence => _lastAcceptedSequence;

    public SubmissionResult Submit(ReadOnlySpan<byte> proposal)
    {
        ThrowIfFaulted();

        OperationStatus status = FrameCodec.TryDecode(
            proposal,
            out FrameHeader header,
            out ReadOnlySpan<byte> payload);
        if (status != OperationStatus.Done)
        {
            throw new InvalidDataException("Proposal must contain exactly one valid encoded frame.");
        }

        if (header.SequenceId != 0)
        {
            throw new InvalidDataException("Proposal sequence must be zero.");
        }

        if (header.MessageId == Guid.Empty)
        {
            throw new InvalidDataException("Proposal message ID cannot be empty.");
        }

        if (header.MessageType == MessageType.CommitThrough)
        {
            throw new InvalidDataException("CommitThrough is not a proposal.");
        }

        if (_messages.TryGetValue(header.MessageId, out Message? existing))
        {
            FrameCodec.TryDecode(
                existing.Frame,
                out FrameHeader existingHeader,
                out ReadOnlySpan<byte> existingPayload);
            bool sameContent = header.MessageType == existingHeader.MessageType
                && payload.SequenceEqual(existingPayload);
            if (sameContent)
            {
                return existing.Committed
                    ? new SubmissionResult(SubmissionStatus.CommittedDuplicate, existing.Frame, ForceCommit: false)
                    : new SubmissionResult(
                        SubmissionStatus.PendingDuplicate,
                        ReadOnlyMemory<byte>.Empty,
                        ForceCommit: false);
            }

            if (header.MessageType != MessageType.StartNewSession || _sessionActive)
            {
                _faulted = true;
                throw new InvalidDataException("Message ID was reused with different content.");
            }
        }

        bool startsSession = header.MessageType == MessageType.StartNewSession;
        bool endsSession = header.MessageType == MessageType.EndCurrentSession;

        if (startsSession)
        {
            if (!StartNewSessionCodec.TryDecode(payload, out StartNewSession command)
                || command.SessionId == Guid.Empty)
            {
                throw new InvalidDataException("StartNewSession requires a nonempty session ID.");
            }

            if (_sessionActive)
            {
                throw new InvalidOperationException("A session is already active.");
            }
        }
        else
        {
            if (!_sessionActive)
            {
                throw new InvalidOperationException("A session is not active.");
            }

            if (_ending)
            {
                throw new InvalidOperationException("The current session is awaiting its final commit.");
            }

            if (endsSession && !payload.IsEmpty)
            {
                throw new InvalidDataException("EndCurrentSession payload must be empty.");
            }
        }

        if (_pendingBytes > MaximumPendingBytes - proposal.Length)
        {
            return new SubmissionResult(
                SubmissionStatus.BatchFull,
                ReadOnlyMemory<byte>.Empty,
                ForceCommit: false);
        }

        long sequenceId = startsSession ? 1 : checked(_lastAcceptedSequence + 1);
        byte[] frame = new byte[proposal.Length];
        FrameCodec.Encode(header.MessageType, header.MessageId, sequenceId, payload, frame);

        if (startsSession)
        {
            _messages.Clear();
            _sessionActive = true;
        }

        Message message = new(frame);
        _messages.Add(header.MessageId, message);
        _pending.Enqueue(message);
        _lastAcceptedSequence = sequenceId;
        _pendingBytes += frame.Length;

        if (endsSession)
        {
            _ending = true;
        }

        return new SubmissionResult(SubmissionStatus.Accepted, frame, ForceCommit: endsSession);
    }

    public void CommitThrough(long sequenceId)
    {
        ThrowIfFaulted();

        if (_pending.Count == 0 || sequenceId != _lastAcceptedSequence)
        {
            _faulted = true;
            throw new InvalidDataException("CommitThrough must match the current pending high-water sequence.");
        }

        while (_pending.TryDequeue(out Message? message))
        {
            message.Committed = true;
            _pendingBytes -= message.Frame.Length;
        }

        if (_ending)
        {
            _ending = false;
            _sessionActive = false;
        }
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The sequencer cannot continue after a fatal protocol error.");
        }
    }

    private sealed class Message(byte[] frame)
    {
        public byte[] Frame { get; } = frame;

        public bool Committed { get; set; }
    }
}
