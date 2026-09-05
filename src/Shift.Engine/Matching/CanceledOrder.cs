namespace Shift.Engine.Matching;

public readonly record struct CanceledOrder(
    long OrderId,
    long CanceledQuantity);
