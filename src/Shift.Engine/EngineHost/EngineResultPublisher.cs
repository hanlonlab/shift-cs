using Shift.Engine.Matching;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Shift.Protocol.Internal.Events;

namespace Shift.Engine.EngineHost;

/// <summary>
/// Encodes and submits engine outcomes, then tracks their committed echoes.
/// The host calls this serially and owns the submission socket.
/// </summary>
internal sealed class EngineResultPublisher
{
    private readonly UnixDatagramSender _submissions;
    private readonly Guid _sessionId;
    private readonly Queue<CanonicalFrame> _pending = new();
    private ulong _producerSequence;

    public EngineResultPublisher(UnixDatagramSender submissions, Guid sessionId, ushort producerId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(producerId);
        _submissions = submissions;
        _sessionId = sessionId;
        ProducerId = producerId;
    }

    public ushort ProducerId { get; }

    public bool HasPending => _pending.Count != 0;

    public int ObservedCount { get; private set; }

    public async ValueTask PublishAsync(
        PlaceOrder order,
        OrderResult result,
        ReadOnlyMemory<Fill> fills,
        CancellationToken cancellationToken)
    {
        byte[] payload = new byte[FrameCodec.MaximumFrameSize - FrameCodec.MinimumFrameSize];
        int length = OrderUpdatedCodec.Encode(new OrderUpdated(
            order.PairId,
            order.OrderId,
            result.RemainingQuantity,
            result.CanceledQuantity,
            result.RejectionReason,
            result.CancellationReason), payload);
        await SubmitAsync(MessageType.OrderUpdated, payload.AsMemory(0, length), cancellationToken);

        for (int index = 0; index < fills.Length; index++)
        {
            length = TradeExecutedCodec.Encode(new TradeExecuted(order.PairId, fills.Span[index]), payload);
            await SubmitAsync(MessageType.TradeExecuted, payload.AsMemory(0, length), cancellationToken);
        }
    }

    public void Observe(CanonicalFrame frame)
    {
        if (!_pending.TryPeek(out CanonicalFrame expected)
            || frame.Header.ProducerId != ProducerId
            || frame.Header.ProducerSequence != expected.Header.ProducerSequence
            || frame.Header.MessageType != expected.Header.MessageType
            || !frame.Payload.Span.SequenceEqual(expected.Payload.Span))
        {
            throw new InvalidDataException("Committed engine result does not match the pending output.");
        }

        _pending.Dequeue();
        ObservedCount++;
    }

    private async ValueTask SubmitAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        CanonicalFrame submission = FrameCodec.Encode(
            type, _sessionId, ProducerId, ++_producerSequence, 0, payload.Span);
        _pending.Enqueue(submission);
        await _submissions.SendAsync(submission.Bytes, cancellationToken);
    }
}
