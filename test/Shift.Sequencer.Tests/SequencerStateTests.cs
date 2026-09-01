using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Sequencer.Tests;

public class SequencerStateTests
{
    private static readonly Guid _firstMessageId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid _firstSessionId = new("10213243-5465-7687-98a9-bacbdcedfe0f");

    [Fact]
    public void SequencesSessionMessagesStartingAtOne()
    {
        SequencerState state = new();

        SubmissionResult start = state.Submit(EncodeStart(_firstMessageId, _firstSessionId));
        SubmissionResult order = state.Submit(EncodeProposal(
            MessageType.PlaceOrder,
            new Guid("11223344-5566-7788-99aa-bbccddeeff00"),
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
    public void RejectsInvalidProposalFramesWithoutChangingState()
    {
        SequencerState state = new();
        byte[] sequencedFrame = EncodeProposal(
            MessageType.StartNewSession,
            _firstMessageId,
            EncodeStartPayload(_firstSessionId),
            sequenceId: 1);
        byte[] commitThrough = EncodeProposal(MessageType.CommitThrough, _firstMessageId, []);
        byte[] emptyMessageId = EncodeStart(Guid.Empty, _firstSessionId);
        byte[] corrupt = EncodeStart(
            new Guid("01020304-0506-0708-0910-111213141516"),
            _firstSessionId);
        corrupt[FrameCodec.HeaderSize] ^= 0xff;

        Assert.Throws<InvalidDataException>(() => state.Submit(sequencedFrame));
        Assert.Throws<InvalidDataException>(() => state.Submit(commitThrough));
        Assert.Throws<InvalidDataException>(() => state.Submit(emptyMessageId));
        Assert.Throws<InvalidDataException>(() => state.Submit(corrupt));

        SubmissionResult accepted = state.Submit(EncodeStart(_firstMessageId, _firstSessionId));
        Assert.Equal(1, DecodeSequence(accepted.Frame.Span));
    }

    [Fact]
    public void StartRequiresNonemptySessionId()
    {
        SequencerState state = new();

        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeStart(_firstMessageId, Guid.Empty)));
        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeProposal(MessageType.StartNewSession, _firstMessageId, [])));

        SubmissionResult accepted = state.Submit(EncodeStart(_firstMessageId, _firstSessionId));
        Assert.Equal(SubmissionStatus.Accepted, accepted.Status);
    }

    [Fact]
    public void EnforcesSessionLifecycle()
    {
        SequencerState state = new();
        byte[] order = EncodeProposal(MessageType.PlaceOrder, _firstMessageId, []);
        byte[] start = EncodeStart(_firstMessageId, _firstSessionId);

        Assert.Throws<InvalidOperationException>(() => state.Submit(order));
        state.Submit(start);
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeStart(
                new Guid("20314253-6475-8697-a8b9-cadbecfd0e1f"),
                new Guid("30415263-7485-96a7-b8c9-daebfc0d1e2f"))));
    }

    [Fact]
    public void EndForcesCommitAndSequenceResetsAfterCommit()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(_firstMessageId, _firstSessionId));
        state.CommitThrough(1);

        SubmissionResult end = state.Submit(EncodeProposal(
            MessageType.EndCurrentSession,
            new Guid("40516273-8495-a6b7-c8d9-eafb0c1d2e3f"),
            []));

        Assert.True(end.ForceCommit);
        Assert.Equal(2, DecodeSequence(end.Frame.Span));
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeProposal(
                MessageType.PlaceOrder,
                new Guid("50617283-94a5-b6c7-d8e9-fa0b1c2d3e4f"),
                [])));
        Assert.Throws<InvalidOperationException>(() =>
            state.Submit(EncodeStart(
                new Guid("60718293-a4b5-c6d7-e8f9-0a1b2c3d4e5f"),
                new Guid("708192a3-b4c5-d6e7-f809-1a2b3c4d5e6f"))));

        state.CommitThrough(2);
        SubmissionResult nextStart = state.Submit(EncodeStart(
            new Guid("8091a2b3-c4d5-e6f7-0819-2a3b4c5d6e7f"),
            new Guid("90a1b2c3-d4e5-f607-1829-3a4b5c6d7e8f")));

        Assert.Equal(1, DecodeSequence(nextStart.Frame.Span));
        Assert.Equal(1, state.LastAcceptedSequence);
    }

    [Fact]
    public void EndRejectsPayload()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(_firstMessageId, _firstSessionId));

        Assert.Throws<InvalidDataException>(() =>
            state.Submit(EncodeProposal(
                MessageType.EndCurrentSession,
                new Guid("a0b1c2d3-e4f5-0617-2839-4a5b6c7d8e9f"),
                [0x01])));

        SubmissionResult end = state.Submit(EncodeProposal(
            MessageType.EndCurrentSession,
            new Guid("b0c1d2e3-f405-1627-3849-5a6b7c8d9e0f"),
            []));
        Assert.True(end.ForceCommit);
    }

    [Fact]
    public void PendingDuplicateDoesNothing()
    {
        SequencerState state = new();
        byte[] proposal = EncodeStart(_firstMessageId, _firstSessionId);

        SubmissionResult accepted = state.Submit(proposal);
        SubmissionResult duplicate = state.Submit(proposal);
        SubmissionResult next = state.Submit(EncodeProposal(
            MessageType.NextSimulationStep,
            new Guid("c0d1e2f3-0415-2637-4859-6a7b8c9d0e1f"),
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
        byte[] proposal = EncodeStart(_firstMessageId, _firstSessionId);
        SubmissionResult accepted = state.Submit(proposal);
        state.CommitThrough(1);

        SubmissionResult duplicate = state.Submit(proposal);

        Assert.Equal(SubmissionStatus.CommittedDuplicate, duplicate.Status);
        Assert.Equal(accepted.Frame.ToArray(), duplicate.Frame.ToArray());
        Assert.False(duplicate.ForceCommit);
    }

    [Fact]
    public void ConflictingDuplicateFaultsSequencer()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(_firstMessageId, _firstSessionId));
        byte[] conflict = EncodeStart(
            _firstMessageId,
            new Guid("d0e1f203-1425-3647-5869-7a8b9c0d1e2f"));

        Assert.Throws<InvalidDataException>(() => state.Submit(conflict));
        Assert.Throws<InvalidOperationException>(() => state.Submit(
            EncodeProposal(
                MessageType.PlaceOrder,
                new Guid("e0f10213-2435-4657-6879-8a9b0c1d2e3f"),
                [])));
        Assert.Throws<InvalidOperationException>(() => state.CommitThrough(1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void CommitThroughRequiresPendingHighWater(long committedThrough)
    {
        SequencerState state = new();
        state.Submit(EncodeStart(_firstMessageId, _firstSessionId));
        state.Submit(EncodeProposal(
            MessageType.PlaceOrder,
            new Guid("f0011223-3445-5667-7889-9aabbccddeef"),
            []));

        Assert.Throws<InvalidDataException>(() => state.CommitThrough(committedThrough));
        Assert.Throws<InvalidOperationException>(() => state.CommitThrough(2));
    }

    [Fact]
    public void DedupeStateClearsWhenNextSessionStarts()
    {
        SequencerState state = new();
        byte[] firstStart = EncodeStart(_firstMessageId, _firstSessionId);
        state.Submit(firstStart);
        state.CommitThrough(1);
        Guid endMessageId = new("fedcba98-7654-3210-fedc-ba9876543210");
        byte[] endProposal = EncodeProposal(MessageType.EndCurrentSession, endMessageId, []);
        state.Submit(endProposal);
        state.CommitThrough(2);

        SubmissionResult oldStartDuplicate = state.Submit(firstStart);
        SubmissionResult oldDuplicate = state.Submit(endProposal);
        Assert.Equal(SubmissionStatus.CommittedDuplicate, oldStartDuplicate.Status);
        Assert.Equal(SubmissionStatus.CommittedDuplicate, oldDuplicate.Status);

        SubmissionResult nextStart = state.Submit(EncodeStart(
            _firstMessageId,
            new Guid("bcdef012-3456-789a-bcde-f0123456789a")));
        Assert.Equal(SubmissionStatus.Accepted, nextStart.Status);
        Assert.Equal(1, DecodeSequence(nextStart.Frame.Span));

        SubmissionResult reusedId = state.Submit(EncodeProposal(
            MessageType.PlaceOrder,
            endMessageId,
            []));

        Assert.Equal(SubmissionStatus.Accepted, reusedId.Status);
        Assert.Equal(2, DecodeSequence(reusedId.Frame.Span));
    }

    [Fact]
    public void PendingFramesAreLimitedToOneMebibyte()
    {
        SequencerState state = new();
        state.Submit(EncodeStart(_firstMessageId, _firstSessionId));
        state.CommitThrough(1);
        byte[] payload = new byte[2_048 - FrameCodec.MinimumFrameSize];
        byte[] lastProposal = [];

        for (int index = 0; index < SequencerState.MaximumPendingBytes / 2_048; index++)
        {
            Guid messageId = new(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
            lastProposal = EncodeProposal(MessageType.PlaceOrder, messageId, payload);
            state.Submit(lastProposal);
        }

        Assert.Equal(SubmissionStatus.PendingDuplicate, state.Submit(lastProposal).Status);
        SubmissionResult full = state.Submit(EncodeProposal(
            MessageType.PlaceOrder,
            new Guid("01234567-89ab-cdef-0123-456789abcdef"),
            []));
        Assert.Equal(SubmissionStatus.BatchFull, full.Status);

        state.CommitThrough(state.LastAcceptedSequence);
        SubmissionResult accepted = state.Submit(EncodeProposal(
            MessageType.PlaceOrder,
            new Guid("12345678-9abc-def0-1234-56789abcdef0"),
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

    private static byte[] EncodeStart(Guid messageId, Guid sessionId)
    {
        return EncodeProposal(
            MessageType.StartNewSession,
            messageId,
            EncodeStartPayload(sessionId));
    }

    private static byte[] EncodeStartPayload(Guid sessionId)
    {
        byte[] payload = new byte[16];
        StartNewSessionCodec.Encode(new StartNewSession(sessionId), payload);
        return payload;
    }

    private static byte[] EncodeProposal(
        MessageType messageType,
        Guid messageId,
        byte[] payload,
        long sequenceId = 0)
    {
        byte[] proposal = new byte[FrameCodec.MinimumFrameSize + payload.Length];
        FrameCodec.Encode(messageType, messageId, sequenceId, payload, proposal);
        return proposal;
    }
}
