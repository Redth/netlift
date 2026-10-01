using NetLift;

[LiftDto("point", Id = 2, Version = 1)]
public sealed record Point(
    [property: LiftField(1)] int X,
    [property: LiftField(2)] int Y);

[LiftService("transform", Id = 1, Version = 1)]
public interface ITransformService
{
    [LiftMethod(1)]
    Point Transform(Point point);
}

internal sealed class TransformService : ITransformService
{
    public Point Transform(Point point) => new(checked(point.X + 1), checked(point.Y * 2));
}
