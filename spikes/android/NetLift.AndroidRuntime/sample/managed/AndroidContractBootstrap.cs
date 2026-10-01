using NetLift.AndroidRuntime.Managed;
using NetLift;

namespace NetLift.Generated;

public interface IStubService
{
    byte[] Echo(ReadOnlyMemory<byte> payload, LiftInvocationContext context);
}

public interface IStubServiceFactories
{
    IStubService CreateStubService(LiftInvocationContext context);
}

public static partial class LiftApplicationBootstrap
{
    public static partial IStubServiceFactories CreateServiceFactories();
}

public static partial class LiftApplicationBootstrap
{
    public static partial IStubServiceFactories CreateServiceFactories() =>
        new StubServiceFactories();
}

public static class ContractModule
{
    private static uint registrationDescriptorVersion = 1;
    private static uint registrationAbiVersion = 1;
    private static uint registrationFeatureBits = RequiredFeatureBits;
    private static byte[] registrationSchemaHash = Convert.FromHexString(SchemaHashHex);

    public const uint ServiceId = 0x51;
    public const uint MethodId = 1;
    public const uint RequiredFeatureBits = 1;
    public const string SchemaHashHex =
        "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    public static uint SetRegistrationMismatchV1(uint mismatchKind)
    {
        switch (mismatchKind)
        {
            case 1:
                registrationDescriptorVersion++;
                break;
            case 2:
                registrationAbiVersion++;
                break;
            case 3:
                registrationFeatureBits++;
                break;
            case 4:
                registrationSchemaHash = [.. registrationSchemaHash];
                registrationSchemaHash[0] ^= 0xff;
                break;
            default:
                return (uint)LiftBootstrapStatus.InvalidArgument;
        }
        return (uint)LiftBootstrapStatus.Success;
    }

    public static (
        uint DescriptorVersion,
        uint AbiVersion,
        uint FeatureBits,
        byte[] SchemaHash) GetRegistrationDescriptor() =>
        (
            registrationDescriptorVersion,
            registrationAbiVersion,
            registrationFeatureBits,
            registrationSchemaHash
        );

    public static IReadOnlyList<LiftMethodRegistration> CreateRegistrations(
        IStubServiceFactories factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        return
        [
            new LiftMethodRegistration(ServiceId, MethodId, (payload, context) =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var service = factories.CreateStubService(context);
                var result = service.Echo(payload, context);
                return ValueTask.FromResult(LiftDispatchResult.Success(result));
            })
        ];
    }
}

public static class AndroidContractBootstrap
{
    public static unsafe uint DescribeV1(
        LiftContractDescriptorV1* output,
        uint outputCapacity)
    {
        if (output is null)
            return (uint)LiftBootstrapStatus.InvalidArgument;
        if (outputCapacity < (uint)sizeof(LiftContractDescriptorV1))
            return (uint)LiftBootstrapStatus.BufferTooSmall;

        output->StructSize = (uint)sizeof(LiftContractDescriptorV1);
        output->DescriptorVersion = 1;
        output->AbiVersion = 1;
        output->RequiredFeatureBits = ContractModule.RequiredFeatureBits;
        for (var index = 0; index < 32; index++)
            output->SchemaHash[index] = (byte)(index + 1);
        return 0;
    }

    public static uint RegisterV1()
    {
        if (!LiftGeneratedHost.TryGetRegistry(out var registry))
            return (uint)LiftBootstrapStatus.NotReady;

        var (descriptorVersion, abiVersion, featureBits, schemaHash) =
            ContractModule.GetRegistrationDescriptor();
        var validation = registry.ValidateDescriptor(
            descriptorVersion,
            abiVersion,
            featureBits,
            schemaHash);
        if (validation != LiftBootstrapStatus.Success)
            return (uint)validation;

        try
        {
            var factories = LiftApplicationBootstrap.CreateServiceFactories();
            var registrations = ContractModule.CreateRegistrations(factories);
            return (uint)registry.TryRegisterBatch(
                abiVersion,
                featureBits,
                schemaHash,
                registrations);
        }
        catch (Exception)
        {
            return (uint)LiftBootstrapStatus.ManagedFailure;
        }
    }

    public static uint FactoryCreationCountV1() => StubServiceFactories.CreationCount;
    public static uint SetRegistrationMismatchV1(uint mismatchKind) =>
        ContractModule.SetRegistrationMismatchV1(mismatchKind);
}

internal sealed class StubServiceFactories : IStubServiceFactories
{
    private static int creationCount;
    public static uint CreationCount => unchecked((uint)Volatile.Read(ref creationCount));

    public StubServiceFactories() => Interlocked.Increment(ref creationCount);

    public IStubService CreateStubService(LiftInvocationContext context) =>
        new StubService();
}

internal sealed class StubService : IStubService
{
    public byte[] Echo(ReadOnlyMemory<byte> payload, LiftInvocationContext context) =>
        payload.ToArray();
}
