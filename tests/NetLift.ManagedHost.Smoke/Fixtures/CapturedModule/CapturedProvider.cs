using NetLift;
using NetLift.Negotiation.AsyncHost.Managed;
using NetLift.ManagedHost.Smoke.Fixtures;

namespace NetLift.ManagedHost.Smoke.Fixtures;

public sealed unsafe class CapturedProvider : ILiftAsyncCallbackV1ManagedHostModuleProviderV2
{
    public string GetMarker() => $"{GetRootMarker()}/{CapturedPrivateDependency.Marker}";

    public LiftAsyncCallbackV1GeneratedHostModuleApiV2 GetApiV2()
    {
        if (Environment.GetEnvironmentVariable("NETLIFT_CAPTURED_REGISTRATION_EXPECT_ORIGINAL") == "1" &&
            GetMarker() != "root-original/private-original")
            throw new InvalidOperationException(
                "The captured registration loaded a replacement root or private dependency.");
        if (Environment.GetEnvironmentVariable("NETLIFT_CAPTURED_PROVIDER_FAILURE") == "1")
            throw new InvalidOperationException("Injected captured-provider construction failure.");

        _ = CapturedPrivateDependency.Marker;
        return new AsyncCallbackV1ManagedHostModuleV2Provider().GetApiV2();
    }

    private static string GetRootMarker() => "root-original";
}
