using NetLift;

[LiftDto("person", Id = 2, Version = 1)]
public sealed record Person(
    [property: LiftField(1)] string Name,
    [property: LiftField(2)] int Age);

[LiftService("echo", Id = 1, Version = 1)]
public interface IEchoService
{
    [LiftMethod(7)]
    Person Echo(Person input);
}
