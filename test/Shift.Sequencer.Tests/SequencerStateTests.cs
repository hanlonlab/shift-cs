using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Sequencer.Tests;

public class SequencerStateTests
{
    private const ushort FirstProducerId = 1;
    private const ushort SecondProducerId = 2;
    private static readonly Guid _firstSessionId = new("10213243-5465-7687-98a9-bacbdcedfe0f");

    [Fact]
    public void SequencesSessionMessagesStartingAtOne()
    {
        SequencerState state = new();

        SubmissionResult start = state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        SubmissionResult order = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            FirstProducerId,
            2,
            [0xde, 0xad]));

        Assert.Equal(SubmissionStatus.Accepted, start.Status);
        Assert.False(start.ForceCommit);
        Assert.Equal(1, DecodeSequence(start.Frame.Span));
        Assert.Equal(SubmissionStatus.Accepted, order.Status);
        Assert.False(order.ForceCommit);
        Assert.Equal(2, DecodeSequence(order.Frame.Span));
        Assert.Equal(2, state.LastAcceptedSequence);
    }

    [Fact]
    public void RejectsInvalidSubmissionFramesWithoutChangingState()
    {
        SequencerState state = new();
        byte[] sequencedFrame = EncodeSubmissionFrame(
            MessageType.StartNewSession,
            FirstProducerId,
            1,
            EncodeStartPayload(_firstSessionId),
            sequenceId: 1);
        byte[] commitThrough = EncodeSubmissionFrame(
            MessageType.CommitThrough,
            FirstProducerId,
            1,
            []);
        byte[] controlProducer = EncodeStartFrame(FrameCodec.ControlProducerId, 1, _firstSessionId);
        byte[] zeroProducerSequence = EncodeStartFrame(FirstProducerId, 0, _firstSessionId);
        byte[] corrupt = EncodeStartFrame(
            FirstProducerId,
            1,
            _firstSessionId);
        corrupt[FrameCodec.HeaderSize] ^= 0xff;

        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(sequencedFrame));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(commitThrough));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(controlProducer));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(zeroProducerSequence));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(corrupt));
        Assert.Throws<ArgumentException>(() => state.Submit(default));

        SubmissionResult accepted = state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        Assert.Equal(1, DecodeSequence(accepted.Frame.Span));
    }

    [Fact]
    public void StartRequiresNonemptySessionId()
    {
        SequencerState state = new();

        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeStart(FirstProducerId, 1, Guid.Empty)));
        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeSubmission(MessageType.StartNewSession, FirstProducerId, 1, [])));

        SubmissionResult accepted = state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        Assert.Equal(SubmissionStatus.Accepted, accepted.Status);
    }

    [Fact]
    public void EnforcesSessionLifecycle()
    {
        SequencerState state = new();
        VerifiedSubmission order = EncodeSubmission(MessageType.PlaceOrder, FirstProducerId, 1, []);
        VerifiedSubmission start = EncodeStart(FirstProducerId, 1, _firstSessionId);

        Assert.Throws<InvalidOperationException>(() => state.Submit(order));
        state.Submit(start);
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeStart(
                SecondProducerId,
                1,
                new Guid("30415263-7485-96a7-b8c9-daebfc0d1e2f"))));
    }

    [Fact]
    public void EndForcesCommitAndSequenceResetsAfterCommit()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.CommitThrough(1);

        SubmissionResult end = state.Submit(EncodeSubmission(
            MessageType.EndCurrentSession,
            FirstProducerId,
            2,
            []));

        Assert.True(end.ForceCommit);
        Assert.Equal(2, DecodeSequence(end.Frame.Span));
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeSubmission(
                MessageType.PlaceOrder,
                FirstProducerId,
                3,
                [])));
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeStart(
                FirstProducerId,
                3,
                new Guid("708192a3-b4c5-d6e7-f809-1a2b3c4d5e6f"))));

        state.CommitThrough(2);
        SubmissionResult nextStart = state.Submit(EncodeStart(
            FirstProducerId,
            3,
            new Guid("90a1b2c3-d4e5-f607-1829-3a4b5c6d7e8f")));

        Assert.Equal(1, DecodeSequence(nextStart.Frame.Span));
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
                FirstProducerId,
                2,
                [0x01])));

        SubmissionResult end = state.Submit(EncodeSubmission(
            MessageType.EndCurrentSession,
            FirstProducerId,
            2,
            []));
        Assert.True(end.ForceCommit);
    }

    [Fact]
    public void PendingDuplicateDoesNothing()
    {
        SequencerState state = new();
        VerifiedSubmission submission = EncodeStart(FirstProducerId, 1, _firstSessionId);

        SubmissionResult accepted = state.Submit(submission);
        SubmissionResult duplicate = state.Submit(submission);
        SubmissionResult next = state.Submit(EncodeSubmission(
            MessageType.NextSimulationStep,
            FirstProducerId,
            2,
            []));

        Assert.Equal(SubmissionStatus.Accepted, accepted.Status);
        Assert.Equal(SubmissionStatus.PendingDuplicate, duplicate.Status);
        Assert.True(duplicate.Frame.IsEmpty);
        Assert.False(duplicate.ForceCommit);
        Assert.Equal(2, DecodeSequence(next.Frame.Span));
    }

    [Fact]
    public void CommittedDuplicateReturnsStoredFrame()
    {
        SequencerState state = new();
        VerifiedSubmission submission = EncodeStart(FirstProducerId, 1, _firstSessionId);
        SubmissionResult accepted = state.Submit(submission);
        state.CommitThrough(1);

        SubmissionResult duplicate = state.Submit(submission);

        Assert.Equal(SubmissionStatus.CommittedDuplicate, duplicate.Status);
        Assert.Equal(accepted.Frame.ToArray(), duplicate.Frame.ToArray());
        Assert.False(duplicate.ForceCommit);
    }

    [Fact]
    public void OlderPendingDuplicateBecomesCommittedWithBatch()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.CommitThrough(1);
        VerifiedSubmission second = EncodeSubmission(
            MessageType.PlaceOrder,
            FirstProducerId,
            2,
            [0x01]);
        state.Submit(second);
        state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            FirstProducerId,
            3,
            [0x02]));

        Assert.Equal(SubmissionStatus.PendingDuplicate, state.Submit(second).Status);

        state.CommitThrough(3);

        SubmissionResult committed = state.Submit(second);
        Assert.Equal(SubmissionStatus.CommittedDuplicate, committed.Status);
        Assert.True(committed.Frame.IsEmpty);
    }

    [Fact]
    public void ConflictingDuplicateFaultsSequencer()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        VerifiedSubmission conflict = EncodeStart(
            FirstProducerId,
            1,
            new Guid("d0e1f203-1425-3647-5869-7a8b9c0d1e2f"));

        Assert.Throws<InvalidDataException>(() => state.Submit(conflict));
        Assert.Throws<InvalidOperationException>(() => state.Submit(
            EncodeSubmission(
                MessageType.PlaceOrder,
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
            SecondProducerId,
            1,
            [0x01]));
        SubmissionResult firstNext = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            FirstProducerId,
            2,
            [0x02]));

        Assert.Equal(2, DecodeSequence(second.Frame.Span));
        Assert.Equal(3, DecodeSequence(firstNext.Frame.Span));
        Assert.Equal(
            SubmissionStatus.PendingDuplicate,
            state.Submit(EncodeSubmission(MessageType.PlaceOrder, SecondProducerId, 1, [0x01])).Status);
    }

    [Fact]
    public void RejectsProducerSequenceGaps()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));

        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeSubmission(MessageType.PlaceOrder, FirstProducerId, 3, [])));
        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeSubmission(MessageType.PlaceOrder, SecondProducerId, 2, [])));
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
            new Guid("bcdef012-3456-789a-bcde-f0123456789a")));
        Assert.Equal(SubmissionStatus.Accepted, nextStart.Status);
        Assert.Equal(1, DecodeSequence(nextStart.Frame.Span));

        SubmissionResult nextOrder = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            FirstProducerId,
            2,
            []));

        Assert.Equal(SubmissionStatus.Accepted, nextOrder.Status);
        Assert.Equal(2, DecodeSequence(nextOrder.Frame.Span));
    }

    [Fact]
    public void PendingFramesAreLimitedToOneMebibyte()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(FirstProducerId, 1, _firstSessionId));
        state.CommitThrough(1);
        byte[] payload = new byte[2_048 - FrameCodec.MinimumFrameSize];
        VerifiedSubmission lastSubmission = default;
        SubmissionResult lastAccepted = default;

        for (int index = 0; index < SequencerState.MaximumPendingBytes / 2_048; index++)
        {
            ulong producerSequence = (ulong)(index + 2);
            lastSubmission = EncodeSubmission(MessageType.PlaceOrder, FirstProducerId, producerSequence, payload);
            lastAccepted = state.Submit(lastSubmission);
        }

        Assert.True(lastAccepted.ForceCommit);
        Assert.Equal(SubmissionStatus.PendingDuplicate, state.Submit(lastSubmission).Status);
        SubmissionResult full = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            FirstProducerId,
            (ulong)((SequencerState.MaximumPendingBytes / 2_048) + 2),
            []));
        Assert.Equal(SubmissionStatus.BatchFull, full.Status);

        state.CommitThrough(state.LastAcceptedSequence);
        SubmissionResult accepted = state.Submit(EncodeSubmission(
            MessageType.PlaceOrder,
            FirstProducerId,
            (ulong)((SequencerState.MaximumPendingBytes / 2_048) + 2),
            []));
        Assert.Equal(SubmissionStatus.Accepted, accepted.Status);
    }

    private static long DecodeSequence(ReadOnlySpan<byte> frame)
    {
        Assert.Equal(
            System.Buffers.OperationStatus.Done,
            FrameCodec.TryDecode(frame, out FrameHeader header, out _));
        return header.SequenceId;
    }

    private static VerifiedSubmission EncodeStart(ushort producerId, ulong producerSequence, Guid sessionId)
    {
        return EncodeSubmission(
            MessageType.StartNewSession,
            producerId,
            producerSequence,
            EncodeStartPayload(sessionId));
    }

    private static byte[] EncodeStartFrame(ushort producerId, ulong producerSequence, Guid sessionId)
    {
        return EncodeSubmissionFrame(
            MessageType.StartNewSession,
            producerId,
            producerSequence,
            EncodeStartPayload(sessionId));
    }

    private static byte[] EncodeStartPayload(Guid sessionId)
    {
        byte[] payload = new byte[16];
        StartNewSessionCodec.Encode(new StartNewSession(sessionId), payload);
        return payload;
    }

    private static VerifiedSubmission EncodeSubmission(
        MessageType messageType,
        ushort producerId,
        ulong producerSequence,
        byte[] payload,
        long sequenceId = 0)
    {
        return VerifiedSubmission.Verify(
            EncodeSubmissionFrame(messageType, producerId, producerSequence, payload, sequenceId));
    }

    private static byte[] EncodeSubmissionFrame(
        MessageType messageType,
        ushort producerId,
        ulong producerSequence,
        byte[] payload,
        long sequenceId = 0)
    {
        byte[] submission = new byte[FrameCodec.MinimumFrameSize + payload.Length];
        FrameCodec.Encode(messageType, producerId, producerSequence, sequenceId, payload, submission);
        return submission;
    }
}
