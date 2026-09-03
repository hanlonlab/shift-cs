using System.Buffers;
using System.Buffers.Binary;
using Shift.Ipc;
using Shift.Protocol.Framing;

namespace Shift.Sequencer;

public sealed class SequencerServer(
    UnixDatagramReceiver submissions,
    UnixStreamSocket archiver,
    UdpMulticastSender multicast)
{
    private static readonly TimeSpan _maximumBatchDelay = TimeSpan.FromMilliseconds(1);

    private readonly SequencerState _state = new();
    private readonly List<ReadOnlyMemory<byte>> _pendingBatch = [];
    private CancellationTokenSource? _batchDeadline;
    private readonly byte[] _receiveBuffer = new byte[UnixDatagramReceiver.MaximumDatagramSize];
    private byte[]? _committedThroughFrame;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                VerifiedSubmission submission = await ReceiveNextSubmissionAsync(cancellationToken);
                await HandleSubmissionAsync(submission, cancellationToken);
            }
        }
        finally
        {
            _batchDeadline?.Dispose();
        }
    }

    private async ValueTask<VerifiedSubmission> ReceiveNextSubmissionAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            int frameLength;
            try
            {
                frameLength = await submissions.ReceiveAsync(
                    _receiveBuffer,
                    _batchDeadline?.Token ?? cancellationToken);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && _pendingBatch.Count != 0)
            {
                await ArchiveAndPublishBatchAsync(cancellationToken);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_batchDeadline?.IsCancellationRequested == true)
            {
                await ArchiveAndPublishBatchAsync(cancellationToken);
            }

            return VerifiedSubmission.Verify(_receiveBuffer.AsMemory(0, frameLength));
        }
    }

    private async Task HandleSubmissionAsync(
        VerifiedSubmission verified,
        CancellationToken cancellationToken)
    {
        SubmissionResult submission = _state.Submit(verified);
        if (submission.Status == SubmissionStatus.BatchFull)
        {
            await ArchiveAndPublishBatchAsync(cancellationToken);
            submission = _state.Submit(verified);
        }

        switch (submission.Status)
        {
            case SubmissionStatus.PendingDuplicate:
                return;
            case SubmissionStatus.CommittedDuplicate:
                await PublishCommittedDuplicateAsync(submission.Frame, cancellationToken);
                return;
            case SubmissionStatus.BatchFull:
                throw new InvalidDataException("A submission cannot fit in an empty batch.");
            case SubmissionStatus.Accepted:
                AddAcceptedSubmissionToBatch(submission.Frame, cancellationToken);
                break;
            default:
                throw new InvalidDataException("The Sequencer returned an invalid submission status.");
        }

        if (submission.ForceCommit)
        {
            await ArchiveAndPublishBatchAsync(cancellationToken);
        }
    }

    private async Task PublishCommittedDuplicateAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken)
    {
        if (_committedThroughFrame is null)
        {
            throw new InvalidDataException("A committed submission has no durable watermark.");
        }

        if (!frame.IsEmpty)
        {
            await multicast.SendAsync(frame, cancellationToken);
        }

        await multicast.SendAsync(_committedThroughFrame, cancellationToken);
    }

    private void AddAcceptedSubmissionToBatch(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken)
    {
        _pendingBatch.Add(frame);

        if (_pendingBatch.Count == 1)
        {
            _batchDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _batchDeadline.CancelAfter(_maximumBatchDelay);
        }
    }

    private async Task ArchiveAndPublishBatchAsync(CancellationToken cancellationToken)
    {
        long expectedSequence = _state.LastAcceptedSequence;
        await archiver.SendExactlyAsync(EncodePendingBatch(), cancellationToken);
        byte[] durableWatermark = await ReceiveDurableWatermarkAsync(
            expectedSequence,
            cancellationToken);

        _state.CommitThrough(expectedSequence);
        foreach (ReadOnlyMemory<byte> frame in _pendingBatch)
        {
            await multicast.SendAsync(frame, cancellationToken);
        }

        await multicast.SendAsync(durableWatermark, cancellationToken);
        _committedThroughFrame = durableWatermark;
        _pendingBatch.Clear();
        _batchDeadline!.Dispose();
        _batchDeadline = null;
    }

    private byte[] EncodePendingBatch()
    {
        byte[] request = new byte[sizeof(uint) + _state.PendingBytes];
        BinaryPrimitives.WriteUInt32BigEndian(request, checked((uint)_pendingBatch.Count));

        int offset = sizeof(uint);
        foreach (ReadOnlyMemory<byte> frame in _pendingBatch)
        {
            frame.Span.CopyTo(request.AsSpan(offset));
            offset += frame.Length;
        }

        return request;
    }

    private async Task<byte[]> ReceiveDurableWatermarkAsync(
        long expectedSequence,
        CancellationToken cancellationToken)
    {
        byte[] durableWatermark = new byte[FrameCodec.MinimumFrameSize];
        await archiver.ReceiveExactlyAsync(
            durableWatermark.AsMemory(0, sizeof(uint)),
            cancellationToken);
        if (BinaryPrimitives.ReadUInt32BigEndian(durableWatermark) != FrameCodec.MinimumFrameSize)
        {
            throw new InvalidDataException("The Archiver returned an invalid durable watermark length.");
        }

        await archiver.ReceiveExactlyAsync(durableWatermark.AsMemory(sizeof(uint)), cancellationToken);

        OperationStatus status = FrameCodec.TryDecode(
            durableWatermark,
            out FrameHeader header,
            out _);
        if (status != OperationStatus.Done
            || header.MessageType != MessageType.CommitThrough
            || header.ProducerId != FrameCodec.ControlProducerId
            || header.ProducerSequence != 0
            || header.SequenceId != expectedSequence)
        {
            throw new InvalidDataException("The Archiver returned an invalid durable watermark.");
        }

        return durableWatermark;
    }
}
