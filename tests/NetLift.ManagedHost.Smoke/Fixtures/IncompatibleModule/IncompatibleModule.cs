using IncompatibleContracts;

namespace NetLift.ManagedHost.Smoke.Fixtures;

public sealed class IncompatibleModule
{
    public ContractMarker Marker { get; } = new();
}
