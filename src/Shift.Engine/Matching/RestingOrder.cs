namespace Shift.Engine.Matching;

public readonly record struct RestingOrder(
    long OrderId,
    OrderSide Side,
    long PriceTicks,
    long RemainingQuantity);
