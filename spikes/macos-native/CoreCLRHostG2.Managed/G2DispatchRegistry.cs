using NetLift;

namespace NetLift.CoreCLRHostG2;

internal sealed class G2DispatchRegistry(
    uint descriptorVersion,
    uint abiVersion,
    uint requiredFeatureBits,
    byte[] schemaHash) : ILiftDispatchRegistry
{
    private readonly object gate = new();
    private Dictionary<(uint ServiceId, uint MethodId), LiftManagedMethod>? methods;

    public LiftBootstrapStatus ValidateDescriptor(
        uint actualDescriptorVersion,
        uint actualAbiVersion,
        uint actualRequiredFeatureBits,
        ReadOnlySpan<byte> actualSchemaHash)
    {
        return actualDescriptorVersion == descriptorVersion &&
               actualAbiVersion == abiVersion &&
               actualRequiredFeatureBits == requiredFeatureBits &&
               actualSchemaHash.SequenceEqual(schemaHash)
            ? LiftBootstrapStatus.Success
            : LiftBootstrapStatus.SchemaMismatch;
    }

    public LiftBootstrapStatus TryRegisterBatch(
        uint actualAbiVersion,
        uint actualRequiredFeatureBits,
        ReadOnlySpan<byte> actualSchemaHash,
        IReadOnlyList<LiftMethodRegistration> registrations)
    {
        lock (gate)
        {
            if (methods is not null)
                return LiftBootstrapStatus.RegistrationConflict;

            var validation = ValidateDescriptor(
                descriptorVersion,
                actualAbiVersion,
                actualRequiredFeatureBits,
                actualSchemaHash);
            if (validation != LiftBootstrapStatus.Success)
                return validation;

            var batch = new Dictionary<(uint, uint), LiftManagedMethod>();
            foreach (var registration in registrations)
            {
                if (registration.Handler is null ||
                    !batch.TryAdd(
                        (registration.ServiceId, registration.MethodId),
                        registration.Handler))
                    return LiftBootstrapStatus.RegistrationConflict;
            }
            methods = batch;
            return LiftBootstrapStatus.Success;
        }
    }

    public ValueTask<LiftDispatchResult> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        LiftInvocationContext context)
    {
        LiftManagedMethod? handler = null;
        lock (gate)
            methods?.TryGetValue((serviceId, methodId), out handler);

        return handler is null
            ? ValueTask.FromResult(
                LiftDispatchResult.Failure(LiftDispatchStatus.Unsupported))
            : handler(payload, context);
    }
}
