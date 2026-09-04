using System.Runtime.InteropServices;
using Shift.Archiver;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Control;

namespace Shift.Sequencer;

public sealed class SequencerServer(
    UnixDatagramReceiver submissions,
    SessionArchive archiver,
    UdpMulticastSender multicast)
{
    private static readonly TimeSpan _maximumBatchDelay = TimeSpan.FromMilliseconds(1);

    private readonly SequencerState _state = new();
    private readonly List<CanonicalFrame> _pendingBatch = [];
    private CancellationTokenSource? _batchDeadline;
    private readonly byte[] _receiveBuffer = new byte[UnixDatagramReceiver.MaximumDatagramSize];
    private ReadOnlyMemory<byte> _committedThroughFrame;

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
            case SubmissionStatus.SessionMismatch:
            case SubmissionStatus.PendingDuplicate:
                return;
            case SubmissionStatus.CommittedDuplicate:
                await PublishCommittedDuplicateAsync(submission.Frame.Bytes, cancellationToken);
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
        if (_committedThroughFrame.IsEmpty)
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
        CanonicalFrame frame,
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
        cancellationToken.ThrowIfCancellationRequested();
        long committedThrough = archiver.CommitBatch(CollectionsMarshal.AsSpan(_pendingBatch));
        _state.CommitThrough(committedThrough);
        ReadOnlyMemory<byte> durableWatermark = CommitThroughCodec.Encode(
            _pendingBatch[^1].Header.SessionId,
            committedThrough).Bytes;

        foreach (CanonicalFrame frame in _pendingBatch)
        {
            await multicast.SendAsync(frame.Bytes, cancellationToken);
        }

        await multicast.SendAsync(durableWatermark, cancellationToken);
        _committedThroughFrame = durableWatermark;
        _pendingBatch.Clear();
        _batchDeadline!.Dispose();
        _batchDeadline = null;
    }
}
