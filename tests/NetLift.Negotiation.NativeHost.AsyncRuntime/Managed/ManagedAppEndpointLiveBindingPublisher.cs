using System.Runtime.CompilerServices;
using NetLift.ManagedHost;
using NetLift.Negotiation;
#if NETLIFT_ASYNC_RUNTIME_NEW
using EndpointHost = NetLift.Negotiation.NativeHost.Managed.New;
#else
using EndpointHost = NetLift.Negotiation.NativeHost.Managed.Old;
#endif

namespace NetLift.Negotiation.NativeHost.AsyncRuntime;

internal static unsafe class ManagedAppEndpointLiveBindingPublisher
{
    private const uint Success = 0;
    private const uint SchemaBufferTooSmall = 12;
    private const int MaximumPeerSchemaBytes = 344;
    private const int MaximumManagedResultSchemaBytes = 392;
    private static readonly ManagedAppEndpointBindingProvider Provider = ConfigureProvider();

    internal static ManagedAppEndpointBindingProvider BindingProvider => Provider;

    public static void PublishFromEndpoint(ulong endpointToken)
    {
        var localManifest = CopyLocalManifest();
        var localNegotiationManifest = NegotiationManifest.LoadEmbedded(localManifest);
        var peerRequestSchema = CopyPeerRequestSchema(endpointToken);
        var route = ManagedAppEndpointDescriptorFactory.GetManagedRouteIds(localManifest);
        var managedResultSchema = CopyManagedResultSchema(
            endpointToken, route.ServiceId, route.MethodId);
        var descriptor = ManagedAppEndpointDescriptorFactory.Create(
            endpointToken, localManifest, peerRequestSchema, managedResultSchema);
        if (!descriptor.LocalSchemaHash.Span.SequenceEqual(
                Convert.FromHexString(localNegotiationManifest.SchemaHash)))
            throw new InvalidOperationException(
                "The endpoint publisher's local schema pin differs from its embedded manifest.");

        EndpointHost.NativeApi.BeforeManagedAppBindingPublishForTesting?.Invoke();
        var publishStatus = Provider.Publish(descriptor);
        if (publishStatus != ManagedAppEndpointBindingStatus.Success)
            throw new InvalidOperationException(
                $"Live endpoint binding publication failed with status {publishStatus}.");
    }

    private static ManagedAppEndpointBindingProvider ConfigureProvider()
    {
        if (ManagedAppHostApiV1Exports.ConfiguredBindingProvider is { } configured)
        {
            return configured as ManagedAppEndpointBindingProvider
                ?? throw new InvalidOperationException(
                    "The process endpoint-binding provider has an incompatible implementation.");
        }

        var provider = new ManagedAppEndpointBindingProvider();
        ManagedAppHostApiV1Exports.ConfigureTestEndpointBindingProvider(provider);
        return provider;
    }

    private static byte[] CopyLocalManifest()
    {
        var output = new EndpointHost.LiftV5OwnedBytes
        {
            StructSize = (uint)sizeof(EndpointHost.LiftV5OwnedBytes)
        };
        var status = NewStatus();
        var getManifest =
            (delegate* unmanaged[Cdecl]<
                EndpointHost.LiftV5OwnedBytes*,
                EndpointHost.LiftV5Status*,
                uint>)&EndpointHost.NativeApi.GetManifest;
        var result = getManifest(&output, &status);
        try
        {
            EnsureSuccess(result, status, "get the endpoint's local manifest");
            if (output.Data is null || output.Length == 0)
                throw new InvalidOperationException("The endpoint returned an empty local manifest.");

            return new ReadOnlySpan<byte>(output.Data, checked((int)output.Length)).ToArray();
        }
        finally
        {
            if (output.Data is not null)
                FreeBuffer(output.Data);
        }
    }

    private static byte[] CopyPeerRequestSchema(ulong endpointToken)
    {
        var output = new EndpointHost.LiftV5OwnedPeerSchema
        {
            StructSize = (uint)sizeof(EndpointHost.LiftV5OwnedPeerSchema),
            Version = 1
        };
        var status = NewStatus();
        var getPeerSchema =
            (delegate* unmanaged[Cdecl]<
                ulong,
                EndpointHost.LiftV5OwnedPeerSchema*,
                EndpointHost.LiftV5Status*,
                uint>)&EndpointHost.NativeApi.GetPeerSchema;
        var result = getPeerSchema(endpointToken, &output, &status);
        try
        {
            EnsureSuccess(result, status, "get the negotiated peer schema");
            if (output.Data is null ||
                output.Length is < 88 or > MaximumPeerSchemaBytes)
                throw new InvalidOperationException("The endpoint returned an invalid NLPS descriptor.");

            return new ReadOnlySpan<byte>(output.Data, checked((int)output.Length)).ToArray();
        }
        finally
        {
            if (output.Data is not null)
                FreeBuffer(output.Data);
        }
    }

    private static byte[] CopyManagedResultSchema(
        ulong endpointToken,
        uint serviceId,
        uint methodId)
    {
        var getManagedResultSchema =
            (delegate* unmanaged[Cdecl]<
                ulong,
                uint,
                uint,
                byte*,
                uint,
                uint*,
                uint*,
                uint>)&EndpointHost.NativeApi.GetManagedResultSchema;
        uint bytesWritten = 0;
        uint requiredBytes = 0;
        var queryStatus = getManagedResultSchema(
            endpointToken,
            serviceId,
            methodId,
            null,
            0,
            &bytesWritten,
            &requiredBytes);
        if (queryStatus != SchemaBufferTooSmall ||
            bytesWritten != 0 ||
            requiredBytes is < 136 or > MaximumManagedResultSchemaBytes)
            throw new InvalidOperationException(
                $"The endpoint managed-result schema size query failed with status {queryStatus}.");

        var bytes = new byte[checked((int)requiredBytes)];
        fixed (byte* destination = bytes)
        {
            var result = getManagedResultSchema(
                endpointToken,
                serviceId,
                methodId,
                destination,
                (uint)bytes.Length,
                &bytesWritten,
                &requiredBytes);
            if (result != Success ||
                bytesWritten != bytes.Length ||
                requiredBytes != bytes.Length)
                throw new InvalidOperationException(
                    $"The endpoint managed-result schema copy failed with status {result}.");
        }
        return bytes;
    }

    private static void FreeBuffer(void* buffer)
    {
        var status = NewStatus();
        var freeBuffer =
            (delegate* unmanaged[Cdecl]<
                void*,
                EndpointHost.LiftV5Status*,
                uint>)&EndpointHost.NativeApi.FreeBuffer;
        var result = freeBuffer(buffer, &status);
        EnsureSuccess(result, status, "free an endpoint-owned schema buffer");
    }

    private static EndpointHost.LiftV5Status NewStatus() =>
        new()
        {
            StructSize = (uint)sizeof(EndpointHost.LiftV5Status)
        };

    private static void EnsureSuccess(
        uint result,
        EndpointHost.LiftV5Status status,
        string operation)
    {
        if (result != Success || status.Code != Success)
            throw new InvalidOperationException(
                $"Could not {operation}: status={result}, detail={status.Code}.");
    }
}
