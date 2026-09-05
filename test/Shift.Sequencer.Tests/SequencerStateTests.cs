using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Sequencer.Tests;

public class SequencerStateTests
{
    private const ushort FirstProducerId = 1;
    private const ushort SecondProducerId = 2;
    private static readonly Guid _firstSessionId = new("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly Guid _secondSessionId = new("bcdef012-3456-789a-bcde-f0123456789a");

    [Fact]
    public void StartRejectsPayload()
    {
        SequencerState state = new();

        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeSubmission(
                MessageType.StartNewSession,
                _firstSessionId,
                FirstProducerId,
                1,
                [0x01])));

        SubmissionResult accepted = state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        Assert.Equal(SubmissionStatus.Accepted, accepted.Status);
    }

    [Fact]
    public void EnforcesSessionLifecycle()
    {
        SequencerState state = new();
        VerifiedSubmission order = EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            FirstProducerId,
            1,
            []);
        VerifiedSubmission start = EncodeStart(FirstProducerId, 1, _firstSessionId);

        Assert.Equal(SubmissionStatus.SessionMismatch, state.Submit(order).Status);
        state.Submit(start);
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeStart(
                SecondProducerId,
                1,
                _firstSessionId)));
    }

    [Fact]
    public void EndForcesCommitAndSequenceResetsAfterCommit()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.CommitThrough(1);

        SubmissionResult end = state.Submit(EncodeSubmission(
            MessageType.EndCurrentSession,
            _firstSessionId,
            FirstProducerId,
            2,
            []));

        Assert.True(end.ForceCommit);
        Assert.Equal(2, DecodeHeader(end.Frame.Bytes.Span).SequenceId);
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeSubmission(
                MessageType.PlaceOrder,
                _firstSessionId,
                FirstProducerId,
                3,
                [])));
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeStart(
                FirstProducerId,
                3,
                _firstSessionId)));

        state.CommitThrough(2);
        SubmissionResult nextStart = state.Submit(EncodeStart(
            FirstProducerId,
            3,
            new Guid("90a1b2c3-d4e5-f607-1829-3a4b5c6d7e8f")));

        Assert.Equal(1, DecodeHeader(nextStart.Frame.Bytes.Span).SequenceId);
        Assert.Equal(1, state.LastAcceptedSequence);
    }

    [Fact]
    public void EndRejectsPayload()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));

        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeSubmission(
                MessageType.EndCurrentSession,
                _firstSessionId,
                FirstProducerId,
                2,
                [0x01])));

        SubmissionResult end = state.Submit(EncodeSubmission(
            MessageType.EndCurrentSession,
            _firstSessionId,
            FirstProducerId,
            2,
            []));
        Assert.True(end.ForceCommit);
    }

    [Fact]
    public void OlderPendingDuplicateBecomesCommittedWithBatch()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.CommitThrough(1);
        VerifiedSubmission second = EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            FirstProducerId,
            2,
            [0x01]);
        state.Submit(second);
        state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            FirstProducerId,
            3,
            [0x02]));

        Assert.Equal(SubmissionStatus.PendingDuplicate, state.Submit(second).Status);

        state.CommitThrough(3);

        SubmissionResult committed = state.Submit(second);
        Assert.Equal(SubmissionStatus.CommittedDuplicate, committed.Status);
        Assert.True(committed.Frame.Bytes.IsEmpty);
    }

    [Fact]
    public void ConflictingDuplicateFaultsSequencer()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        VerifiedSubmission conflict = EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            FirstProducerId,
            1,
            []);

        Assert.Throws<InvalidDataException>(() => state.Submit(conflict));
        Assert.Throws<InvalidOperationException>(() => state.Submit(
            EncodeSubmission(
                MessageType.PlaceOrder,
                _firstSessionId,
                FirstProducerId,
                2,
                [])));
        Assert.Throws<InvalidOperationException>(() => state.CommitThrough(1));
    }

    [Fact]
    public void IndependentProducersDedupSeparately()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.CommitThrough(1);

        SubmissionResult second = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            SecondProducerId,
            1,
            [0x01]));
        Assert.Equal(2, DecodeHeader(second.Frame.Bytes.Span).SequenceId);
        Assert.Equal(
            SubmissionStatus.PendingDuplicate,
            state.Submit(EncodeSubmission(
                MessageType.PlaceOrder,
                _firstSessionId,
                SecondProducerId,
                1,
                [0x01])).Status);

        SubmissionResult firstNext = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            FirstProducerId,
            2,
            [0x02]));
        Assert.Equal(3, DecodeHeader(firstNext.Frame.Bytes.Span).SequenceId);
    }

    [Fact]
    public void RejectsProducerSequenceGaps()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));

        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeSubmission(
                MessageType.PlaceOrder,
                _firstSessionId,
                FirstProducerId,
                3,
                [])));
        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeSubmission(
                MessageType.PlaceOrder,
                _firstSessionId,
                SecondProducerId,
                2,
                [])));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void CommitThroughRequiresPendingHighWater(long committedThrough)
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            FirstProducerId,
            2,
            []));

        Assert.Throws<InvalidDataException>(() => state.CommitThrough(committedThrough));
        Assert.Throws<InvalidOperationException>(() => state.CommitThrough(2));
    }

    [Fact]
    public void DedupeStateClearsWhenNextSessionStarts()
    {
        SequencerState state = new();
        VerifiedSubmission firstStart = EncodeStart(FirstProducerId, 1, _firstSessionId);
        state.Submit(firstStart);
        state.CommitThrough(1);
        VerifiedSubmission endSubmission = EncodeSubmission(
            MessageType.EndCurrentSession,
            _firstSessionId,
            FirstProducerId,
            2,
            []);
        state.Submit(endSubmission);
        state.CommitThrough(2);

        SubmissionResult oldStartDuplicate = state.Submit(firstStart);
        SubmissionResult oldDuplicate = state.Submit(endSubmission);
        Assert.Equal(SubmissionStatus.CommittedDuplicate, oldStartDuplicate.Status);
        Assert.Equal(SubmissionStatus.CommittedDuplicate, oldDuplicate.Status);

        SubmissionResult nextStart = state.Submit(EncodeStart(
            FirstProducerId,
            1,
            _secondSessionId));
        Assert.Equal(SubmissionStatus.Accepted, nextStart.Status);
        Assert.Equal(1, DecodeHeader(nextStart.Frame.Bytes.Span).SequenceId);

        SubmissionResult nextOrder = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            _secondSessionId,
            FirstProducerId,
            2,
            []));

        Assert.Equal(SubmissionStatus.Accepted, nextOrder.Status);
        Assert.Equal(2, DecodeHeader(nextOrder.Frame.Bytes.Span).SequenceId);
    }

    [Fact]
    public void IgnoresStaleEndAfterNextSessionStarts()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.CommitThrough(1);
        VerifiedSubmission staleEnd = EncodeSubmission(
            MessageType.EndCurrentSession,
            _firstSessionId,
            FirstProducerId,
            2,
            []);
        state.Submit(staleEnd);
        state.CommitThrough(2);
        state.Submit(EncodeStart(FirstProducerId, 1, _secondSessionId));
        state.CommitThrough(1);

        SubmissionResult stale = state.Submit(staleEnd);

        Assert.Equal(SubmissionStatus.SessionMismatch, stale.Status);
        Assert.Equal(1, state.LastAcceptedSequence);

        SubmissionResult current = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            _secondSessionId,
            FirstProducerId,
            2,
            []));
        FrameHeader currentHeader = DecodeHeader(current.Frame.Bytes.Span);
        Assert.Equal(SubmissionStatus.Accepted, current.Status);
        Assert.Equal(_secondSessionId, currentHeader.SessionId);
        Assert.Equal(2, currentHeader.SequenceId);
    }

    [Fact]
    public void PendingFramesAreLimitedToOneMebibyte()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.CommitThrough(1);
        byte[] payload = new byte[FrameCodec.MaximumFrameSize - FrameCodec.MinimumFrameSize];
        VerifiedSubmission lastSubmission = default;
        SubmissionResult lastAccepted = default;

        for (int index = 0;
             index < SequencerState.MaximumPendingBytes / FrameCodec.MaximumFrameSize;
             index++)
        {
            ulong producerSequence = (ulong)(index + 2);
            lastSubmission = EncodeSubmission(
                MessageType.PlaceOrder,
                _firstSessionId,
                FirstProducerId,
                producerSequence,
                payload);
            lastAccepted = state.Submit(lastSubmission);
        }

        Assert.True(lastAccepted.ForceCommit);
        Assert.Equal(SubmissionStatus.PendingDuplicate, state.Submit(lastSubmission).Status);
        SubmissionResult full = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            FirstProducerId,
            (ulong)((SequencerState.MaximumPendingBytes / FrameCodec.MaximumFrameSize) + 2),
            []));
        Assert.Equal(SubmissionStatus.BatchFull, full.Status);

        state.CommitThrough(state.LastAcceptedSequence);
        SubmissionResult accepted = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            _firstSessionId,
            FirstProducerId,
            (ulong)((SequencerState.MaximumPendingBytes / FrameCodec.MaximumFrameSize) + 2),
            []));
        Assert.Equal(SubmissionStatus.Accepted, accepted.Status);
    }

    private static FrameHeader DecodeHeader(ReadOnlySpan<byte> frame)
    {
        Assert.Equal(
            System.Buffers.OperationStatus.Done,
            FrameCodec.TryDecode(frame, out FrameHeader header, out _));
        return header;
    }

    private static VerifiedSubmission EncodeStart(ushort producerId, ulong producerSequence, Guid sessionId)
    {
        return EncodeSubmission(
            MessageType.StartNewSession,
            sessionId,
            producerId,
            producerSequence,
            []);
    }

    private static VerifiedSubmission EncodeSubmission(
        MessageType messageType,
        Guid sessionId,
        ushort producerId,
        ulong producerSequence,
        byte[] payload)
    {
        return VerifiedSubmission.Verify(
            FrameCodec.Encode(messageType, sessionId, producerId, producerSequence, 0, payload).Bytes);
    }
}
