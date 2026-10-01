using ClosureDependency;
using NetLift;
using NetLift.Negotiation;
using NetLift.Negotiation.AsyncHost.Managed;
using System.Reflection;

namespace ClosureFixture;

public static class RootProbe
{
    public static string GetMarker() =>
        $"root-original/{DependencyProbe.GetMarker()}";

    public static ILiftAsyncCallbackV1ManagedHostModuleProviderV2 CreateProvider() =>
        new AsyncCallbackV1ManagedHostModuleV2Provider();

    public static Assembly GetNegotiationAssembly() =>
        typeof(LiftAsyncCallbackV1ManagedBridge).Assembly;
}
