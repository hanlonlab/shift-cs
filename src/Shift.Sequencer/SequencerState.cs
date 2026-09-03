using System.Buffers;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;

namespace Shift.Sequencer;

public readonly struct VerifiedSubmission
{
    private VerifiedSubmission(ReadOnlyMemory<byte> frame, FrameHeader header)
    {
        Frame = frame;
        Header = header;
    }

    internal ReadOnlyMemory<byte> Frame { get; }

    internal FrameHeader Header { get; }

    internal ReadOnlySpan<byte> Payload => Frame.Span.Slice(
        FrameCodec.HeaderSize,
        Frame.Length - FrameCodec.MinimumFrameSize);

    public static VerifiedSubmission Verify(ReadOnlyMemory<byte> frame)
    {
        OperationStatus status = FrameCodec.TryDecode(frame.Span, out FrameHeader header, out _);
        if (status != OperationStatus.Done)
        {
            throw new InvalidDataException("Submission must contain exactly one valid encoded frame.");
        }

        if (header.SequenceId != 0)
        {
            throw new InvalidDataException("Submission sequence must be zero.");
        }

        if (header.ProducerId == FrameCodec.ControlProducerId)
        {
            throw new InvalidDataException("Submission producer ID cannot be zero.");
        }

        if (header.ProducerSequence == 0)
        {
            throw new InvalidDataException("Submission producer sequence cannot be zero.");
        }

        if (header.MessageType == MessageType.CommitThrough)
        {
            throw new InvalidDataException("CommitThrough is not a submission.");
        }

        return new VerifiedSubmission(frame, header);
    }
}

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

    private readonly Dictionary<ushort, ProducerState> _producers = [];
    private readonly Queue<byte[]> _pending = [];
    private int _pendingBytes;
    private bool _ending;
    private bool _faulted;
    private bool _sessionActive;
    private Guid _sessionId;
    private byte[] _sessionStartFrame = [];

    public long LastAcceptedSequence { get; private set; }

    public SubmissionResult Submit(VerifiedSubmission submission)
    {
        ThrowIfFaulted();

        if (submission.Frame.IsEmpty)
        {
            throw new ArgumentException("Submission must be verified.", nameof(submission));
        }

        FrameHeader header = submission.Header;
        ReadOnlySpan<byte> payload = submission.Payload;
        bool startsSession = header.MessageType == MessageType.StartNewSession;
        bool endsSession = header.MessageType == MessageType.EndCurrentSession;
        StartNewSession startCommand = default;
        if (startsSession)
        {
            if (!StartNewSessionCodec.TryDecode(payload, out startCommand)
                || startCommand.SessionId == Guid.Empty)
            {
                throw new InvalidDataException("StartNewSession requires a nonempty session ID.");
            }
        }

        bool opensSession = startsSession && !_sessionActive;
        if (opensSession && _sessionId != Guid.Empty && startCommand.SessionId == _sessionId)
        {
            return new SubmissionResult(
                SubmissionStatus.CommittedDuplicate,
                _sessionStartFrame,
                ForceCommit: false);
        }

        if (!opensSession)
        {
            SubmissionResult? duplicate = TryDuplicate(header, payload);
            if (duplicate is not null)
            {
                return duplicate.Value;
            }

            if (!_sessionActive)
            {
                throw new InvalidOperationException("A session is not active.");
            }

            if (startsSession)
            {
                throw new InvalidOperationException("A session is already active.");
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

        ValidateProducerSequence(header.ProducerId, header.ProducerSequence, opensSession);

        if (_pendingBytes > MaximumPendingBytes - submission.Frame.Length)
        {
            return new SubmissionResult(
                SubmissionStatus.BatchFull,
                ReadOnlyMemory<byte>.Empty,
                ForceCommit: false);
        }

        long sequenceId = opensSession ? 1 : checked(LastAcceptedSequence + 1);
        byte[] frame = new byte[submission.Frame.Length];
        FrameCodec.Encode(
            header.MessageType,
            header.ProducerId,
            header.ProducerSequence,
            sequenceId,
            payload,
            frame);

        ulong lastCommitted = 0;
        if (opensSession)
        {
            _producers.Clear();
            _sessionActive = true;
            _sessionId = startCommand.SessionId;
            _sessionStartFrame = frame;
        }
        else if (_producers.TryGetValue(header.ProducerId, out ProducerState? prior))
        {
            lastCommitted = prior.LastCommittedProducerSequence;
        }

        _producers[header.ProducerId] = new ProducerState
        {
            LastAcceptedProducerSequence = header.ProducerSequence,
            LastCommittedProducerSequence = lastCommitted,
            LastAcceptedFrame = frame,
            LastAcceptedCommitted = false,
        };
        _pending.Enqueue(frame);
        LastAcceptedSequence = sequenceId;
        _pendingBytes += frame.Length;

        if (endsSession)
        {
            _ending = true;
        }

        return new SubmissionResult(
            SubmissionStatus.Accepted,
            frame,
            ForceCommit: endsSession || _pendingBytes == MaximumPendingBytes);
    }

    public void CommitThrough(long sequenceId)
    {
        ThrowIfFaulted();

        if (_pending.Count == 0 || sequenceId != LastAcceptedSequence)
        {
            _faulted = true;
            throw new InvalidDataException("CommitThrough must match the current pending high-water sequence.");
        }

        while (_pending.TryDequeue(out byte[]? frame))
        {
            FrameCodec.TryDecode(frame, out FrameHeader header, out _);
            ProducerState producer = _producers[header.ProducerId];
            producer.LastCommittedProducerSequence = header.ProducerSequence;
            if (producer.LastAcceptedProducerSequence == header.ProducerSequence)
            {
                producer.LastAcceptedCommitted = true;
            }

            _pendingBytes -= frame.Length;
        }

        if (_ending)
        {
            _ending = false;
            _sessionActive = false;
        }
    }

    private void ValidateProducerSequence(ushort producerId, ulong producerSequence, bool opensSession)
    {
        if (!_producers.TryGetValue(producerId, out ProducerState? existing))
        {
            if (producerSequence != 1)
            {
                throw new InvalidDataException("A new producer must begin at producer sequence 1.");
            }

            return;
        }

        if (producerSequence == existing.LastAcceptedProducerSequence + 1)
        {
            return;
        }

        if (opensSession && producerSequence == 1)
        {
            return;
        }

        throw new InvalidDataException("Producer sequence must be contiguous.");
    }

    private SubmissionResult? TryDuplicate(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (!_producers.TryGetValue(header.ProducerId, out ProducerState? existing))
        {
            return null;
        }

        if (header.ProducerSequence == existing.LastAcceptedProducerSequence)
        {
            if (existing.LastAcceptedFrame.Length != 0)
            {
                FrameCodec.TryDecode(
                    existing.LastAcceptedFrame,
                    out FrameHeader existingHeader,
                    out ReadOnlySpan<byte> existingPayload);
                bool sameContent = header.MessageType == existingHeader.MessageType
                    && payload.SequenceEqual(existingPayload);
                if (!sameContent)
                {
                    _faulted = true;
                    throw new InvalidDataException("Producer sequence was reused with different content.");
                }
            }

            return existing.LastAcceptedCommitted
                ? new SubmissionResult(
                    SubmissionStatus.CommittedDuplicate,
                    existing.LastAcceptedFrame,
                    ForceCommit: false)
                : new SubmissionResult(
                    SubmissionStatus.PendingDuplicate,
                    ReadOnlyMemory<byte>.Empty,
                    ForceCommit: false);
        }

        if (header.ProducerSequence < existing.LastAcceptedProducerSequence)
        {
            return header.ProducerSequence <= existing.LastCommittedProducerSequence
                ? new SubmissionResult(
                    SubmissionStatus.CommittedDuplicate,
                    ReadOnlyMemory<byte>.Empty,
                    ForceCommit: false)
                : new SubmissionResult(
                    SubmissionStatus.PendingDuplicate,
                    ReadOnlyMemory<byte>.Empty,
                    ForceCommit: false);
        }

        return null;
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The sequencer cannot continue after a fatal protocol error.");
        }
    }

    private sealed class ProducerState
    {
        public ulong LastAcceptedProducerSequence { get; set; }

        public ulong LastCommittedProducerSequence { get; set; }

        public byte[] LastAcceptedFrame { get; set; } = [];

        public bool LastAcceptedCommitted { get; set; }
    }
}
