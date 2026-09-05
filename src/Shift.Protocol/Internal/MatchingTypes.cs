namespace Shift.Protocol.Internal;

public enum OrderType : byte
{
    DayLimit = 1,
    ImmediateOrCancelLimit = 2,
    PostOnlyLimit = 3
}

public enum OrderSide : byte
{
    Buy = 1,
    Sell = 2
}

public readonly record struct ReferenceLevel(
    long PriceTicks,
    long Quantity);

public readonly record struct ReferenceLevelState(
    long PriceTicks,
    long RawObservedQuantity,
    long ExecutableShadowQuantity);

public readonly record struct Fill(
    long ParticipantOrderId,
    long PriceTicks,
    long Quantity,
    FillRole Role);

public enum FillRole : byte
{
    Maker = 1,
    Taker = 2
}

public readonly record struct CanceledOrder(
    long OrderId,
    long CanceledQuantity);

public enum RejectionReason : byte
{
    None = 0,
    InvalidOutputBuffer = 1,
    InvalidOrderId = 2,
    InvalidOrderSide = 3,
    InvalidPrice = 4,
    InvalidQuantity = 5,
    UnsupportedOrderType = 6,
    InvalidReferenceQuote = 7,
    ArithmeticOverflow = 8,
    DayAlreadyStarted = 9,
    DayNotStarted = 10,
    DuplicateOrderId = 11,
    UnknownOrder = 12,
    ReductionExceedsRemainingQuantity = 13,
    LockedOrCrossedReferenceQuote = 14,
    AmbiguousReferenceTrade = 15,
    InvalidPairId = 16
}

public enum CancellationReason : byte
{
    None = 0,
    Requested = 1,
    ImmediateOrCancel = 2,
    PostOnly = 3,
    SelfMatchPrevention = 4,
    EndOfDay = 5
}
