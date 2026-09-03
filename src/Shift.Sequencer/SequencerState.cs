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
    private int _pendingBytes;
    private bool _ending;
    private bool _faulted;
    private bool _sessionActive;
    private Guid _sessionId;
    private byte[] _sessionStartFrame = [];

    public long LastAcceptedSequence { get; private set; }

    internal int PendingBytes => _pendingBytes;

    public SubmissionResult Submit(VerifiedSubmission submission)
    {
        ThrowIfFaulted();

        if (submission.Frame.IsEmpty)
        {
            throw new ArgumentException("Submission must be verified.", nameof(submission));
        }

        FrameHeader header = submission.Header;
        ReadOnlySpan<byte> payload = submission.Payload;
        Guid sessionId = DecodeSessionId(header.MessageType, payload);
        bool opensSession = sessionId != Guid.Empty && !_sessionActive;
        if (opensSession && sessionId == _sessionId)
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

            ValidateSessionLifecycle(header.MessageType, payload);
        }

        ValidateProducerSequence(header.ProducerId, header.ProducerSequence, opensSession);

        if (submission.Frame.Length > MaximumPendingBytes - _pendingBytes)
        {
            return new SubmissionResult(
                SubmissionStatus.BatchFull,
                ReadOnlyMemory<byte>.Empty,
                ForceCommit: false);
        }

        return AcceptSubmission(
            submission,
            opensSession,
            sessionId);
    }

    private static Guid DecodeSessionId(
        MessageType messageType,
        ReadOnlySpan<byte> payload)
    {
        if (messageType != MessageType.StartNewSession)
        {
            return Guid.Empty;
        }

        if (!StartNewSessionCodec.TryDecode(payload, out StartNewSession command)
            || command.SessionId == Guid.Empty)
        {
            throw new InvalidDataException("StartNewSession requires a nonempty session ID.");
        }

        return command.SessionId;
    }

    private void ValidateSessionLifecycle(
        MessageType messageType,
        ReadOnlySpan<byte> payload)
    {
        if (!_sessionActive)
        {
            throw new InvalidOperationException("A session is not active.");
        }

        if (messageType == MessageType.StartNewSession)
        {
            throw new InvalidOperationException("A session is already active.");
        }

        if (_ending)
        {
            throw new InvalidOperationException("The current session is awaiting its final commit.");
        }

        if (messageType == MessageType.EndCurrentSession && !payload.IsEmpty)
        {
            throw new InvalidDataException("EndCurrentSession payload must be empty.");
        }
    }

    private SubmissionResult AcceptSubmission(
        VerifiedSubmission submission,
        bool opensSession,
        Guid sessionId)
    {
        FrameHeader header = submission.Header;
        ReadOnlySpan<byte> payload = submission.Payload;
        bool endsSession = header.MessageType == MessageType.EndCurrentSession;
        long sequenceId = opensSession ? 1 : checked(LastAcceptedSequence + 1);
        byte[] frame = new byte[submission.Frame.Length];
        FrameCodec.Encode(
            header.MessageType,
            header.ProducerId,
            header.ProducerSequence,
            sequenceId,
            payload,
            frame);

        if (opensSession)
        {
            _producers.Clear();
            _sessionActive = true;
            _sessionId = sessionId;
            _sessionStartFrame = frame;
        }

        if (!_producers.TryGetValue(header.ProducerId, out ProducerState? producer))
        {
            producer = new ProducerState();
            _producers.Add(header.ProducerId, producer);
        }

        producer.LastAcceptedProducerSequence = header.ProducerSequence;
        producer.LastAcceptedFrame = frame;
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

        if (_pendingBytes == 0 || sequenceId != LastAcceptedSequence)
        {
            _faulted = true;
            throw new InvalidDataException("CommitThrough must match the current pending high-water sequence.");
        }

        foreach (ProducerState producer in _producers.Values)
        {
            producer.LastCommittedProducerSequence = producer.LastAcceptedProducerSequence;
        }

        _pendingBytes = 0;

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

            return header.ProducerSequence <= existing.LastCommittedProducerSequence
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
    }
}
