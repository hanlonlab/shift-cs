using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;

namespace Shift.Sequencer;

public enum SubmissionStatus
{
    Accepted,
    PendingDuplicate,
    CommittedDuplicate,
    SessionMismatch,
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
    private ReadOnlyMemory<byte> _sessionStartFrame;

    public long LastAcceptedSequence { get; private set; }

    internal int PendingBytes => _pendingBytes;

    internal Guid SessionId => _sessionId;

    public SubmissionResult Submit(VerifiedSubmission submission)
    {
        ThrowIfFaulted();

        if (submission.Frame.Bytes.IsEmpty)
        {
            throw new ArgumentException("Submission must be verified.", nameof(submission));
        }

        FrameHeader header = submission.Frame.Header;
        ReadOnlySpan<byte> payload = submission.Frame.Payload.Span;
        bool opensSession = header.MessageType == MessageType.StartNewSession && !_sessionActive;
        if (!opensSession && header.SessionId != _sessionId)
        {
            return new SubmissionResult(
                SubmissionStatus.SessionMismatch,
                ReadOnlyMemory<byte>.Empty,
                ForceCommit: false);
        }

        if (header.MessageType == MessageType.StartNewSession
            && !StartNewSessionCodec.TryDecode(payload, out _))
        {
            throw new InvalidDataException("StartNewSession payload must be empty.");
        }

        if (opensSession && header.SessionId == _sessionId)
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

        if (submission.Frame.Bytes.Length > MaximumPendingBytes - _pendingBytes)
        {
            return new SubmissionResult(
                SubmissionStatus.BatchFull,
                ReadOnlyMemory<byte>.Empty,
                ForceCommit: false);
        }

        return AcceptSubmission(submission, opensSession);
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

        if (messageType == MessageType.EndCurrentSession
            && !EndCurrentSessionCodec.TryDecode(payload, out _))
        {
            throw new InvalidDataException("EndCurrentSession payload must be empty.");
        }
    }

    private SubmissionResult AcceptSubmission(
        VerifiedSubmission submission,
        bool opensSession)
    {
        FrameHeader header = submission.Frame.Header;
        ReadOnlySpan<byte> payload = submission.Frame.Payload.Span;
        bool endsSession = header.MessageType == MessageType.EndCurrentSession;
        long sequenceId = opensSession ? 1 : checked(LastAcceptedSequence + 1);
        CanonicalFrame frame = FrameCodec.Encode(
            header.MessageType,
            header.SessionId,
            header.ProducerId,
            header.ProducerSequence,
            sequenceId,
            payload);

        if (opensSession)
        {
            _producers.Clear();
            _sessionActive = true;
            _sessionId = header.SessionId;
            _sessionStartFrame = frame.Bytes;
        }

        if (!_producers.TryGetValue(header.ProducerId, out ProducerState? producer))
        {
            producer = new ProducerState();
            _producers.Add(header.ProducerId, producer);
        }

        producer.LastAcceptedProducerSequence = header.ProducerSequence;
        producer.LastAcceptedFrame = frame;
        LastAcceptedSequence = sequenceId;
        _pendingBytes += frame.Bytes.Length;

        if (endsSession)
        {
            _ending = true;
        }

        return new SubmissionResult(
            SubmissionStatus.Accepted,
            frame.Bytes,
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
            bool sameContent = header.MessageType == existing.LastAcceptedFrame.Header.MessageType
                && payload.SequenceEqual(existing.LastAcceptedFrame.Payload.Span);
            if (!sameContent)
            {
                _faulted = true;
                throw new InvalidDataException("Producer sequence was reused with different content.");
            }

            return header.ProducerSequence <= existing.LastCommittedProducerSequence
                ? new SubmissionResult(
                    SubmissionStatus.CommittedDuplicate,
                    existing.LastAcceptedFrame.Bytes,
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

        public CanonicalFrame LastAcceptedFrame { get; set; }
    }
}
