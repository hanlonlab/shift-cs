using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using Shift.Ipc;
using Shift.Protocol.Framing;

namespace Shift.Sequencer;

public sealed class SequencerServer(
    string proposalPath,
    string archiverPath,
    IPAddress multicastGroup,
    int multicastPort,
    IPAddress localInterface)
{
    private static readonly TimeSpan _maximumBatchDelay = TimeSpan.FromMilliseconds(1);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using UnixDatagramReceiver proposals = new(proposalPath);
        using UnixStreamSocket archiver = await UnixStreamSocket.ConnectAsync(
            archiverPath,
            cancellationToken);
        using UdpMulticastSender multicast = new(
            multicastGroup,
            multicastPort,
            localInterface);

        SequencerState state = new();
        List<ReadOnlyMemory<byte>> batch = [];
        byte[] receiveBuffer = new byte[UnixDatagramReceiver.MaximumDatagramSize];
        byte[]? committedThroughFrame = null;
        int batchBytes = 0;
        CancellationTokenSource? batchDeadline = null;

        try
        {
            while (true)
            {
                int frameLength;
                try
                {
                    frameLength = await proposals.ReceiveAsync(
                        receiveBuffer,
                        batchDeadline?.Token ?? cancellationToken);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && batch.Count != 0)
                {
                    await CommitCurrentBatchAsync();
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (batchDeadline?.IsCancellationRequested == true)
                {
                    await CommitCurrentBatchAsync();
                }

                SubmissionResult submission = state.Submit(receiveBuffer.AsSpan(0, frameLength));
                if (submission.Status == SubmissionStatus.BatchFull)
                {
                    await CommitCurrentBatchAsync();
                    submission = state.Submit(receiveBuffer.AsSpan(0, frameLength));
                    if (submission.Status == SubmissionStatus.BatchFull)
                    {
                        throw new InvalidDataException("A proposal cannot fit in an empty batch.");
                    }
                }

                if (submission.Status == SubmissionStatus.PendingDuplicate)
                {
                    continue;
                }

                if (submission.Status == SubmissionStatus.CommittedDuplicate)
                {
                    if (committedThroughFrame is null)
                    {
                        throw new InvalidDataException("A committed proposal has no durable watermark.");
                    }

                    await multicast.SendAsync(submission.Frame, cancellationToken);
                    await multicast.SendAsync(committedThroughFrame, cancellationToken);
                    continue;
                }

                batch.Add(submission.Frame);
                batchBytes += submission.Frame.Length;

                if (batch.Count == 1)
                {
                    batchDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    batchDeadline.CancelAfter(_maximumBatchDelay);
                }

                if (submission.ForceCommit
                    || batchBytes == SequencerState.MaximumPendingBytes)
                {
                    await CommitCurrentBatchAsync();
                }
            }
        }
        finally
        {
            batchDeadline?.Dispose();
        }

        async Task CommitCurrentBatchAsync()
        {
            committedThroughFrame = await CommitAsync(
                archiver,
                multicast,
                state,
                batch,
                batchBytes,
                cancellationToken);
            batch.Clear();
            batchBytes = 0;
            batchDeadline!.Dispose();
            batchDeadline = null;
        }
    }

    private static async Task<byte[]> CommitAsync(
        UnixStreamSocket archiver,
        UdpMulticastSender multicast,
        SequencerState state,
        List<ReadOnlyMemory<byte>> batch,
        int batchBytes,
        CancellationToken cancellationToken)
    {
        byte[] request = new byte[sizeof(uint) + batchBytes];
        BinaryPrimitives.WriteUInt32BigEndian(request, checked((uint)batch.Count));

        int offset = sizeof(uint);
        foreach (ReadOnlyMemory<byte> frame in batch)
        {
            frame.Span.CopyTo(request.AsSpan(offset));
            offset += frame.Length;
        }

        await archiver.SendExactlyAsync(request, cancellationToken);

        byte[] lengthPrefix = new byte[sizeof(uint)];
        await archiver.ReceiveExactlyAsync(lengthPrefix, cancellationToken);
        uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix);
        if (encodedLength is < FrameCodec.MinimumFrameSize
            or > UnixDatagramReceiver.MaximumDatagramSize)
        {
            throw new InvalidDataException("The Archiver returned an invalid acknowledgment length.");
        }

        byte[] acknowledgment = new byte[encodedLength];
        lengthPrefix.CopyTo(acknowledgment, 0);
        await archiver.ReceiveExactlyAsync(acknowledgment.AsMemory(sizeof(uint)), cancellationToken);

        OperationStatus status = FrameCodec.TryDecode(
            acknowledgment,
            out FrameHeader header,
            out ReadOnlySpan<byte> payload);
        long expectedSequence = state.LastAcceptedSequence;
        if (status != OperationStatus.Done
            || header.MessageType != MessageType.CommitThrough
            || header.MessageId != Guid.Empty
            || header.SequenceId != expectedSequence
            || !payload.IsEmpty)
        {
            throw new InvalidDataException("The Archiver returned an invalid durable watermark.");
        }

        state.CommitThrough(expectedSequence);
        foreach (ReadOnlyMemory<byte> frame in batch)
        {
            await multicast.SendAsync(frame, cancellationToken);
        }

        await multicast.SendAsync(acknowledgment, cancellationToken);
        return acknowledgment;
    }
}
