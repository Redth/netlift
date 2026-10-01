using System.Threading;
using NetLift;
using NetLift.CoreCLRHostG2;
using NetLift.Generated;

namespace NetLift.Generated;

internal sealed class G2ServiceFactories : ILiftServiceFactories
{
    public IGreetingService CreateService_41(LiftInvocationContext context) =>
        new GreetingService(context);
}

internal static class G2Probe
{
    internal static int FactoryCreationCount;
}

public static partial class LiftApplicationBootstrap
{
    internal static partial ILiftServiceFactories? CreateServiceFactories()
    {
        Interlocked.Increment(ref G2Probe.FactoryCreationCount);
        return new G2ServiceFactories();
    }
}
