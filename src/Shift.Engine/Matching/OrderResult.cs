using Shift.Protocol.Internal;

namespace Shift.Engine.Matching;

/// <summary>
/// The outcome of an order instruction. Quantities describe successful instructions only;
/// a rejection leaves the book unchanged.
/// </summary>
public readonly record struct OrderResult(
    RejectionReason RejectionReason,
    long RemainingQuantity = 0,
    long CanceledQuantity = 0,
    CancellationReason CancellationReason = CancellationReason.None,
    int FillCount = 0);
