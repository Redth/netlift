using NetLift;
using NetLift.Negotiation.AsyncHost.Managed;

namespace NetLift.Negotiation.AsyncHost.Managed;

public sealed class AsyncCallbackV1ManagedHostModuleV2Provider :
    ILiftAsyncCallbackV1ManagedHostModuleProviderV2
{
    public LiftAsyncCallbackV1GeneratedHostModuleApiV2 GetApiV2() =>
        throw new InvalidOperationException(
            "The staged generated-provider replacement was executed.");
}
