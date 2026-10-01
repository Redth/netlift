using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift;

namespace NetLift.ManagedHost;

public enum ManagedAppEndpointTransportStatusV1 : int
{
    Success = 0,
    InvalidArgument = 1,
    NotReady = 2,
    Unsupported = 3,
    BufferTooLarge = 6,
    MalformedPayload = 7,
    ManagedFailure = 8,
    StaleHandle = 10,
    Unavailable = 11,
    Cancelled = 12,
    HandlerFailure = 13,
    Busy = 14
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ManagedAppEndpointTransportApiV1
{
    public uint StructSize;
    public uint ApiVersion;
    public delegate* unmanaged[Cdecl]<ulong, nint, nint, int> ConfigureNativeCallbacks;
    public delegate* unmanaged[Cdecl]<
        ulong, ulong, ulong, ulong, byte*, uint, int> StartRequest;
    public delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, int> CancelOperation;
    public delegate* unmanaged[Cdecl]<
        ulong, ulong, ulong, ulong, uint, uint, byte*, uint, int> ReplyConfirmation;
    public delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, int> CancelConfirmation;
    public delegate* unmanaged[Cdecl]<ulong, int> RevokeEndpoint;
    public delegate* unmanaged[Cdecl]<
        ulong, uint*, uint*, uint*, uint*, int> GetOutstandingWork;
}

public static class ManagedAppEndpointTransportApiV1Exports
{
    public const uint Version = 1;
    public const int MaximumRequestBytes = LiftAsyncCallbackV1Wire.MaximumPayloadBytes;
    public const int MaximumDiagnosticBytes = LiftNativeReplyDecoder.MaxDiagnosticBytes;

    private const int Nlf1HeaderSize = 12;
    private const int MaximumActiveOperations = 32;
    private const int MaximumActiveConfirmations = 64;
    private static readonly ConcurrentDictionary<ulong, ManagedAppEndpointTransportState> Endpoints = new();
    private static readonly ConcurrentBag<IDisposable> UnreleasedRouteLeaseRoots = [];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NativeRequestCallback(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        ulong operationToken,
        uint serviceId,
        uint methodId,
        byte* payload,
        uint payloadLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void NativePublishResultCallback(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        uint status,
        byte* result,
        uint resultLength,
        byte* error,
        uint errorLength);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetApiV1(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        ManagedAppEndpointTransportApiV1* output) =>
        GetApiV1Core(requestedVersion, outputCapacity, requiredSize, output);

    internal static unsafe int GetApiV1Core(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        ManagedAppEndpointTransportApiV1* output)
    {
        if (requiredSize is null)
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);

        *requiredSize = (uint)sizeof(ManagedAppEndpointTransportApiV1);
        if (requestedVersion != Version)
            return Status(ManagedAppEndpointTransportStatusV1.Unsupported);
        if (output is null)
            return outputCapacity == 0
                ? Status(ManagedAppEndpointTransportStatusV1.Success)
                : Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);
        if (outputCapacity < sizeof(ManagedAppEndpointTransportApiV1))
            return Status(ManagedAppEndpointTransportStatusV1.BufferTooLarge);

        *output = new ManagedAppEndpointTransportApiV1
        {
            StructSize = (uint)sizeof(ManagedAppEndpointTransportApiV1),
            ApiVersion = Version,
            ConfigureNativeCallbacks = &ConfigureNativeCallbacks,
            StartRequest = &StartRequest,
            CancelOperation = &CancelOperation,
            ReplyConfirmation = &ReplyConfirmation,
            CancelConfirmation = &CancelConfirmation,
            RevokeEndpoint = &RevokeEndpoint,
            GetOutstandingWork = &GetOutstandingWork
        };
        return Status(ManagedAppEndpointTransportStatusV1.Success);
    }

    internal static int BeginHostClose(ulong endpointToken)
    {
        if (!Endpoints.TryGetValue(endpointToken, out var endpoint))
            return Status(ManagedAppEndpointTransportStatusV1.Success);

        endpoint.BeginDrain();
        return endpoint.HasOutstandingWork
            ? Status(ManagedAppEndpointTransportStatusV1.Busy)
            : Status(ManagedAppEndpointTransportStatusV1.Success);
    }

    internal static bool ReleaseAfterHostClose(ulong endpointToken)
    {
        if (!Endpoints.TryGetValue(endpointToken, out var endpoint))
            return true;
        if (!endpoint.TryReleaseCallbackOwner())
            return false;
        return Endpoints.TryRemove(
            new KeyValuePair<ulong, ManagedAppEndpointTransportState>(endpointToken, endpoint));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ConfigureNativeCallbacks(
        ulong endpointToken,
        nint requestCallback,
        nint publishResultCallback)
    {
        if (endpointToken == 0 || requestCallback == 0 || publishResultCallback == 0)
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);

        try
        {
            var provider = ManagedAppEndpointAuthority.Process.BindingProvider;
            if (!provider.TryAcquireRegisteredRoute(
                    endpointToken, out _, out _, out var routeLease) ||
                routeLease is null)
                return Status(ManagedAppEndpointTransportStatusV1.StaleHandle);

            using (routeLease)
            {
                var callbacks = new CallbackOwner(
                    Marshal.GetDelegateForFunctionPointer<NativeRequestCallback>(requestCallback),
                    Marshal.GetDelegateForFunctionPointer<NativePublishResultCallback>(publishResultCallback));
                var endpoint = new ManagedAppEndpointTransportState(callbacks);
                return Endpoints.TryAdd(endpointToken, endpoint)
                    ? Status(ManagedAppEndpointTransportStatusV1.Success)
                    : Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);
            }
        }
        catch (Exception exception)
        {
            LogFailure(nameof(ConfigureNativeCallbacks), exception);
            return Status(ManagedAppEndpointTransportStatusV1.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int StartRequest(
        ulong endpointToken,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        byte* request,
        uint requestLength)
    {
        if (endpointToken == 0 || registrationToken == 0 ||
            registrationGeneration == 0 || operationToken == 0 ||
            request is null || requestLength is < Nlf1HeaderSize or > MaximumRequestBytes)
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);
        if (!Endpoints.TryGetValue(endpointToken, out var endpoint))
            return Status(ManagedAppEndpointTransportStatusV1.NotReady);

        var key = new OperationKey(registrationToken, registrationGeneration, operationToken);
        var operation = new ManagedOperation(key);
        if (!endpoint.TryBeginOperation(operation, MaximumActiveOperations))
            return endpoint.IsRevoked
                ? Status(ManagedAppEndpointTransportStatusV1.Unavailable)
                : Status(ManagedAppEndpointTransportStatusV1.Busy);

        IDisposable? routeLease = null;
        try
        {
            if (!ManagedAppEndpointAuthority.Process.BindingProvider.TryAcquireRegisteredRoute(
                    endpointToken,
                    out var descriptor,
                    out var routeHandler,
                    out routeLease) ||
                routeLease is null)
            {
                endpoint.FinishOperation(operation, releaseRoute: false);
                return Status(ManagedAppEndpointTransportStatusV1.StaleHandle);
            }

            if (descriptor.EndpointToken != endpointToken ||
                !endpoint.TryAttachRoute(operation, descriptor, routeHandler, routeLease))
            {
                DisposeRouteLease(routeLease);
                routeLease = null;
                endpoint.FinishOperation(operation, releaseRoute: false);
                return Status(ManagedAppEndpointTransportStatusV1.Unavailable);
            }
            routeLease = null;

            var requestCopy = new ReadOnlySpan<byte>(request, checked((int)requestLength)).ToArray();
            _ = Task.Run(() => CompleteOperationAsync(endpoint, operation, requestCopy));
            return Status(ManagedAppEndpointTransportStatusV1.Success);
        }
        catch (Exception exception)
        {
            if (routeLease is not null)
            {
                try
                {
                    DisposeRouteLease(routeLease);
                }
                catch (Exception releaseException)
                {
                    LogFailure("StartRequest route rollback", releaseException);
                }
            }
            endpoint.FinishOperation(operation, releaseRoute: routeLease is null);
            LogFailure(nameof(StartRequest), exception);
            return Status(ManagedAppEndpointTransportStatusV1.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CancelOperation(
        ulong endpointToken,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken)
    {
        if (endpointToken == 0 || registrationToken == 0 ||
            registrationGeneration == 0 || operationToken == 0)
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);
        if (!Endpoints.TryGetValue(endpointToken, out var endpoint))
            return Status(ManagedAppEndpointTransportStatusV1.StaleHandle);
        return endpoint.CancelOperation(
                new OperationKey(registrationToken, registrationGeneration, operationToken))
            ? Status(ManagedAppEndpointTransportStatusV1.Success)
            : Status(ManagedAppEndpointTransportStatusV1.StaleHandle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReplyConfirmation(
        ulong endpointToken,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        uint outcome,
        uint accepted,
        byte* diagnostic,
        uint diagnosticLength)
    {
        if (endpointToken == 0 || registrationToken == 0 ||
            registrationGeneration == 0 || requestToken == 0 ||
            outcome > (uint)LiftNativeReplyKind.HandlerFailure ||
            accepted > 1 || diagnosticLength > MaximumDiagnosticBytes ||
            (diagnosticLength != 0 && diagnostic is null))
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);

        var kind = (LiftNativeReplyKind)outcome;
        if ((kind != LiftNativeReplyKind.Result && accepted != 0) ||
            (kind == LiftNativeReplyKind.Result && diagnosticLength != 0))
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);
        if (!Endpoints.TryGetValue(endpointToken, out var endpoint))
            return Status(ManagedAppEndpointTransportStatusV1.StaleHandle);

        try
        {
            var diagnosticBytes = diagnosticLength == 0
                ? ReadOnlyMemory<byte>.Empty
                : new ReadOnlySpan<byte>(diagnostic, checked((int)diagnosticLength)).ToArray();
            if (!LiftNativeReplyDecoder.IsValidDiagnostic(diagnosticBytes))
                return Status(ManagedAppEndpointTransportStatusV1.MalformedPayload);

            var reply = new LiftNativeReply(
                kind,
                kind == LiftNativeReplyKind.Result
                    ? new byte[] { accepted == 1 ? (byte)1 : (byte)0 }
                    : ReadOnlyMemory<byte>.Empty,
                diagnosticBytes);
            return endpoint.TryCompleteConfirmation(
                    new RequestKey(registrationToken, registrationGeneration, requestToken),
                    reply)
                ? Status(ManagedAppEndpointTransportStatusV1.Success)
                : Status(ManagedAppEndpointTransportStatusV1.StaleHandle);
        }
        catch (Exception exception)
        {
            LogFailure(nameof(ReplyConfirmation), exception);
            return Status(ManagedAppEndpointTransportStatusV1.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CancelConfirmation(
        ulong endpointToken,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken)
    {
        if (endpointToken == 0 || registrationToken == 0 ||
            registrationGeneration == 0 || requestToken == 0)
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);
        if (!Endpoints.TryGetValue(endpointToken, out var endpoint))
            return Status(ManagedAppEndpointTransportStatusV1.StaleHandle);
        return endpoint.TryCancelConfirmation(
                new RequestKey(registrationToken, registrationGeneration, requestToken))
            ? Status(ManagedAppEndpointTransportStatusV1.Success)
            : Status(ManagedAppEndpointTransportStatusV1.StaleHandle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RevokeEndpoint(ulong endpointToken)
    {
        if (endpointToken == 0)
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);
        if (!Endpoints.TryGetValue(endpointToken, out var endpoint))
            return Status(ManagedAppEndpointTransportStatusV1.StaleHandle);

        endpoint.BeginDrain();
        return Status(ManagedAppEndpointTransportStatusV1.Success);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int GetOutstandingWork(
        ulong endpointToken,
        uint* operations,
        uint* confirmations,
        uint* callbacks,
        uint* routeLeases)
    {
        if (operations is null || confirmations is null ||
            callbacks is null || routeLeases is null)
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);

        *operations = 0;
        *confirmations = 0;
        *callbacks = 0;
        *routeLeases = 0;
        if (endpointToken == 0)
            return Status(ManagedAppEndpointTransportStatusV1.InvalidArgument);
        if (!Endpoints.TryGetValue(endpointToken, out var endpoint))
            return Status(ManagedAppEndpointTransportStatusV1.StaleHandle);

        var work = endpoint.GetOutstandingWork();
        *operations = work.Operations;
        *confirmations = work.Confirmations;
        *callbacks = work.Callbacks;
        *routeLeases = checked((uint)ManagedAppEndpointAuthority.Process.BindingProvider.ActiveRouteLeaseCount);
        return Status(ManagedAppEndpointTransportStatusV1.Success);
    }

    private static async Task CompleteOperationAsync(
        ManagedAppEndpointTransportState endpoint,
        ManagedOperation operation,
        byte[] request)
    {
        uint status = (uint)ManagedAppEndpointTransportStatusV1.Success;
        byte[] result = [];
        byte[] error = [];
        try
        {
            var descriptor = operation.Descriptor
                ?? throw new InvalidOperationException("The managed route descriptor is unavailable.");
            var handler = operation.RouteHandler
                ?? throw new InvalidOperationException("The registered managed route is unavailable.");
            var correlationToken = NextToken();
            var invoker = new EndpointNativeServiceInvoker(endpoint, operation, descriptor);
            var invocationContext = new LiftInvocationContext(
                operation.Cancellation.Token,
                correlationToken,
                invoker)
            {
                EndpointToken = descriptor.EndpointToken,
                RegistrationToken = operation.Key.RegistrationToken,
                RegistrationGeneration = operation.Key.RegistrationGeneration,
                OperationToken = operation.Key.OperationToken,
                PeerRequestSchema = descriptor.PeerRequestSchema,
                ManagedResultSchema = descriptor.ManagedResultSchema
            };
            var encoded = await handler(
                request,
                invocationContext,
                operation.Cancellation.Token).ConfigureAwait(false);
            if (encoded.Length > MaximumRequestBytes)
            {
                status = (uint)ManagedAppEndpointTransportStatusV1.BufferTooLarge;
            }
            else
            {
                result = encoded.ToArray();
            }
        }
        catch (OperationCanceledException)
        {
            status = (uint)ManagedAppEndpointTransportStatusV1.Cancelled;
        }
        catch (LiftNativeServiceException exception)
        {
            if (exception.DiagnosticUtf8.Length > MaximumDiagnosticBytes ||
                !LiftNativeReplyDecoder.IsValidDiagnostic(exception.DiagnosticUtf8))
            {
                Console.Error.WriteLine(
                    "ManagedAppEndpointTransportApiV1 received an invalid native-service diagnostic.");
                status = (uint)ManagedAppEndpointTransportStatusV1.MalformedPayload;
            }
            else
            {
                status = exception.StatusCode;
                error = exception.DiagnosticUtf8.ToArray();
            }
        }
        catch (LiftAsyncCallbackV1ContractException exception)
        {
            Console.Error.WriteLine(
                $"Managed app endpoint route rejected a request: {exception.Message}");
            status = (uint)ManagedAppEndpointTransportStatusV1.MalformedPayload;
        }
        catch (EndpointNativeConfirmationException exception)
        {
            status = exception.Status;
            error = Encoding.UTF8.GetBytes(exception.Message);
        }
        catch (Exception exception)
        {
            LogFailure(nameof(CompleteOperationAsync), exception);
            status = (uint)ManagedAppEndpointTransportStatusV1.HandlerFailure;
        }

        try
        {
            endpoint.PublishResult(operation.Key, status, result, error);
        }
        catch (Exception exception)
        {
            LogFailure("PublishResult", exception);
        }
        finally
        {
            try
            {
                endpoint.FinishOperation(operation, releaseRoute: true);
            }
            catch (Exception exception)
            {
                LogFailure("ReleaseRouteLease", exception);
            }
        }
    }

    private static unsafe int CallNativeRequest(
        NativeRequestCallback callback,
        OperationKey operation,
        ulong requestToken,
        uint serviceId,
        uint methodId,
        byte[] payload)
    {
        unsafe
        {
            fixed (byte* payloadPointer = payload)
            {
                return callback(
                    operation.RegistrationToken,
                    operation.RegistrationGeneration,
                    requestToken,
                    operation.OperationToken,
                    serviceId,
                    methodId,
                    payloadPointer,
                    checked((uint)payload.Length));
            }
        }
    }

    private static ulong NextToken()
    {
        var next = Interlocked.Increment(ref _nextCorrelationToken);
        if (next <= 0)
            throw new InvalidOperationException("Endpoint transport correlation token space is exhausted.");
        return checked((ulong)next);
    }

    private static long _nextCorrelationToken;

    private static int Status(ManagedAppEndpointTransportStatusV1 status) => (int)status;

    private static void DisposeRouteLease(IDisposable routeLease)
    {
        try
        {
            routeLease.Dispose();
        }
        catch
        {
            UnreleasedRouteLeaseRoots.Add(routeLease);
            throw;
        }
    }

    private static void LogFailure(string operation, Exception exception) =>
        Console.Error.WriteLine(
            $"ManagedAppEndpointTransportApiV1.{operation} failed ({exception.GetType().Name}): {exception.Message}");

    private static async Task ObserveNativeRequestAsync(
        Task<int> callbackTask,
        ManagedAppEndpointTransportState endpoint,
        TaskCompletionSource<LiftNativeReply> completion)
    {
        try
        {
            if (await callbackTask.ConfigureAwait(false) != 1)
            {
                completion.TrySetException(new EndpointNativeConfirmationException(
                    (uint)ManagedAppEndpointTransportStatusV1.Unavailable,
                    "The native confirmation callback is unavailable."));
            }
        }
        catch (Exception exception)
        {
            LogFailure("NativeRequestCallback", exception);
            completion.TrySetException(new EndpointNativeConfirmationException(
                (uint)ManagedAppEndpointTransportStatusV1.HandlerFailure,
                $"The native confirmation callback failed ({exception.GetType().Name})."));
        }
        finally
        {
            endpoint.EndCallback();
        }
    }

    private sealed class EndpointNativeServiceInvoker(
        ManagedAppEndpointTransportState endpoint,
        ManagedOperation operation,
        LiftAsyncCallbackV1HostDescriptor descriptor) : ILiftAsyncCallbackV1NativeServiceInvoker
    {
        public ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            ulong correlationToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LiftNativeReply(
                LiftNativeReplyKind.HandlerFailure,
                ReadOnlyMemory<byte>.Empty,
                "Generation-aware native calls are required."u8.ToArray()));

        public async ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            LiftInvocationContext invocationContext,
            CancellationToken cancellationToken)
        {
            if (serviceId != descriptor.NativeServiceId ||
                methodId != descriptor.NativeMethodId)
                return Failure(LiftNativeReplyKind.UnknownMethod);
            if (!ReferenceEquals(invocationContext.NativeServices, this) ||
                invocationContext.EndpointToken != descriptor.EndpointToken ||
                invocationContext.RegistrationToken != operation.Key.RegistrationToken ||
                invocationContext.RegistrationGeneration != operation.Key.RegistrationGeneration ||
                invocationContext.OperationToken != operation.Key.OperationToken ||
                invocationContext.CorrelationToken == 0 ||
                invocationContext.CancellationToken != cancellationToken ||
                cancellationToken.IsCancellationRequested)
                return Failure(
                    cancellationToken.IsCancellationRequested
                        ? LiftNativeReplyKind.Cancelled
                        : LiftNativeReplyKind.HandlerFailure);

            var requestKey = new RequestKey(
                operation.Key.RegistrationToken,
                operation.Key.RegistrationGeneration,
                invocationContext.CorrelationToken);
            var completion = new TaskCompletionSource<LiftNativeReply>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!endpoint.TryAddConfirmation(requestKey, completion, MaximumActiveConfirmations))
                return Failure(LiftNativeReplyKind.HandlerFailure);

            try
            {
                using var cancellationRegistration = cancellationToken.Register(
                    () => completion.TrySetCanceled(cancellationToken));
                if (!endpoint.TryBeginCallback(out var callback))
                    return Failure(LiftNativeReplyKind.Unavailable);

                Task<int> callbackTask;
                try
                {
                    var requestPayload = payload.ToArray();
                    callbackTask = Task.Run(() => CallNativeRequest(
                        callback,
                        operation.Key,
                        requestKey.RequestToken,
                        descriptor.NativeServiceId,
                        methodId,
                        requestPayload));
                }
                catch
                {
                    endpoint.EndCallback();
                    throw;
                }
                _ = ObserveNativeRequestAsync(callbackTask, endpoint, completion);
                return await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failure(LiftNativeReplyKind.Cancelled);
            }
            catch (EndpointNativeConfirmationException exception)
            {
                return exception.Status == (uint)ManagedAppEndpointTransportStatusV1.Unavailable
                    ? Failure(LiftNativeReplyKind.Unavailable)
                    : Failure(LiftNativeReplyKind.HandlerFailure, exception.Message);
            }
            finally
            {
                endpoint.RemoveConfirmation(requestKey);
            }
        }

        private static LiftNativeReply Failure(
            LiftNativeReplyKind kind,
            string diagnostic = "") =>
            new(kind, ReadOnlyMemory<byte>.Empty, Encoding.UTF8.GetBytes(diagnostic));
    }

    private sealed class CallbackOwner(
        NativeRequestCallback request,
        NativePublishResultCallback publishResult)
    {
        public NativeRequestCallback Request { get; } = request;

        public NativePublishResultCallback PublishResult { get; } = publishResult;
    }

    private sealed class ManagedAppEndpointTransportState(CallbackOwner callbacks)
    {
        private readonly object _gate = new();
        private readonly Dictionary<OperationKey, ManagedOperation> _operations = [];
        private readonly Dictionary<RequestKey, TaskCompletionSource<LiftNativeReply>> _confirmations = [];
        private CallbackOwner? _callbacks = callbacks;
        private bool _revoked;
        private int _callbacksInFlight;

        public bool IsRevoked
        {
            get
            {
                lock (_gate)
                    return _revoked;
            }
        }

        public bool HasOutstandingWork
        {
            get
            {
                lock (_gate)
                    return _operations.Count != 0 ||
                           _confirmations.Count != 0 ||
                           _callbacksInFlight != 0;
            }
        }

        public bool TryBeginOperation(ManagedOperation operation, int maximumOperations)
        {
            lock (_gate)
            {
                if (_revoked || _callbacks is null ||
                    _operations.Count >= maximumOperations ||
                    _operations.ContainsKey(operation.Key))
                    return false;
                _operations.Add(operation.Key, operation);
                return true;
            }
        }

        public bool TryAttachRoute(
            ManagedOperation operation,
            LiftAsyncCallbackV1HostDescriptor descriptor,
            LiftAsyncCallbackV1ManagedRouteHandler routeHandler,
            IDisposable routeLease)
        {
            lock (_gate)
            {
                if (_revoked ||
                    !_operations.TryGetValue(operation.Key, out var current) ||
                    !ReferenceEquals(current, operation))
                    return false;
                operation.AttachRoute(descriptor, routeHandler, routeLease);
                return true;
            }
        }

        public void FinishOperation(ManagedOperation operation, bool releaseRoute)
        {
            if (releaseRoute)
                operation.ReleaseRoute();
            lock (_gate)
            {
                if (_operations.TryGetValue(operation.Key, out var current) &&
                    ReferenceEquals(current, operation))
                    _operations.Remove(operation.Key);
            }
            operation.DisposeCancellation();
        }

        public bool CancelOperation(OperationKey key)
        {
            ManagedOperation? operation;
            lock (_gate)
            {
                if (!_operations.TryGetValue(key, out operation))
                    return false;
            }
            operation.Cancel();
            return true;
        }

        public bool TryAddConfirmation(
            RequestKey key,
            TaskCompletionSource<LiftNativeReply> completion,
            int maximumConfirmations)
        {
            lock (_gate)
            {
                if (_revoked || _callbacks is null ||
                    _confirmations.Count >= maximumConfirmations ||
                    _confirmations.ContainsKey(key))
                    return false;
                _confirmations.Add(key, completion);
                return true;
            }
        }

        public bool TryCompleteConfirmation(RequestKey key, LiftNativeReply reply)
        {
            lock (_gate)
            {
                return _confirmations.TryGetValue(key, out var completion) &&
                       completion.TrySetResult(reply);
            }
        }

        public bool TryCancelConfirmation(RequestKey key)
        {
            lock (_gate)
            {
                return _confirmations.TryGetValue(key, out var completion) &&
                       completion.TrySetCanceled();
            }
        }

        public void RemoveConfirmation(RequestKey key)
        {
            lock (_gate)
                _confirmations.Remove(key);
        }

        public bool TryBeginCallback(out NativeRequestCallback callback)
        {
            lock (_gate)
            {
                if (_revoked || _callbacks is not { } owner)
                {
                    callback = null!;
                    return false;
                }
                _callbacksInFlight++;
                callback = owner.Request;
                return true;
            }
        }

        public void EndCallback()
        {
            lock (_gate)
            {
                if (_callbacksInFlight <= 0)
                    throw new InvalidOperationException("Native callback lease count underflow.");
                _callbacksInFlight--;
            }
        }

        public unsafe void PublishResult(
            OperationKey operation,
            uint status,
            byte[] result,
            byte[] error)
        {
            NativePublishResultCallback callback;
            lock (_gate)
            {
                callback = _callbacks?.PublishResult
                    ?? throw new InvalidOperationException(
                        "The endpoint result callback owner was released before dispatch completed.");
            }

            unsafe
            {
                fixed (byte* resultPointer = result)
                fixed (byte* errorPointer = error)
                    callback(
                        operation.RegistrationToken,
                        operation.RegistrationGeneration,
                        operation.OperationToken,
                        status,
                        resultPointer,
                        checked((uint)result.Length),
                        errorPointer,
                        checked((uint)error.Length));
            }
        }

        public void BeginDrain()
        {
            ManagedOperation[] operations;
            lock (_gate)
            {
                _revoked = true;
                operations = _operations.Values.ToArray();
            }
            foreach (var operation in operations)
            {
                try
                {
                    operation.Cancel();
                }
                catch (Exception exception)
                {
                    LogFailure("RevokeEndpoint", exception);
                }
            }
        }

        public (uint Operations, uint Confirmations, uint Callbacks) GetOutstandingWork()
        {
            lock (_gate)
                return (
                    checked((uint)_operations.Count),
                    checked((uint)_confirmations.Count),
                    checked((uint)_callbacksInFlight));
        }

        public bool TryReleaseCallbackOwner()
        {
            lock (_gate)
            {
                if (!_revoked ||
                    _operations.Count != 0 ||
                    _confirmations.Count != 0 ||
                    _callbacksInFlight != 0)
                    return false;
                _callbacks = null;
                return true;
            }
        }
    }

    private sealed class ManagedOperation(OperationKey key)
    {
        private IDisposable? _routeLease;
        private int _disposed;

        public OperationKey Key { get; } = key;

        public CancellationTokenSource Cancellation { get; } = new();

        public LiftAsyncCallbackV1HostDescriptor? Descriptor { get; private set; }

        public LiftAsyncCallbackV1ManagedRouteHandler? RouteHandler { get; private set; }

        public void AttachRoute(
            LiftAsyncCallbackV1HostDescriptor descriptor,
            LiftAsyncCallbackV1ManagedRouteHandler handler,
            IDisposable routeLease)
        {
            if (_routeLease is not null)
                throw new InvalidOperationException("A managed operation already owns a route lease.");
            Descriptor = LiftAsyncCallbackV1Wire.CopyDescriptor(descriptor);
            RouteHandler = handler;
            _routeLease = routeLease;
        }

        public void Cancel() => Cancellation.Cancel();

        public void ReleaseRoute()
        {
            var routeLease = Volatile.Read(ref _routeLease);
            if (routeLease is null)
                return;
            ManagedAppEndpointTransportApiV1Exports.DisposeRouteLease(routeLease);
            Interlocked.CompareExchange(ref _routeLease, null, routeLease);
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Cancellation.Dispose();
        }
    }

    private sealed class EndpointNativeConfirmationException(uint status, string message) : Exception(message)
    {
        public uint Status { get; } = status;
    }

    private readonly record struct OperationKey(
        ulong RegistrationToken,
        ulong RegistrationGeneration,
        ulong OperationToken);

    private readonly record struct RequestKey(
        ulong RegistrationToken,
        ulong RegistrationGeneration,
        ulong RequestToken);
}
