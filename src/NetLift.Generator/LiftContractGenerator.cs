using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace NetLift.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class LiftContractGenerator : IIncrementalGenerator
{
    private const string ServiceAttribute = "NetLift.LiftServiceAttribute";
    private const string NativeServiceAttribute = "NetLift.LiftNativeServiceAttribute";
    private const string MethodAttribute = "NetLift.LiftMethodAttribute";
    private const string StreamAttribute = "NetLift.LiftStreamAttribute";
    private const string EventAttribute = "NetLift.LiftEventAttribute";
    private const string DtoAttribute = "NetLift.LiftDtoAttribute";
    private const string FieldAttribute = "NetLift.LiftFieldAttribute";
    private const string ContractRevisionAttribute = "NetLift.LiftContractRevisionAttribute";
    private const string ContractProfileAttribute = "NetLift.LiftContractProfileAttribute";
    private const string HostModuleAttribute = "NetLift.LiftHostModuleAttribute";
    private const int AsyncCallbackV1Profile = 1;

    private static readonly DiagnosticDescriptor Unsupported = new(
        "NLT001", "Unsupported contract type",
        "Type '{0}' is not supported in an opted-in contract; use a supported scalar or [LiftDto] type",
        "NetLift.Contract", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidContract = new(
        "NLT002", "Invalid contract declaration",
        "{0}", "NetLift.Contract", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingHostFactory = new(
        "NLT003", "Missing generated host factory",
        "An assembly opted into [LiftHostModule] must implement NetLift.Generated.LiftApplicationBootstrap.CreateServiceFactories().",
        "NetLift.Contract", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var services = context.SyntaxProvider.ForAttributeWithMetadataName(
            ServiceAttribute,
            static (node, _) => node is InterfaceDeclarationSyntax,
            static (syntaxContext, _) => (INamedTypeSymbol)syntaxContext.TargetSymbol)
            .Collect()
            .WithComparer(NamedTypeArrayComparer.Instance);

        var nativeServices = context.SyntaxProvider.ForAttributeWithMetadataName(
            NativeServiceAttribute,
            static (node, _) => node is InterfaceDeclarationSyntax,
            static (syntaxContext, _) => (INamedTypeSymbol)syntaxContext.TargetSymbol)
            .Collect()
            .WithComparer(NamedTypeArrayComparer.Instance);

        var dtos = context.SyntaxProvider.ForAttributeWithMetadataName(
            DtoAttribute,
            static (node, _) => node is RecordDeclarationSyntax or ClassDeclarationSyntax,
            static (syntaxContext, _) => (INamedTypeSymbol)syntaxContext.TargetSymbol)
            .Collect()
            .WithComparer(NamedTypeArrayComparer.Instance);

        var hostModuleEnabled = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is AttributeSyntax attribute &&
                    attribute.Parent is AttributeListSyntax list &&
                    list.Target?.Identifier.ValueText == "assembly",
                static (syntaxContext, _) =>
                    syntaxContext.SemanticModel.GetSymbolInfo((AttributeSyntax)syntaxContext.Node)
                        .Symbol?.ContainingType?.ToDisplayString() == HostModuleAttribute)
            .Collect()
            .Select(static (attributes, _) => attributes.Any(static matches => matches))
            .WithComparer(EqualityComparer<bool>.Default)
            .WithTrackingName("HostModuleOptIn");

        var hostFactoryImplemented = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax declaration &&
                    declaration.Identifier.ValueText == "LiftApplicationBootstrap",
                static (syntaxContext, _) =>
                {
                    var declaration = (ClassDeclarationSyntax)syntaxContext.Node;
                    if (syntaxContext.SemanticModel.GetDeclaredSymbol(declaration)?.ToDisplayString() !=
                        "NetLift.Generated.LiftApplicationBootstrap")
                        return false;

                    return declaration.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                        method.Identifier.ValueText == "CreateServiceFactories" &&
                        method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) &&
                        method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)) &&
                        (method.Body is not null || method.ExpressionBody is not null));
                })
            .Collect()
            .Select(static (declarations, _) => declarations.Any(static implements => implements))
            .WithComparer(EqualityComparer<bool>.Default)
            .WithTrackingName("HostFactoryShape");

        var contractRevision = context.CompilationProvider
            .Select(static (compilation, _) => ReadContractRevision(compilation.Assembly))
            .WithTrackingName("ContractRevisionOptIn");
        var contractProfile = context.CompilationProvider
            .Select(static (compilation, _) => ReadContractProfile(compilation.Assembly))
            .WithTrackingName("ContractProfileOptIn");

        var contractInputs = services.Combine(nativeServices).Combine(dtos)
            .WithTrackingName("ContractSymbols");
        var generationInputs = contractInputs.Combine(hostModuleEnabled).Combine(hostFactoryImplemented)
            .Combine(contractRevision.Combine(contractProfile))
            .WithTrackingName("ContractGenerationInput");
        context.RegisterSourceOutput(generationInputs,
            static (productionContext, input) =>
                LiftContractGenerator.Generate(productionContext,
                    input.Left.Left.Left.Left.Left,
                    input.Left.Left.Left.Left.Right,
                    input.Left.Left.Left.Right,
                    input.Left.Left.Right,
                    input.Left.Right,
                    input.Right.Left,
                    input.Right.Right));
    }

    private static void Generate(
        SourceProductionContext context,
        IReadOnlyList<INamedTypeSymbol> serviceSymbols,
        IReadOnlyList<INamedTypeSymbol> nativeServiceSymbols,
        IReadOnlyList<INamedTypeSymbol> dtoSymbols,
        bool hostModuleEnabled,
        bool hasServiceFactoryImplementation,
        ContractRevisionData? contractRevision,
        ContractProfileData? contractProfile)
    {
        var dtos = dtoSymbols
            .Select(ParseDto)
            .OrderBy(dto => dto.Id)
            .ThenBy(dto => dto.Name, StringComparer.Ordinal)
            .ToArray();
        var dtoBySymbol = new Dictionary<INamedTypeSymbol, DtoData>(NamedTypeSymbolComparer.Instance);
        foreach (var symbol in dtoSymbols)
            dtoBySymbol[symbol] = ParseDto(symbol);
        var dtoDeclarationsValid = ValidateDtos(context, dtos);
        var services = serviceSymbols.Select(symbol => ParseService(context, symbol, dtoBySymbol, isNative: false))
            .Concat(nativeServiceSymbols.Select(symbol =>
                ParseService(context, symbol, dtoBySymbol, isNative: true)))
            .OrderBy(service => service.Id)
            .ThenBy(service => service.Name, StringComparer.Ordinal)
            .ToArray();

        var contractIdsValid = ValidateIds(context, services, dtos);

        if (contractProfile?.Error is { } profileError)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidContract,
                contractProfile.Location ?? Location.None, profileError));
            return;
        }
        if (contractProfile is not null && contractRevision is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidContract,
                contractProfile.Location ?? Location.None,
                "[LiftContractProfile] requires an assembly-level [LiftContractRevision(major, minor)]."));
            return;
        }
        if (contractRevision?.Error is { } revisionError)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidContract,
                contractRevision.Location ?? Location.None, revisionError));
            return;
        }

        if (contractRevision is null &&
            dtos.SelectMany(dto => dto.Fields).Any(field => field.Optional || field.HasDefault))
        {
            foreach (var field in dtos.SelectMany(dto => dto.Fields)
                         .Where(field => field.Optional || field.HasDefault))
                Report(context, field.Symbol,
                    "LiftField Optional/DefaultValue metadata requires an assembly-level [LiftContractRevision(major, minor)] opt-in.");
            return;
        }

        if (contractRevision is not null)
        {
            var asyncCallbackProfile = contractProfile?.Profile == AsyncCallbackV1Profile;
            var v5ContractValid = asyncCallbackProfile
                ? ValidateAsyncCallbackV5Contract(context, contractRevision, services, dtos, hostModuleEnabled)
                : ValidateV5Contract(context, contractRevision, services, dtos, hostModuleEnabled);
            if (!dtoDeclarationsValid || !contractIdsValid || !v5ContractValid)
                return;

            var v5Manifest = asyncCallbackProfile
                ? SerializeAsyncCallbackManifest(contractRevision, services, dtos, out var v5Hash)
                : SerializeNegotiationManifest(contractRevision, services[0], services[0].Methods[0],
                    dtos[0], out v5Hash);
            context.AddSource("LiftContract.g.cs",
                SourceText.From(EmitSource(v5Manifest, v5Hash, services, dtos,
                    emitDtoCodecs: false,
                    emitNativeServiceAdapters: false,
                    asyncCallbackV1Profile: asyncCallbackProfile), Encoding.UTF8));
            return;
        }

        var typedReplies = services.Any(service => service.ReplyProtocol == "typed-v1");
        var hasStreams = services.Any(service => service.Streams.Count != 0 || service.Events.Count != 0);
        var schemaWithoutHash = hasStreams
            ? SerializeStreamSchema(services, dtos, null)
            : typedReplies ? SerializeTypedSchema(services, dtos, null) : SerializeSchema(services, dtos, null);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schemaWithoutHash))).ToLowerInvariant();
        var schema = hasStreams
            ? SerializeStreamSchema(services, dtos, hash)
            : typedReplies ? SerializeTypedSchema(services, dtos, hash) : SerializeSchema(services, dtos, hash);
        var source = EmitSource(schema, hash, services, dtos);
        context.AddSource("LiftContract.g.cs", SourceText.From(source, Encoding.UTF8));

        if (hostModuleEnabled)
        {
            if (!hasServiceFactoryImplementation)
                context.ReportDiagnostic(Diagnostic.Create(MissingHostFactory,
                    Location.None));

            ValidateHostModule(context, services);
            context.AddSource("LiftHostModule.g.cs",
                SourceText.From(EmitHostModule(services), Encoding.UTF8));
        }
    }

    private static void ValidateHostModule(
        SourceProductionContext context,
        IReadOnlyList<ServiceData> services)
    {
        var managedRoutes = 0;
        var managedStreams = 0;
        foreach (var service in services.Where(service => service.Kind == "managed"))
        {
            if (service.Events.Count != 0)
                Report(context, service.Symbol,
                    "Opted-in v4 host modules do not yet support [LiftEvent] registrations.");

            foreach (var method in service.Methods)
            {
                if (!IsHostDispatchSupported(method))
                {
                    Report(context, method.Symbol,
                        "Opted-in host modules currently support one non-null [LiftDto] parameter and a non-null [LiftDto] result.");
                    continue;
                }

                managedRoutes++;
            }

            foreach (var stream in service.Streams)
            {
                if (!IsHostStreamSupported(stream))
                {
                    Report(context, stream.Symbol,
                        "Opted-in v4 host modules currently support [LiftStream] methods with no payload parameters and a non-null [LiftDto] item type.");
                    continue;
                }

                managedStreams++;
            }
        }

        if (managedRoutes == 0 && managedStreams == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidContract, Location.None,
                "An opted-in host module requires at least one supported managed method or stream."));
        }
    }

    private static bool IsHostDispatchSupported(MethodData method) =>
        method.Parameters.Count == 1 &&
        method.Parameters[0].Type.Kind == "dto" &&
        !method.Parameters[0].Type.Nullable &&
        method.ReturnType is { Kind: "dto", Nullable: false };

    private static bool IsHostStreamSupported(StreamData stream) =>
        stream.Parameters.Count == 0 &&
        stream.ItemType is { Kind: "dto", Nullable: false };

    private static string EmitHostModule(IReadOnlyList<ServiceData> services)
    {
        var hasV4Contract = services.Any(service =>
            service.Streams.Count != 0 || service.Events.Count != 0);
        var methods = services
            .Where(service => service.Kind == "managed")
            .SelectMany(service => service.Methods
                .Where(IsHostDispatchSupported)
                .Select(method => (Service: service, Method: method)))
            .OrderBy(route => route.Service.Id)
            .ThenBy(route => route.Method.Id)
            .ToArray();
        var subscriptions = services
            .Where(service => service.Kind == "managed")
            .SelectMany(service => service.Streams
                .Where(IsHostStreamSupported)
                .Select(stream => (Service: service, Stream: stream)))
            .OrderBy(subscription => subscription.Service.Id)
            .ThenBy(subscription => subscription.Stream.Id)
            .ToArray();
        var requiredFeatureBits = GetHostFeatureBits(services);
        var builder = new StringBuilder()
            .AppendLine("// <auto-generated/>")
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated;")
            .AppendLine("internal interface ILiftServiceFactories")
            .AppendLine("{");

        foreach (var service in services.Where(service => service.Kind == "managed" &&
                     (service.Methods.Any(IsHostDispatchSupported) ||
                      service.Streams.Any(IsHostStreamSupported))))
        {
            var serviceType = service.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            builder.Append("    ").Append(serviceType).Append(" CreateService_")
                .Append(service.Id);
            if (hasV4Contract)
                builder.AppendLine("();");
            else
                builder.AppendLine("(global::NetLift.LiftInvocationContext context);");
        }

        builder.AppendLine("}")
            .AppendLine("public static partial class LiftApplicationBootstrap")
            .AppendLine("{")
            .AppendLine("    internal static partial ILiftServiceFactories? CreateServiceFactories();")
            .AppendLine("}")
            .AppendLine("internal static class ContractModule")
            .AppendLine("{")
            .Append("    private const uint DescriptorVersion = 1u;").AppendLine()
            .Append("    private const uint AbiVersion = 1u;").AppendLine()
            .Append("    private const uint RequiredFeatureBits = ").Append(requiredFeatureBits).AppendLine("u;")
            .AppendLine("    internal static readonly byte[] SchemaHashBytes = global::System.Convert.FromHexString(Contract.SchemaHash);")
            .AppendLine();
        if (!hasV4Contract)
        {
            builder.AppendLine("    internal static global::NetLift.LiftBootstrapStatus Register(")
                .AppendLine("        global::NetLift.ILiftDispatchRegistry registry,")
                .AppendLine("        ILiftServiceFactories? factories)")
                .AppendLine("    {")
                .AppendLine("        if (registry is null) return global::NetLift.LiftBootstrapStatus.NotReady;")
                .AppendLine("        if (factories is null) return global::NetLift.LiftBootstrapStatus.FactoryUnavailable;")
                .AppendLine("        var validation = registry.ValidateDescriptor(DescriptorVersion, AbiVersion, RequiredFeatureBits, SchemaHashBytes);")
                .AppendLine("        if (validation != global::NetLift.LiftBootstrapStatus.Success) return validation;")
                .AppendLine("        var registrations = new global::NetLift.LiftMethodRegistration[]")
                .AppendLine("        {");

            foreach (var (service, method) in methods)
            {
                builder.Append("            new global::NetLift.LiftMethodRegistration(")
                    .Append(service.Id).Append("u, ").Append(method.Id).Append("u, ")
                    .Append("(payload, context) => Dispatch_").Append(service.Id).Append('_')
                    .Append(method.Id).AppendLine("(payload, context, factories)),");
            }

            builder.AppendLine("        };")
                .AppendLine("        return registry.TryRegisterBatch(AbiVersion, RequiredFeatureBits, SchemaHashBytes, registrations);")
                .AppendLine("    }");
        }
        else
        {
            builder.AppendLine("    internal static global::NetLift.LiftBootstrapStatus RegisterV4(")
                .AppendLine("        global::NetLift.ILiftV4DispatchRegistry registry,")
                .AppendLine("        ILiftServiceFactories? factories)")
                .AppendLine("    {")
                .AppendLine("        if (registry is null) return global::NetLift.LiftBootstrapStatus.NotReady;")
                .AppendLine("        if (factories is null) return global::NetLift.LiftBootstrapStatus.FactoryUnavailable;");
            foreach (var service in services.Where(service => service.Kind == "managed" &&
                         (service.Methods.Any(IsHostDispatchSupported) ||
                          service.Streams.Any(IsHostStreamSupported))))
            {
                builder.Append("        var service_").Append(service.Id).Append(" = factories.CreateService_")
                    .Append(service.Id).AppendLine("();")
                    .Append("        if (service_").Append(service.Id).AppendLine(" is null)")
                    .AppendLine("            return global::NetLift.LiftBootstrapStatus.FactoryUnavailable;");
            }
            builder.AppendLine("        var methods = new global::NetLift.LiftMethodRegistration[]")
                .AppendLine("        {");
            foreach (var (service, method) in methods)
            {
                builder.Append("            new global::NetLift.LiftMethodRegistration(")
                    .Append(service.Id).Append("u, ").Append(method.Id).Append("u, ")
                    .Append("(payload, context) => Dispatch_").Append(service.Id).Append('_')
                    .Append(method.Id).Append("(payload, context, service_").Append(service.Id).AppendLine(")),");
            }
            builder.AppendLine("        };")
                .AppendLine("        var subscriptions = new global::NetLift.LiftStreamSubscriptionRegistration[]")
                .AppendLine("        {");
            foreach (var (service, stream) in subscriptions)
            {
                builder.Append("            new global::NetLift.LiftStreamSubscriptionRegistration(")
                    .Append(service.Id).Append("u, ").Append(stream.Id).Append("u, ")
                    .Append("(payload, context) => DispatchStream_").Append(service.Id).Append('_')
                    .Append(stream.Id).Append("(payload, context, service_").Append(service.Id).AppendLine(")),");
            }
            builder.AppendLine("        };")
                .AppendLine("        return registry.TryRegisterV4Batch(AbiVersion, RequiredFeatureBits, SchemaHashBytes, methods, subscriptions);")
                .AppendLine("    }");
        }

        builder.AppendLine()
            .AppendLine("    private static global::NetLift.LiftDispatchResult MapNativeFailure(")
            .AppendLine("        global::NetLift.LiftNativeServiceException exception)")
            .AppendLine("    {")
            .AppendLine("        var status = exception.StatusCode switch")
            .AppendLine("        {")
            .AppendLine("            3u => global::NetLift.LiftDispatchStatus.Unsupported,")
            .AppendLine("            7u => global::NetLift.LiftDispatchStatus.MalformedPayload,")
            .AppendLine("            11u => global::NetLift.LiftDispatchStatus.Unavailable,")
            .AppendLine("            13u => global::NetLift.LiftDispatchStatus.HandlerFailure,")
            .AppendLine("            _ => global::NetLift.LiftDispatchStatus.ManagedFailure")
            .AppendLine("        };")
            .AppendLine("        if (!exception.DiagnosticUtf8.IsEmpty &&")
            .AppendLine("            !global::NetLift.LiftNativeReplyDecoder.IsValidDiagnostic(exception.DiagnosticUtf8))")
            .AppendLine("            return global::NetLift.LiftDispatchResult.Failure(global::NetLift.LiftDispatchStatus.MalformedPayload);")
            .AppendLine("        return global::NetLift.LiftDispatchResult.Failure(status, exception.DiagnosticUtf8);")
            .AppendLine("    }");

        foreach (var (service, method) in methods)
            EmitHostDispatcher(builder, service, method, hasV4Contract);

        if (hasV4Contract)
            foreach (var (service, stream) in subscriptions)
                EmitHostStreamDispatcher(builder, service, stream);

        builder.AppendLine("    internal static global::NetLift.LiftBootstrapStatus ValidateDescriptor(")
            .AppendLine("        global::NetLift.ILiftDispatchRegistry registry) =>")
            .AppendLine("        registry.ValidateDescriptor(DescriptorVersion, AbiVersion, RequiredFeatureBits, SchemaHashBytes);")
            .AppendLine("}")
            .AppendLine("public static unsafe class AndroidContractBootstrap")
            .AppendLine("{")
            .AppendLine("    public static uint DescribeV1(global::NetLift.LiftContractDescriptorV1* output, uint outputCapacity)")
            .AppendLine("    {")
            .AppendLine("        if (output is null) return (uint)global::NetLift.LiftBootstrapStatus.InvalidArgument;")
            .AppendLine("        if (outputCapacity < (uint)sizeof(global::NetLift.LiftContractDescriptorV1))")
            .AppendLine("            return (uint)global::NetLift.LiftBootstrapStatus.BufferTooSmall;")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("        var descriptor = default(global::NetLift.LiftContractDescriptorV1);")
            .AppendLine("        descriptor.StructSize = (uint)sizeof(global::NetLift.LiftContractDescriptorV1);")
            .AppendLine("        descriptor.DescriptorVersion = 1u;")
            .AppendLine("        descriptor.AbiVersion = 1u;")
            .Append("        descriptor.RequiredFeatureBits = ").Append(requiredFeatureBits).AppendLine("u;")
            .AppendLine("        byte* hash = descriptor.SchemaHash;")
            .AppendLine("        global::System.Runtime.InteropServices.Marshal.Copy(ContractModule.SchemaHashBytes, 0, (global::System.IntPtr)hash, 32);")
            .AppendLine("        *output = descriptor;")
            .AppendLine("        return (uint)global::NetLift.LiftBootstrapStatus.Success;")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception)")
            .AppendLine("        {")
            .AppendLine("            return (uint)global::NetLift.LiftBootstrapStatus.ManagedFailure;")
            .AppendLine("        }")
            .AppendLine("    }");
        if (hasV4Contract)
        {
            builder.AppendLine()
                .AppendLine("    public static uint RegisterV4()")
                .AppendLine("    {")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            if (!global::NetLift.LiftGeneratedHost.TryGetRegistry(out var registry))")
                .AppendLine("                return (uint)global::NetLift.LiftBootstrapStatus.NotReady;")
                .AppendLine("            if (registry is not global::NetLift.ILiftV4DispatchRegistry v4Registry)")
                .AppendLine("                return (uint)global::NetLift.LiftBootstrapStatus.FeatureMismatch;")
                .AppendLine("            var validation = ContractModule.ValidateDescriptor(registry);")
                .AppendLine("            if (validation != global::NetLift.LiftBootstrapStatus.Success) return (uint)validation;")
                .AppendLine("            var factories = LiftApplicationBootstrap.CreateServiceFactories();")
                .AppendLine("            return (uint)ContractModule.RegisterV4(v4Registry, factories);")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception)")
                .AppendLine("        {")
                .AppendLine("            return (uint)global::NetLift.LiftBootstrapStatus.ManagedFailure;")
                .AppendLine("        }")
                .AppendLine("    }");
        }
        else
        {
            builder.AppendLine()
                .AppendLine("    public static uint RegisterV1()")
                .AppendLine("    {")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            if (!global::NetLift.LiftGeneratedHost.TryGetRegistry(out var registry))")
                .AppendLine("                return (uint)global::NetLift.LiftBootstrapStatus.NotReady;")
                .AppendLine("            var validation = ContractModule.ValidateDescriptor(registry);")
                .AppendLine("            if (validation != global::NetLift.LiftBootstrapStatus.Success) return (uint)validation;")
                .AppendLine("            var factories = LiftApplicationBootstrap.CreateServiceFactories();")
                .AppendLine("            return (uint)ContractModule.Register(registry, factories);")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception)")
                .AppendLine("        {")
                .AppendLine("            return (uint)global::NetLift.LiftBootstrapStatus.ManagedFailure;")
                .AppendLine("        }")
                .AppendLine("    }");
        }
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static void EmitHostDispatcher(
        StringBuilder builder,
        ServiceData service,
        MethodData method,
        bool appScopedService)
    {
        var inputDtoId = method.Parameters[0].Type.TypeId!.Value;
        var outputDtoId = method.ReturnType!.TypeId!.Value;
        var serviceType = service.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var invocation = "global::NetLift.Generated.Dispatch.Dispatch_" + service.Id + "_" + method.Id +
            "(service, request" + (method.SupportsCancellation ? ", context.CancellationToken" : "") + ")";

        builder.AppendLine()
            .Append("    private static async global::System.Threading.Tasks.ValueTask<global::NetLift.LiftDispatchResult> Dispatch_")
            .Append(service.Id).Append('_').Append(method.Id)
            .AppendLine("(global::System.ReadOnlyMemory<byte> payload,")
            .Append("        global::NetLift.LiftInvocationContext context, ")
            .Append(appScopedService ? serviceType : "ILiftServiceFactories")
            .AppendLine(appScopedService ? " service)" : " factories)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {");
        if (!method.IsAsync)
            builder.AppendLine("            await global::System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);");

        builder
            .AppendLine("            if (payload.Length > global::NetLift.Generated.Codec.MaxBufferBytes)")
            .AppendLine("                return global::NetLift.LiftDispatchResult.Failure(global::NetLift.LiftDispatchStatus.BufferTooLarge);")
            .Append("            var decodeStatus = global::NetLift.Generated.Codec.TryDecode_Dto_")
            .Append(inputDtoId)            .AppendLine("(payload.ToArray(), out var request);")
            .AppendLine("            if (decodeStatus != global::NetLift.Generated.CodecStatus.Success || request is null)")
            .AppendLine("                return global::NetLift.LiftDispatchResult.Failure(decodeStatus == global::NetLift.Generated.CodecStatus.BufferTooLarge")
            .AppendLine("                    ? global::NetLift.LiftDispatchStatus.BufferTooLarge")
            .AppendLine("                    : global::NetLift.LiftDispatchStatus.MalformedPayload);");
        if (!appScopedService)
        {
            builder.Append("            ").Append(serviceType).Append(" service = factories.CreateService_")
                .Append(service.Id).AppendLine("(context);")
                .AppendLine("            if (service is null)")
                .AppendLine("                return global::NetLift.LiftDispatchResult.Failure(global::NetLift.LiftDispatchStatus.Unavailable);");
        }

        if (appScopedService)
            builder.AppendLine("            using var invocationScope = global::NetLift.LiftInvocationContextAccessor.Push(context);");
        if (method.IsAsync)
            builder.Append("            var result = await ").Append(invocation).AppendLine(".ConfigureAwait(false);");
        else
            builder.Append("            var result = ").Append(invocation).AppendLine(";");

        builder.Append("            var encodeStatus = global::NetLift.Generated.Codec.TryEncode_Dto_")
            .Append(outputDtoId).AppendLine("(result, out var encoded);")
            .AppendLine("            if (encodeStatus != global::NetLift.Generated.CodecStatus.Success || encoded is null)")
            .AppendLine("                return global::NetLift.LiftDispatchResult.Failure(encodeStatus == global::NetLift.Generated.CodecStatus.BufferTooLarge")
            .AppendLine("                    ? global::NetLift.LiftDispatchStatus.BufferTooLarge")
            .AppendLine("                    : global::NetLift.LiftDispatchStatus.InvalidArgument);")
            .AppendLine("            return global::NetLift.LiftDispatchResult.Success(encoded);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.OperationCanceledException)")
            .AppendLine("        {")
            .AppendLine("            return global::NetLift.LiftDispatchResult.Failure(global::NetLift.LiftDispatchStatus.Cancelled);")
            .AppendLine("        }")
            .AppendLine("        catch (global::NetLift.LiftNativeServiceException exception)")
            .AppendLine("        {")
            .AppendLine("            return MapNativeFailure(exception);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception)")
            .AppendLine("        {")
            .AppendLine("            return global::NetLift.LiftDispatchResult.Failure(global::NetLift.LiftDispatchStatus.ManagedFailure);")
            .AppendLine("        }")
            .AppendLine("    }");
    }

    private static void EmitHostStreamDispatcher(
        StringBuilder builder,
        ServiceData service,
        StreamData stream)
    {
        var serviceType = service.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var itemTypeId = stream.ItemType.TypeId!.Value;
        var invocation = "global::NetLift.Generated.StreamDispatch.DispatchStream_" +
            service.Id + "_" + stream.Id + "(service" +
            (stream.SupportsCancellation ? ", context.CancellationToken" : "") + ")";

        builder.AppendLine()
            .Append("    private static async global::System.Collections.Generic.IAsyncEnumerable<global::System.ReadOnlyMemory<byte>> DispatchStream_")
            .Append(service.Id).Append('_').Append(stream.Id)
            .AppendLine("(global::System.ReadOnlyMemory<byte> payload,")
            .Append("        global::NetLift.LiftInvocationContext context, ").Append(serviceType).AppendLine(" service)")
            .AppendLine("    {")
            .AppendLine("        if (!payload.IsEmpty)")
            .AppendLine("            throw new global::System.IO.InvalidDataException(\"This stream does not accept payload parameters.\");")
            .AppendLine("        using var invocationScope = global::NetLift.LiftInvocationContextAccessor.Push(context);")
            .Append("        var source = ").Append(invocation).AppendLine(";")
            .AppendLine("        await using var iterator = source.GetAsyncEnumerator(context.CancellationToken);")
            .AppendLine("        while (await iterator.MoveNextAsync().ConfigureAwait(false))")
            .AppendLine("        {")
            .Append("            var encodeStatus = global::NetLift.Generated.Codec.TryEncode_Dto_")
            .Append(itemTypeId).AppendLine("(iterator.Current, out var encoded);")
            .AppendLine("            if (encodeStatus != global::NetLift.Generated.CodecStatus.Success || encoded is null)")
            .AppendLine("                throw new global::System.InvalidOperationException(\"Managed stream item could not be encoded.\");")
            .AppendLine("            yield return encoded;")
            .AppendLine("        }")
            .AppendLine("    }");
    }

    private static uint GetHostFeatureBits(IEnumerable<ServiceData> services)
    {
        var methods = services.SelectMany(service => service.Methods).ToArray();
        var bits = methods.Any(method => !method.IsAsync)
            ? 0x00000001u
            : 0u;
        if (methods.Any(method => method.IsAsync))
            bits |= 0x00000002u;
        if (services.Any(service => service.ReplyProtocol == "typed-v1"))
            bits |= 0x00000004u;
        if (services.Any(service => service.Streams.Count != 0 || service.Events.Count != 0))
            bits |= 0x00000008u;
        return bits;
    }

    private static ServiceData ParseService(
        SourceProductionContext context,
        INamedTypeSymbol symbol,
        IReadOnlyDictionary<INamedTypeSymbol, DtoData> dtoBySymbol,
        bool isNative)
    {
        var attribute = FindAttribute(symbol, isNative ? NativeServiceAttribute : ServiceAttribute)!;
        var name = StringConstructorArgument(attribute);
        var id = NamedIntArgument(attribute, "Id");
        var version = NamedIntArgument(attribute, "Version", 1);
        var replyProtocol = isNative ? NamedIntArgument(attribute, "ReplyProtocol") switch
        {
            0 => null,
            1 => "typed-v1",
            _ => "unsupported"
        } : null;
        if (symbol.TypeKind != TypeKind.Interface)
        {
            Report(context, symbol, isNative
                ? "[LiftNativeService] is only valid on interfaces."
                : "[LiftService] is only valid on interfaces.");
        }
        if (id <= 0 || string.IsNullOrWhiteSpace(name) || version <= 0)
        {
            Report(context, symbol, "A service requires a non-empty name, positive Id, and positive Version.");
        }
        if (replyProtocol == "unsupported")
            Report(context, symbol, "Unsupported LiftNativeService reply protocol.");

        var methods = new List<MethodData>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>()
                     .Where(method => FindAttribute(method, MethodAttribute) is not null))
        {
            var methodAttribute = FindAttribute(method, MethodAttribute)!;
            var methodId = ConstructorIntArgument(methodAttribute);
            var location = method.Locations.FirstOrDefault();
            if (method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod || method.ReturnsByRef || method.ReturnsByRefReadonly ||
                method.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
            {
                Report(context, method, "Lift methods must be non-generic ordinary methods without ref, in, or out parameters.");
                continue;
            }
            if (methodId <= 0)
            {
                Report(context, method, "A lift method requires a positive explicit method ID.");
                continue;
            }

            var supported = true;
            var isAsync = TryGetTaskResult(method.ReturnType, out var taskResult);
            var isTaskReturn = IsTaskType(method.ReturnType);
            if (isNative && !isAsync)
            {
                Report(context, method, "A [LiftNativeService] method must return Task<T>.");
                supported = false;
            }

            ITypeSymbol returnSymbol = method.ReturnType;
            if (isAsync)
                returnSymbol = taskResult;
            else if (isTaskReturn)
            {
                context.ReportDiagnostic(Diagnostic.Create(Unsupported, location,
                    method.ReturnType.ToDisplayString()));
                supported = false;
            }

            var cancellationParameters = method.Parameters
                .Where(parameter => IsCancellationToken(parameter.Type))
                .ToArray();
            var validCancellation = cancellationParameters.Length <= 1 &&
                (cancellationParameters.Length == 0 ||
                 cancellationParameters[0].Ordinal == method.Parameters.Length - 1);
            if (!validCancellation || (cancellationParameters.Length != 0 && !isAsync))
            {
                Report(context, method,
                    "CancellationToken is supported only once as the final parameter of an async Task<T> method.");
                supported = false;
            }

            var parameters = new List<ParameterData>();
            foreach (var parameter in method.Parameters.Where(parameter => !IsCancellationToken(parameter.Type)))
            {
                if (!TryType(parameter.Type, dtoBySymbol, out var type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Unsupported, parameter.Locations.FirstOrDefault(),
                        parameter.Type.ToDisplayString()));
                    supported = false;
                }
                else
                {
                    parameters.Add(new ParameterData(parameter.Name, type, parameter));
                }
            }

            ContractType? returnType = null;
            var returnsVoid = method.ReturnsVoid;
            if ((isAsync || (!returnsVoid && !isTaskReturn)) &&
                !TryType(returnSymbol, dtoBySymbol, out returnType))
            {
                context.ReportDiagnostic(Diagnostic.Create(Unsupported, location, returnSymbol.ToDisplayString()));
                supported = false;
            }
            if (isNative && (parameters.Count != 1 || parameters[0].Type.Kind != "string" ||
                returnType?.Kind != "bool"))
            {
                Report(context, method,
                    "Generated native-service adapters currently support one UTF-8 string parameter and a bool Task<T> result.");
                supported = false;
            }

            var supportsCancellation = validCancellation && cancellationParameters.Length == 1 && isAsync;
            if (!supported)
            {
                continue;
            }

            methods.Add(new MethodData(methodId, method.Name, method, parameters, returnType, returnsVoid,
                isAsync, supportsCancellation));
        }

        var streams = new List<StreamData>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>()
                     .Where(method => FindAttribute(method, StreamAttribute) is not null))
        {
            var streamAttribute = FindAttribute(method, StreamAttribute)!;
            var streamId = ConstructorIntArgument(streamAttribute);
            if (isNative)
            {
                Report(context, method, "[LiftNativeService] streams are not supported by the v4 managed subscription adapter.");
                continue;
            }
            if (method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod ||
                method.ReturnsByRef || method.ReturnsByRefReadonly ||
                method.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
            {
                Report(context, method, "Lift streams must be non-generic ordinary methods without ref, in, or out parameters.");
                continue;
            }
            if (streamId <= 0)
            {
                Report(context, method, "A lift stream requires a positive explicit stream ID.");
                continue;
            }
            if (!TryGetAsyncEnumerableElement(method.ReturnType, out var elementType))
            {
                Report(context, method, "A [LiftStream] method must return IAsyncEnumerable<T>.");
                continue;
            }
            var cancellationParameters = method.Parameters
                .Where(parameter => IsCancellationToken(parameter.Type)).ToArray();
            if (cancellationParameters.Length > 1 ||
                (cancellationParameters.Length == 1 &&
                 cancellationParameters[0].Ordinal != method.Parameters.Length - 1))
            {
                Report(context, method, "A lift stream may have one final CancellationToken parameter.");
                continue;
            }
            var streamParameters = new List<ParameterData>();
            var supported = true;
            foreach (var parameter in method.Parameters.Where(parameter => !IsCancellationToken(parameter.Type)))
            {
                if (!TryType(parameter.Type, dtoBySymbol, out var parameterType))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Unsupported, parameter.Locations.FirstOrDefault(),
                        parameter.Type.ToDisplayString()));
                    supported = false;
                }
                else
                {
                    streamParameters.Add(new ParameterData(parameter.Name, parameterType, parameter));
                }
            }
            if (!TryType(elementType, dtoBySymbol, out var itemType))
            {
                context.ReportDiagnostic(Diagnostic.Create(Unsupported, method.Locations.FirstOrDefault(),
                    elementType.ToDisplayString()));
                supported = false;
            }
            if (supported)
                streams.Add(new StreamData(streamId, method.Name, method, streamParameters, itemType!,
                    cancellationParameters.Length == 1));
        }

        var events = new List<EventData>();
        foreach (var eventSymbol in symbol.GetMembers().OfType<IEventSymbol>()
                     .Where(eventSymbol => FindAttribute(eventSymbol, EventAttribute) is not null))
        {
            var eventAttribute = FindAttribute(eventSymbol, EventAttribute)!;
            var eventId = ConstructorIntArgument(eventAttribute);
            if (isNative)
            {
                Report(context, eventSymbol, "[LiftNativeService] events are not supported by the v4 managed subscription adapter.");
                continue;
            }
            if (eventId <= 0)
            {
                Report(context, eventSymbol, "A lift event requires a positive explicit event ID.");
                continue;
            }
            if (eventSymbol.IsStatic || eventSymbol.AddMethod is null || eventSymbol.RemoveMethod is null ||
                !TryGetActionEventArgument(eventSymbol.Type, out var eventType))
            {
                Report(context, eventSymbol, "A [LiftEvent] must be a non-static event of type Action<T>.");
                continue;
            }
            if (!TryType(eventType, dtoBySymbol, out var contractEventType))
            {
                context.ReportDiagnostic(Diagnostic.Create(Unsupported, eventSymbol.Locations.FirstOrDefault(),
                    eventType.ToDisplayString()));
                continue;
            }
            events.Add(new EventData(eventId, eventSymbol.Name, eventSymbol, contractEventType));
        }

        return new ServiceData(id, name, version, isNative ? "native" : "managed", replyProtocol, symbol,
            methods.OrderBy(method => method.Id).ThenBy(method => method.Name, StringComparer.Ordinal).ToList(),
            streams.OrderBy(stream => stream.Id).ThenBy(stream => stream.Name, StringComparer.Ordinal).ToList(),
            events.OrderBy(eventData => eventData.Id).ThenBy(eventData => eventData.Name, StringComparer.Ordinal).ToList());
    }

    private static ContractRevisionData? ReadContractRevision(IAssemblySymbol assembly)
    {
        var attributes = assembly.GetAttributes()
            .Where(attribute => attribute.AttributeClass?.ToDisplayString() == ContractRevisionAttribute)
            .ToArray();
        if (attributes.Length == 0)
            return null;

        var location = attributes[0].ApplicationSyntaxReference?.GetSyntax().GetLocation();
        if (attributes.Length != 1)
            return new ContractRevisionData(0, 0, location,
                "Only one assembly-level [LiftContractRevision(major, minor)] is allowed.");

        var arguments = attributes[0].ConstructorArguments;
        if (arguments.Length != 2 || arguments[0].Value is not int major ||
            arguments[1].Value is not int minor)
            return new ContractRevisionData(0, 0, location,
                "[LiftContractRevision] requires explicit integer major and minor revisions.");

        return new ContractRevisionData(major, minor, location, null);
    }

    private static ContractProfileData? ReadContractProfile(IAssemblySymbol assembly)
    {
        var attributes = assembly.GetAttributes()
            .Where(attribute => attribute.AttributeClass?.ToDisplayString() == ContractProfileAttribute)
            .ToArray();
        if (attributes.Length == 0)
            return null;

        var location = attributes[0].ApplicationSyntaxReference?.GetSyntax().GetLocation();
        if (attributes.Length != 1)
            return new ContractProfileData(AsyncCallbackV1Profile, location,
                "Only one assembly-level [LiftContractProfile] is allowed.");

        var arguments = attributes[0].ConstructorArguments;
        if (arguments.Length != 1 || arguments[0].Value is not int profile ||
            profile != AsyncCallbackV1Profile)
            return new ContractProfileData(AsyncCallbackV1Profile, location,
                "[LiftContractProfile] requires the supported AsyncCallbackV1 profile.");

        return new ContractProfileData(profile, location, null);
    }

    private static DtoData ParseDto(INamedTypeSymbol symbol)
    {
        var attribute = FindAttribute(symbol, DtoAttribute)!;
        var fields = symbol.GetMembers().OfType<IPropertySymbol>()
            .Where(property => !property.IsStatic &&
                (property.GetMethod?.DeclaredAccessibility == Accessibility.Public ||
                 property.SetMethod?.DeclaredAccessibility == Accessibility.Public))
            .Select(property =>
            {
                var fieldAttribute = FindAttribute(property, FieldAttribute);
                if (fieldAttribute is null)
                    return new FieldData(0, property.Name, property, null, false, false, null);

                var id = ConstructorIntArgument(fieldAttribute);
                var type = TryType(property.Type, new Dictionary<INamedTypeSymbol, DtoData>(NamedTypeSymbolComparer.Instance), out var scalar)
                    ? scalar
                    : null;
                var optional = false;
                var hasDefault = false;
                object? defaultValue = null;
                foreach (var argument in fieldAttribute.NamedArguments)
                {
                    if (argument.Key == "Optional" && argument.Value.Value is bool optionalValue)
                        optional = optionalValue;
                    else if (argument.Key == "DefaultValue")
                    {
                        hasDefault = true;
                        defaultValue = argument.Value.Value;
                    }
                }
                return new FieldData(id, property.Name, property, type, optional, hasDefault, defaultValue);
            })
            .OrderBy(field => field.Id)
            .ThenBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

        var accountedFieldSymbols = fields.Select(field => field.Symbol)
            .ToHashSet(SymbolEqualityComparer.Default);
        var unaccountedAnnotatedFields = symbol.GetMembers().OfType<IPropertySymbol>()
            .Where(property => FindAttribute(property, FieldAttribute) is not null &&
                !accountedFieldSymbols.Contains(property))
            .ToArray();

        return new DtoData(NamedIntArgument(attribute, "Id"), StringConstructorArgument(attribute),
            NamedIntArgument(attribute, "Version", 1), symbol, fields, unaccountedAnnotatedFields,
            FindCodecConstructor(symbol, fields));
    }

    private static IParameterSymbol[]? FindCodecConstructor(INamedTypeSymbol symbol, FieldData[] fields)
    {
        foreach (var constructor in symbol.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or
                Accessibility.ProtectedOrInternal) || constructor.Parameters.Length != fields.Length)
                continue;

            var matchedFields = new HashSet<string>(StringComparer.Ordinal);
            var matches = constructor.Parameters.All(parameter =>
            {
                var field = FindFieldForParameter(fields, parameter);
                return field is not null && matchedFields.Add(field.Name);
            });
            if (matches && matchedFields.Count == fields.Length)
                return constructor.Parameters.ToArray();
        }

        return null;
    }

    private static FieldData? FindFieldForParameter(FieldData[] fields, IParameterSymbol parameter)
    {
        var field = fields.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Name, parameter.Name) &&
            SymbolEqualityComparer.Default.Equals(candidate.Symbol.Type, parameter.Type));
        if (field is not null)
            return field;

        var candidates = fields.Where(candidate =>
                StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, parameter.Name) &&
                SymbolEqualityComparer.Default.Equals(candidate.Symbol.Type, parameter.Type))
            .Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool ValidateDtos(SourceProductionContext context, IEnumerable<DtoData> dtos)
    {
        var valid = true;
        void Invalid(ISymbol symbol, string message)
        {
            Report(context, symbol, message);
            valid = false;
        }

        foreach (var dto in dtos)
        {
            if (!dto.Symbol.IsRecord)
                Invalid(dto.Symbol, "A [LiftDto] must be an immutable record.");
            if (dto.Id <= 0 || string.IsNullOrWhiteSpace(dto.Name) || dto.Version <= 0)
            {
                Invalid(dto.Symbol, "A DTO requires a non-empty name, positive explicit Id, and positive Version.");
            }
            if (dto.Fields.Length == 0)
            {
                Invalid(dto.Symbol, "A [LiftDto] must declare at least one [LiftField] property.");
            }
            if (dto.CodecConstructorParameters is null)
            {
                Invalid(dto.Symbol,
                    "A [LiftDto] requires an accessible constructor whose parameters match its [LiftField] properties.");
            }

            var usedIds = new HashSet<int>();
            foreach (var field in dto.Fields)
            {
                if (FindAttribute(field.Symbol, FieldAttribute) is null)
                {
                    Invalid(field.Symbol, $"DTO property '{field.Name}' requires an explicit [LiftField] ID.");
                }
                else if (field.Id <= 0 || !usedIds.Add(field.Id))
                {
                    Invalid(field.Symbol, $"DTO '{dto.Name}' has a missing, non-positive, or duplicate field ID '{field.Id}'.");
                }
                else if (field.Id > ushort.MaxValue)
                {
                    Invalid(field.Symbol,
                        $"DTO field ID '{field.Id}' exceeds the wire codec limit of {ushort.MaxValue}.");
                }
                if (field.Type is null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Unsupported, field.Symbol.Locations.FirstOrDefault(),
                        field.Symbol.Type.ToDisplayString()));
                    valid = false;
                }
                if (field.Symbol.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                    field.Symbol.SetMethod is { IsInitOnly: false })
                {
                    Invalid(field.Symbol, $"DTO property '{field.Name}' must have a public getter and no mutable setter.");
                }
            }
            foreach (var field in dto.Symbol.GetMembers().OfType<IFieldSymbol>()
                         .Where(field => !field.IsStatic && !field.IsImplicitlyDeclared &&
                             field.DeclaredAccessibility == Accessibility.Public))
                Invalid(field, $"DTO field '{field.Name}' is unsupported; use a property with [LiftField].");
        }
        return valid;
    }

    private static bool ValidateIds(
        SourceProductionContext context,
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos)
    {
        var valid = true;
        void Invalid(ISymbol symbol, string message)
        {
            Report(context, symbol, message);
            valid = false;
        }

        foreach (var group in services.GroupBy(service => service.Id).Where(group => group.Key > 0 && group.Count() > 1))
        {
            foreach (var service in group)
                Invalid(service.Symbol, $"Duplicate service ID '{group.Key}'.");
        }
        foreach (var group in dtos.GroupBy(dto => dto.Id).Where(group => group.Key > 0 && group.Count() > 1))
        {
            foreach (var dto in group)
                Invalid(dto.Symbol, $"Duplicate DTO ID '{group.Key}'.");
        }
        foreach (var group in services.GroupBy(service => service.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            foreach (var service in group)
                Invalid(service.Symbol, $"Duplicate service name '{group.Key}'.");
        }
        foreach (var group in dtos.GroupBy(dto => dto.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            foreach (var dto in group)
                Invalid(dto.Symbol, $"Duplicate DTO name '{group.Key}'.");
        }
        foreach (var service in services)
        {
            var ids = service.Methods.Select(method => method.Id)
                .Concat(service.Streams.Select(stream => stream.Id))
                .Concat(service.Events.Select(eventData => eventData.Id));
            foreach (var id in ids.GroupBy(id => id).Where(group => group.Count() > 1))
                Invalid(service.Symbol, $"Service '{service.Name}' has duplicate member ID '{id.Key}'.");
            foreach (var group in service.Methods.GroupBy(method => method.Id).Where(group => group.Count() > 1))
            {
                foreach (var method in group)
                    Invalid(method.Symbol, $"Service '{service.Name}' has duplicate method ID '{group.Key}'.");
            }
        }
        return valid;
    }

    private static bool ValidateV5Contract(
        SourceProductionContext context,
        ContractRevisionData revision,
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos,
        bool hostModuleEnabled)
    {
        var valid = true;
        void Invalid(ISymbol? symbol, string message) =>
            context.ReportDiagnostic(Diagnostic.Create(InvalidContract,
                symbol?.Locations.FirstOrDefault() ?? revision.Location ?? Location.None, message));

        if (revision.Major <= 0 || revision.Minor < 0)
        {
            Invalid(null, "[LiftContractRevision] requires a positive major and a non-negative minor revision.");
            valid = false;
        }
        if (hostModuleEnabled)
        {
            Invalid(null, "The schema-v5 manifest profile cannot be combined with [LiftHostModule].");
            valid = false;
        }

        if (services.Count != 1)
        {
            Invalid(null, "The schema-v5 generator profile supports exactly one [LiftService] route and no native services.");
            return false;
        }
        if (dtos.Count != 1)
        {
            Invalid(services[0].Symbol,
                "The schema-v5 generator profile supports exactly one [LiftDto] used by its route.");
            return false;
        }

        var service = services[0];
        var dto = dtos[0];
        if (service.Kind != "managed" || service.Methods.Count != 1 ||
            service.Streams.Count != 0 || service.Events.Count != 0)
        {
            Invalid(service.Symbol,
                "The schema-v5 generator profile supports one managed service with one [LiftMethod], no streams, and no events.");
            valid = false;
        }
        if (!IsV5Identifier(service.Name))
        {
            Invalid(service.Symbol, "Schema-v5 service names must use ASCII identifier characters.");
            valid = false;
        }
        if (service.Methods.Count == 1)
        {
            var method = service.Methods[0];
            if (!IsV5Identifier(method.Name))
            {
                Invalid(method.Symbol, "Schema-v5 method names must use ASCII identifier characters.");
                valid = false;
            }
            if (!method.ReturnsVoid || method.IsAsync || method.SupportsCancellation ||
                method.Parameters.Count != 1 || method.Parameters[0].Type.Kind != "dto" ||
                method.Parameters[0].Type.Nullable || method.Parameters[0].Type.TypeId != dto.Id)
            {
                Invalid(method.Symbol,
                    "The schema-v5 generator profile supports a synchronous void method with exactly one parameter of the sole [LiftDto] type.");
                valid = false;
            }
        }

        var dtoValid = ValidateV5Dto(context, revision, dto);
        return valid && dtoValid;
    }

    private static bool ValidateV5Dto(
        SourceProductionContext context,
        ContractRevisionData revision,
        DtoData dto)
    {
        var valid = true;
        void Invalid(ISymbol? symbol, string message) =>
            context.ReportDiagnostic(Diagnostic.Create(InvalidContract,
                symbol?.Locations.FirstOrDefault() ?? revision.Location ?? Location.None, message));

        if (!IsV5Identifier(dto.Name))
        {
            Invalid(dto.Symbol, "Schema-v5 DTO names must use ASCII identifier characters.");
            valid = false;
        }
        if (dto.Fields.Length is 0 or > 64)
        {
            Invalid(dto.Symbol, "Schema-v5 DTOs must contain between 1 and 64 flat fields.");
            valid = false;
        }
        if (dto.UnaccountedAnnotatedFields.Length != 0)
        {
            foreach (var field in dto.UnaccountedAnnotatedFields)
                Invalid(field,
                    $"Annotated [LiftField] property '{field.Name}' must have a public getter and be included in the immutable DTO contract.");
            valid = false;
        }

        foreach (var field in dto.Fields)
        {
            if (field.Id <= 0 || field.Id > ushort.MaxValue)
            {
                Invalid(field.Symbol, "Schema-v5 field IDs must be positive uint16 values.");
                valid = false;
            }
            if (!IsV5Identifier(field.Name))
            {
                Invalid(field.Symbol, "Schema-v5 field names must use ASCII identifier characters.");
                valid = false;
            }
            var type = field.Type;
            if (type is null || type.Kind is not ("string" or "int32" or "bool") ||
                (type.Nullable && type.Kind != "string"))
            {
                Invalid(field.Symbol,
                    "Schema-v5 supports only string, int, and bool DTO fields; only string may be nullable.");
                valid = false;
                continue;
            }

            if (field.Optional && !field.HasDefault)
            {
                Invalid(field.Symbol, "An optional schema-v5 field requires an explicit DefaultValue.");
                valid = false;
                continue;
            }
            if (!field.Optional && field.HasDefault)
            {
                Invalid(field.Symbol, "DefaultValue is valid only when Optional = true in the schema-v5 profile.");
                valid = false;
                continue;
            }
            if (!field.Optional)
                continue;

            var validDefault = field.DefaultValue switch
            {
                string when type.Kind == "string" => true,
                null when type.Kind == "string" => type.Nullable,
                int when type.Kind == "int32" => true,
                bool when type.Kind == "bool" => true,
                _ => false
            };
            if (!validDefault)
            {
                Invalid(field.Symbol,
                    "Schema-v5 DefaultValue must match the field type (null requires nullable string).");
                valid = false;
                continue;
            }

            if (field.DefaultValue is string text)
            {
                try
                {
                    _ = new UTF8Encoding(false, true).GetByteCount(text);
                }
                catch (EncoderFallbackException)
                {
                    Invalid(field.Symbol, "Schema-v5 string defaults must be valid UTF-8.");
                    valid = false;
                }
            }
        }

        return valid;
    }

    private static bool ValidateAsyncCallbackV5Contract(
        SourceProductionContext context,
        ContractRevisionData revision,
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos,
        bool hostModuleEnabled)
    {
        var valid = true;
        void Invalid(ISymbol? symbol, string message) =>
            context.ReportDiagnostic(Diagnostic.Create(InvalidContract,
                symbol?.Locations.FirstOrDefault() ?? revision.Location ?? Location.None, message));

        if (revision.Major <= 0 || revision.Minor < 0)
        {
            Invalid(null, "[LiftContractRevision] requires a positive major and a non-negative minor revision.");
            valid = false;
        }
        if (hostModuleEnabled)
        {
            Invalid(null, "The schema-v5 async-callback profile cannot be combined with [LiftHostModule].");
            valid = false;
        }

        var managedServices = services.Where(service => service.Kind == "managed").ToArray();
        var nativeServices = services.Where(service => service.Kind == "native").ToArray();
        if (managedServices.Length != 1 || nativeServices.Length != 1 || services.Count != 2)
        {
            Invalid(null,
                "The AsyncCallbackV1 profile requires exactly one managed [LiftService] and one typed native [LiftNativeService].");
            return false;
        }
        if (dtos.Count != 2)
        {
            Invalid(managedServices[0].Symbol,
                "The AsyncCallbackV1 profile requires exactly two [LiftDto] types: request and result.");
            return false;
        }

        var managed = managedServices[0];
        var native = nativeServices[0];
        if (managed.Methods.Count != 1 || managed.Streams.Count != 0 || managed.Events.Count != 0)
        {
            Invalid(managed.Symbol,
                "The AsyncCallbackV1 profile requires one managed [LiftMethod] and no streams or events.");
            valid = false;
        }
        if (native.Methods.Count != 1 || native.Streams.Count != 0 || native.Events.Count != 0 ||
            native.ReplyProtocol != "typed-v1")
        {
            Invalid(native.Symbol,
                "The AsyncCallbackV1 profile requires one native [LiftMethod], typed-v1 replies, no streams, and no events.");
            valid = false;
        }
        if (!IsV5Identifier(managed.Name) || !IsV5Identifier(native.Name))
        {
            Invalid(!IsV5Identifier(managed.Name) ? managed.Symbol : native.Symbol,
                "Schema-v5 service names must use ASCII identifier characters.");
            valid = false;
        }

        DtoData? requestDto = null;
        DtoData? resultDto = null;
        if (managed.Methods.Count == 1)
        {
            var method = managed.Methods[0];
            if (!IsV5Identifier(method.Name))
            {
                Invalid(method.Symbol, "Schema-v5 method names must use ASCII identifier characters.");
                valid = false;
            }
            if (!method.IsAsync || !method.SupportsCancellation || method.ReturnsVoid ||
                method.Parameters.Count != 1 || method.Parameters[0].Type.Kind != "dto" ||
                method.Parameters[0].Type.Nullable || method.ReturnType is not { Kind: "dto", Nullable: false })
            {
                Invalid(method.Symbol,
                    "AsyncCallbackV1 requires a cancellable Task<ResultDto> method with one non-null request DTO parameter.");
                valid = false;
            }
            else
            {
                requestDto = dtos.FirstOrDefault(dto => dto.Id == method.Parameters[0].Type.TypeId);
                resultDto = dtos.FirstOrDefault(dto => dto.Id == method.ReturnType.TypeId);
                if (requestDto is null || resultDto is null || requestDto.Id == resultDto.Id)
                {
                    Invalid(method.Symbol,
                        "AsyncCallbackV1 request and result must reference two distinct declared [LiftDto] identities.");
                    valid = false;
                }
                if (requestDto is not null &&
                    !requestDto.Fields.Any(field =>
                        field.Name == "Name" && field.Type is { Kind: "string", Nullable: false } &&
                        !field.Optional))
                {
                    Invalid(method.Symbol,
                        "AsyncCallbackV1 request DTO must contain a required non-null string field named Name for confirmation.");
                    valid = false;
                }
                if (resultDto is not null &&
                    !resultDto.Fields.Any(field =>
                        field.Name == "Accepted" && field.Type is { Kind: "bool", Nullable: false } &&
                        !field.Optional))
                {
                    Invalid(method.Symbol,
                        "AsyncCallbackV1 result DTO must contain a required non-null bool field named Accepted.");
                    valid = false;
                }
            }
        }

        if (native.Methods.Count == 1)
        {
            var method = native.Methods[0];
            if (!IsV5Identifier(method.Name))
            {
                Invalid(method.Symbol, "Schema-v5 method names must use ASCII identifier characters.");
                valid = false;
            }
            if (method.Name != "ConfirmAsync")
            {
                Invalid(method.Symbol,
                    "AsyncCallbackV1 native confirmation method must be named ConfirmAsync.");
                valid = false;
            }
            if (!method.IsAsync || !method.SupportsCancellation || method.ReturnsVoid ||
                method.Parameters.Count != 1 || method.Parameters[0].Type.Kind != "string" ||
                method.Parameters[0].Type.Nullable || method.ReturnType is not { Kind: "bool", Nullable: false })
            {
                Invalid(method.Symbol,
                    "AsyncCallbackV1 confirmation requires typed-v1 Task<bool> ConfirmAsync(string, CancellationToken).");
                valid = false;
            }
        }

        foreach (var dto in dtos)
        {
            var dtoValid = ValidateV5Dto(context, revision, dto);
            valid &= dtoValid;
        }
        return valid;
    }

    private static bool IsV5Identifier(string name) =>
        name.Length != 0 && char.IsAsciiLetter(name[0]) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool TryType(
        ITypeSymbol symbol,
        IReadOnlyDictionary<INamedTypeSymbol, DtoData> dtoBySymbol,
        out ContractType type)
    {
        var nullable = symbol.NullableAnnotation == NullableAnnotation.Annotated;
        if (symbol.SpecialType == SpecialType.System_String)
        {
            type = new ContractType("string", null, nullable);
            return true;
        }
        if (symbol.SpecialType == SpecialType.System_Boolean)
            return Scalar("bool", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_Byte)
            return Scalar("uint8", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_SByte)
            return Scalar("int8", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_Int16)
            return Scalar("int16", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_UInt16)
            return Scalar("uint16", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_Int32)
            return Scalar("int32", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_UInt32)
            return Scalar("uint32", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_Int64)
            return Scalar("int64", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_UInt64)
            return Scalar("uint64", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_Single)
            return Scalar("float32", nullable, out type);
        if (symbol.SpecialType == SpecialType.System_Double)
            return Scalar("float64", nullable, out type);
        if (symbol is INamedTypeSymbol named && dtoBySymbol.TryGetValue(named, out var dto))
        {
            type = new ContractType("dto", dto.Name, nullable, dto.Id);
            return true;
        }

        type = null!;
        return false;
    }

    private static bool TryGetTaskResult(ITypeSymbol symbol, out ITypeSymbol result)
    {
        if (symbol is INamedTypeSymbol named &&
            named.IsGenericType &&
            named.OriginalDefinition.MetadataName == "Task`1" &&
            named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
        {
            result = named.TypeArguments[0];
            return true;
        }

        result = null!;
        return false;
    }

    private static bool TryGetAsyncEnumerableElement(ITypeSymbol symbol, out ITypeSymbol result)
    {
        if (symbol is INamedTypeSymbol named && named.IsGenericType &&
            named.OriginalDefinition.MetadataName == "IAsyncEnumerable`1" &&
            named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System.Collections.Generic")
        {
            result = named.TypeArguments[0];
            return true;
        }
        result = null!;
        return false;
    }

    private static bool TryGetActionEventArgument(ITypeSymbol symbol, out ITypeSymbol result)
    {
        if (symbol is INamedTypeSymbol named && named.IsGenericType &&
            named.OriginalDefinition.MetadataName == "Action`1" &&
            named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System")
        {
            result = named.TypeArguments[0];
            return true;
        }
        result = null!;
        return false;
    }

    private static bool IsTaskType(ITypeSymbol symbol) =>
        symbol is INamedTypeSymbol named &&
        named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
        named.OriginalDefinition.MetadataName is "Task" or "Task`1";

    private static bool IsCancellationToken(ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ==
            "global::System.Threading.CancellationToken";

    private static bool Scalar(string name, bool nullable, out ContractType type)
    {
        type = new ContractType(name, null, nullable);
        return true;
    }

    private static string SerializeSchema(
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos,
        string? hash)
    {
        var schema = new SchemaData(2, hash,
            services.Select(service => new SchemaService(service.Id, service.Name, service.Version, service.Kind,
                service.Methods.Select(method => new SchemaMethod(method.Id, method.Name,
                    method.IsAsync ? "async" : "sync", method.SupportsCancellation,
                    method.Parameters.Select(parameter => new SchemaParameter(parameter.Name, parameter.Type)).ToArray(),
                    method.ReturnType)).ToArray())).ToArray(),
            dtos.Select(dto => new SchemaDto(dto.Id, dto.Name, dto.Version,
                dto.Fields.Select(field => new SchemaField(field.Id, field.Name, field.Type)).ToArray())).ToArray());

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    private static string SerializeTypedSchema(
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos,
        string? hash)
    {
        var schema = new TypedSchemaData(3, hash,
            services.Select(service => new TypedSchemaService(
                service.Id, service.Name, service.Version, service.Kind,
                service.Methods.Select(method => new SchemaMethod(method.Id, method.Name,
                    method.IsAsync ? "async" : "sync", method.SupportsCancellation,
                    method.Parameters.Select(parameter => new SchemaParameter(parameter.Name, parameter.Type)).ToArray(),
                    method.ReturnType)).ToArray(),
                service.ReplyProtocol)).ToArray(),
            dtos.Select(dto => new SchemaDto(dto.Id, dto.Name, dto.Version,
                dto.Fields.Select(field => new SchemaField(field.Id, field.Name, field.Type)).ToArray())).ToArray());

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    private static string SerializeStreamSchema(
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos,
        string? hash)
    {
        var schema = new StreamSchemaData(4, hash,
            services.Select(service => new StreamSchemaService(
                service.Id, service.Name, service.Version, service.Kind,
                service.Methods.Select(method => new SchemaMethod(method.Id, method.Name,
                    method.IsAsync ? "async" : "sync", method.SupportsCancellation,
                    method.Parameters.Select(parameter => new SchemaParameter(parameter.Name, parameter.Type)).ToArray(),
                    method.ReturnType)).ToArray(),
                service.ReplyProtocol,
                service.Streams.Select(stream => new SchemaSubscription(stream.Id, stream.Name, "stream",
                    stream.SupportsCancellation,
                    stream.Parameters.Select(parameter => new SchemaParameter(parameter.Name, parameter.Type)).ToArray(),
                    stream.ItemType)).Concat(service.Events.Select(eventData => new SchemaSubscription(
                    eventData.Id, eventData.Name, "event", false, [], eventData.ItemType))).ToArray())).ToArray(),
            dtos.Select(dto => new SchemaDto(dto.Id, dto.Name, dto.Version,
                dto.Fields.Select(field => new SchemaField(field.Id, field.Name, field.Type)).ToArray())).ToArray());
        return JsonSerializer.Serialize(schema, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    private static string SerializeNegotiationManifest(
        ContractRevisionData revision,
        ServiceData service,
        MethodData method,
        DtoData dto,
        out string hash)
    {
        var schema = new NegotiationSchemaData(
            1,
            5,
            new NegotiationRevisionData((uint)revision.Major, (uint)revision.Minor),
            0x10,
            0,
            true,
            new NegotiationServiceData((uint)service.Id, service.Name, (uint)service.Version),
            new NegotiationMethodData((uint)method.Id, method.Name, "sync", false, (uint)dto.Id),
            new NegotiationDtoData((uint)dto.Id, dto.Name, (uint)dto.Version,
                dto.Fields.Select(field => new NegotiationFieldData(
                    (uint)field.Id,
                    field.Name,
                    field.Type!.Kind switch
                    {
                        "string" => "utf8",
                        "int32" => "i32",
                        "bool" => "bool",
                        _ => throw new InvalidOperationException("Unsupported field reached schema-v5 serialization.")
                    },
                    field.Type.Nullable,
                    field.Optional,
                    field.Optional ? field.DefaultValue : null)).ToArray()));
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
        using var schemaDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(schema, options));
        using var canonicalSchema = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalSchema, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
            WriteCanonicalJson(schemaDocument.RootElement, writer);
        hash = Convert.ToHexString(SHA256.HashData(canonicalSchema.ToArray())).ToLowerInvariant();
        return JsonSerializer.Serialize(new NegotiationManifestData(hash, schema), options);
    }

    private static string SerializeAsyncCallbackManifest(
        ContractRevisionData revision,
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos,
        out string hash)
    {
        var managed = services.Single(service => service.Kind == "managed");
        var native = services.Single(service => service.Kind == "native");
        var method = managed.Methods[0];
        var requestDtoId = (uint)method.Parameters[0].Type.TypeId!.Value;
        var resultDtoId = (uint)method.ReturnType!.TypeId!.Value;
        var schema = new AsyncCallbackNegotiationSchemaData(
            1,
            5,
            "async-callback-v1",
            new NegotiationRevisionData((uint)revision.Major, (uint)revision.Minor),
            0x36,
            0x36,
            true,
            new NegotiationServiceData((uint)managed.Id, managed.Name, (uint)managed.Version),
            new AsyncCallbackManagedMethodData(
                (uint)method.Id, method.Name, "async", true, requestDtoId, resultDtoId),
            [new AsyncCallbackNativeServiceData(
                (uint)native.Id,
                native.Name,
                (uint)native.Version,
                native.ReplyProtocol!,
                [new AsyncCallbackNativeMethodData(
                    (uint)native.Methods[0].Id,
                    native.Methods[0].Name,
                    "async",
                    true,
                    "utf8",
                    "bool")])],
            dtos.OrderBy(dto => dto.Id)
                .Select(dto => new NegotiationDtoData((uint)dto.Id, dto.Name, (uint)dto.Version,
                    dto.Fields.Select(field => new NegotiationFieldData(
                        (uint)field.Id,
                        field.Name,
                        field.Type!.Kind switch
                        {
                            "string" => "utf8",
                            "int32" => "i32",
                            "bool" => "bool",
                            _ => throw new InvalidOperationException(
                                "Unsupported field reached schema-v5 async serialization.")
                        },
                        field.Type.Nullable,
                        field.Optional,
                        field.Optional ? field.DefaultValue : null)).ToArray()))
                .ToArray());
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
        using var schemaDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(schema, options));
        using var canonicalSchema = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalSchema, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
            WriteCanonicalJson(schemaDocument.RootElement, writer);
        hash = Convert.ToHexString(SHA256.HashData(canonicalSchema.ToArray())).ToLowerInvariant();
        return JsonSerializer.Serialize(new AsyncCallbackNegotiationManifestData(hash, schema), options);
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string EmitSource(
        string schema,
        string hash,
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos,
        bool emitDtoCodecs = true,
        bool emitNativeServiceAdapters = true,
        bool asyncCallbackV1Profile = false)
    {
        var builder = new StringBuilder()
            .AppendLine("// <auto-generated/>")
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated;")
            .AppendLine("public static class Contract")
            .AppendLine("{")
            .Append("    public const string CanonicalJson = ")
            .Append(SymbolDisplay.FormatLiteral(schema, quote: true))
            .AppendLine(";")
            .Append("    public const string SchemaHash = ")
            .Append(SymbolDisplay.FormatLiteral(hash, quote: true))
            .AppendLine(";");

        foreach (var service in services)
        {
            builder.Append("    public const uint ServiceId_").Append(service.Id)
                .Append(" = ").Append(service.Id).AppendLine("u;");
            foreach (var method in service.Methods)
                builder.Append("    public const uint MethodId_").Append(service.Id).Append('_')
                    .Append(method.Id).Append(" = ").Append(method.Id).AppendLine("u;");
            foreach (var stream in service.Streams)
                builder.Append("    public const uint StreamId_").Append(service.Id).Append('_')
                    .Append(stream.Id).Append(" = ").Append(stream.Id).AppendLine("u;");
            foreach (var eventData in service.Events)
                builder.Append("    public const uint EventId_").Append(service.Id).Append('_')
                    .Append(eventData.Id).Append(" = ").Append(eventData.Id).AppendLine("u;");
        }

        builder.AppendLine("}");
        if (emitDtoCodecs)
            EmitCodecs(builder, dtos, services);
        builder.AppendLine("public static class Dispatch")
            .AppendLine("{");

        foreach (var service in services.Where(service => service.Kind == "managed"))
        {
            var serviceType = service.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            foreach (var method in service.Methods)
            {
                var returnType = method.ReturnsVoid
                    ? "void"
                    : method.Symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var parameters = string.Join(", ", new[] { serviceType + " service" }.Concat(
                    method.Symbol.Parameters.Select(parameter =>
                        parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " " +
                        EscapeIdentifier(parameter.Name))));
                var invocation = "service." + EscapeIdentifier(method.Symbol.Name) + "(" +
                    string.Join(", ", method.Symbol.Parameters.Select(parameter => EscapeIdentifier(parameter.Name))) + ")";
                builder.Append("    public static ").Append(returnType).Append(" Dispatch_")
                    .Append(service.Id).Append('_').Append(method.Id).Append('(').Append(parameters).AppendLine(")")
                    .AppendLine("    {");
                if (!method.ReturnsVoid)
                    builder.Append("        return ").Append(invocation).AppendLine(";");
                else
                    builder.Append("        ").Append(invocation).AppendLine(";");
                builder.AppendLine("    }");
            }
        }
        builder.AppendLine("}");
        if (services.Any(service => service.Streams.Count != 0 || service.Events.Count != 0))
            EmitStreamDispatch(builder, services);
        if (emitNativeServiceAdapters)
            EmitNativeServiceAdapters(builder, services);
        if (asyncCallbackV1Profile)
            EmitAsyncCallbackV1Host(builder, services, dtos);
        return builder.ToString();
    }

    private static void EmitStreamDispatch(StringBuilder builder, IReadOnlyList<ServiceData> services)
    {
        builder.AppendLine("public static class StreamDispatch")
            .AppendLine("{");
        foreach (var service in services.Where(service => service.Kind == "managed"))
        {
            var serviceType = service.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            foreach (var stream in service.Streams)
            {
                var parameters = string.Join(", ", new[] { serviceType + " service" }.Concat(
                    stream.Symbol.Parameters.Select(parameter =>
                        parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " " +
                        EscapeIdentifier(parameter.Name))));
                var arguments = string.Join(", ", stream.Symbol.Parameters.Select(parameter =>
                    EscapeIdentifier(parameter.Name)));
                var itemType = ((INamedTypeSymbol)stream.Symbol.ReturnType).TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                builder.Append("    public static global::System.Collections.Generic.IAsyncEnumerable<")
                    .Append(itemType).Append("> DispatchStream_").Append(service.Id).Append('_')
                    .Append(stream.Id).Append('(').Append(parameters).AppendLine(")")
                    .Append("        => service.").Append(EscapeIdentifier(stream.Symbol.Name))
                    .Append('(').Append(arguments).AppendLine(");");
            }
            foreach (var eventData in service.Events)
            {
                var itemType = ((INamedTypeSymbol)eventData.Symbol.Type)
                    .TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var handlerType = "global::System.Action<" + itemType + ">";
                var memberName = EscapeIdentifier(eventData.Symbol.Name);
                builder.Append("    public static void AddEvent_").Append(service.Id).Append('_')
                    .Append(eventData.Id).Append('(').Append(serviceType).Append(" service, ")
                    .Append(handlerType).AppendLine(" handler)")
                    .Append("        => service.").Append(memberName).AppendLine(" += handler;");
                builder.Append("    public static void RemoveEvent_").Append(service.Id).Append('_')
                    .Append(eventData.Id).Append('(').Append(serviceType).Append(" service, ")
                    .Append(handlerType).AppendLine(" handler)")
                    .Append("        => service.").Append(memberName).AppendLine(" -= handler;");
            }
        }
        builder.AppendLine("}");
    }

    private static void EmitNativeServiceAdapters(StringBuilder builder, IReadOnlyList<ServiceData> services)
    {
        var methods = services.Where(service => service.Kind == "native")
            .SelectMany(service => service.Methods.Select(method => (Service: service, Method: method)))
            .ToArray();
        if (methods.Length == 0)
            return;

        builder.AppendLine("public static class NativeServiceAdapters")
            .AppendLine("{");
        foreach (var (service, method) in methods)
        {
            var parameter = method.Parameters[0];
            var parameterType = parameter.Symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var returnType = method.Symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var taskResultType = ((INamedTypeSymbol)method.Symbol.ReturnType).TypeArguments[0]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            builder.Append("    public static ").Append(returnType).Append(" Invoke_")
                .Append(service.Id).Append('_').Append(method.Id)
                .Append("(global::System.Func<uint, uint, byte[], global::System.Threading.CancellationToken, ")
                .Append(returnType).Append("> invoke, ")
                .Append(parameterType).Append(' ').Append(EscapeIdentifier(parameter.Name))
                .AppendLine(", global::System.Threading.CancellationToken cancellationToken)")
                .AppendLine("    {")
                .AppendLine("        if (invoke is null) return global::System.Threading.Tasks.Task.FromException<" +
                    taskResultType + ">(new global::System.ArgumentNullException(nameof(invoke)));")
                .Append("        var status = Codec.TryEncodeRequest_").Append(service.Id).Append('_')
                .Append(method.Id).Append('(').Append(EscapeIdentifier(parameter.Name)).AppendLine(", out var payload);")
                .AppendLine("        if (status != CodecStatus.Success)")
                .AppendLine("            return global::System.Threading.Tasks.Task.FromException<" +
                    taskResultType + ">(new CodecException(status));")
                .Append("        return invoke(Contract.ServiceId_").Append(service.Id)
                .Append(", Contract.MethodId_").Append(service.Id).Append('_').Append(method.Id)
                .Append(", payload, cancellationToken);")
                .AppendLine("    }");
        }
        builder.AppendLine("}");
    }

    private static void EmitAsyncCallbackV1Host(
        StringBuilder builder,
        IReadOnlyList<ServiceData> services,
        IReadOnlyList<DtoData> dtos)
    {
        var managed = services.Single(service => service.Kind == "managed");
        var native = services.Single(service => service.Kind == "native");
        var method = managed.Methods[0];
        var nativeMethod = native.Methods[0];
        var requestDto = dtos.Single(dto => dto.Id == method.Parameters[0].Type.TypeId);
        var resultDto = dtos.Single(dto => dto.Id == method.ReturnType!.TypeId);
        var requestType = requestDto.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var resultType = resultDto.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var serviceType = managed.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var nativeServiceType = native.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        builder.AppendLine("public static class AsyncCallbackV1Host")
            .AppendLine("{");
        EmitAsyncCallbackFieldArray(builder, "RequestFields", requestDto.Fields);
        EmitAsyncCallbackFieldArray(builder, "ResultFields", resultDto.Fields);

        builder.AppendLine()
            .Append("    public static global::System.IDisposable Register(")
            .AppendLine("global::NetLift.ILiftAsyncCallbackV1HostRegistry registry,")
            .Append("        ").Append(serviceType).AppendLine(" service)")
            .AppendLine("    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(registry);")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(service);")
            .AppendLine("        var descriptor = global::NetLift.LiftAsyncCallbackV1Wire.CopyDescriptor(registry.Descriptor);")
            .AppendLine("        ValidateBinding(descriptor);")
            .Append("        return registry.RegisterManagedRoute(").Append(managed.Id).Append("u, ")
            .Append(method.Id).AppendLine("u,")
            .AppendLine("            async (payload, invocationContext, cancellationToken) =>")
            .AppendLine("            {")
            .AppendLine("                global::NetLift.LiftAsyncCallbackV1Wire.ValidateInvocation(")
            .AppendLine("                    invocationContext, descriptor, cancellationToken,")
            .Append("                    ").Append(managed.Id).Append("u, ").Append(method.Id).Append("u, ")
            .Append(requestDto.Id).Append("u, ").Append(requestDto.Version).Append("u, ")
            .Append(resultDto.Id).Append("u, ").Append(resultDto.Version).Append("u, ")
            .Append(native.Id).Append("u, ").Append(nativeMethod.Id).AppendLine("u,")
            .AppendLine("                    global::NetLift.Generated.Contract.SchemaHash, RequestFields, ResultFields);")
            .AppendLine("                var request = DecodeRequest(payload, invocationContext);")
            .AppendLine("                var result = await DispatchAsync(request, service, invocationContext, cancellationToken).ConfigureAwait(false);")
            .AppendLine("                return EncodeResult(result, invocationContext);")
            .AppendLine("            }) ?? throw new global::System.InvalidOperationException(")
            .AppendLine("                \"The AsyncCallbackV1 registry returned no route registration.\");")
            .AppendLine("    }");

        EmitAsyncCallbackV1FactoryRegistration(
            builder, serviceType, managed.Id, method.Id, requestDto.Id, requestDto.Version,
            resultDto.Id, resultDto.Version, native.Id, nativeMethod.Id);
        EmitAsyncCallbackV1FactoryHelpers(builder, serviceType, requestType, managed.Id, method.Id);

        builder.AppendLine()
            .Append("    public static ").Append(requestType)
            .AppendLine(" DecodeRequest(global::System.ReadOnlyMemory<byte> payload,")
            .AppendLine("        global::NetLift.LiftInvocationContext invocationContext)")
            .AppendLine("    {")
            .AppendLine("        ValidateInvocationContext(invocationContext, invocationContext.CancellationToken);")
            .AppendLine("        var values = global::NetLift.LiftAsyncCallbackV1Wire.DecodeRequest(")
            .AppendLine("            payload, invocationContext.PeerRequestSchema, RequestFields);")
            .Append("        return new ").Append(requestType).Append('(')
            .AppendLine(EmitDtoConstructorArguments(requestDto,
                field => "(" + field.Symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) +
                    ")values[" + field.Id + "]!"))
            .AppendLine("        );")
            .AppendLine("    }")
            .AppendLine()
            .Append("    public static global::System.ReadOnlyMemory<byte> EncodeResult(")
            .Append(resultType).AppendLine(" result,")
            .AppendLine("        global::NetLift.LiftInvocationContext invocationContext)")
            .AppendLine("    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(result);")
            .AppendLine("        ValidateInvocationContext(invocationContext, invocationContext.CancellationToken);")
            .AppendLine("        var values = new global::System.Collections.Generic.Dictionary<ushort, object?>")
            .AppendLine("        {");
        foreach (var field in resultDto.Fields)
        {
            builder.Append("            [").Append(field.Id).Append("] = result.")
                .Append(EscapeIdentifier(field.Name)).AppendLine(",");
        }
        builder.AppendLine("        };")
            .AppendLine("        return global::NetLift.LiftAsyncCallbackV1Wire.EncodeResult(")
            .AppendLine("            values, invocationContext.ManagedResultSchema, invocationContext.EndpointToken,")
            .AppendLine("            global::NetLift.Generated.Contract.SchemaHash, ResultFields,")
            .Append("            ").Append(managed.Id).Append("u, ").Append(method.Id).Append("u, ")
            .Append(resultDto.Id).Append("u, ").Append(resultDto.Version).AppendLine("u);")
            .AppendLine("    }")
            .AppendLine()
            .Append("    public static async global::System.Threading.Tasks.Task<").Append(resultType)
            .Append(" > DispatchAsync(").Append(requestType).Append(" request, ")
            .Append(serviceType).AppendLine(" service,")
            .AppendLine("        global::NetLift.LiftInvocationContext invocationContext,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(request);")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(service);")
            .AppendLine("        ValidateInvocationContext(invocationContext, cancellationToken);")
            .AppendLine("        using var invocationScope = global::NetLift.LiftInvocationContextAccessor.Push(invocationContext);")
            .Append("        var result = await global::NetLift.Generated.Dispatch.Dispatch_")
            .Append(managed.Id).Append('_').Append(method.Id)
            .AppendLine("(service, request, cancellationToken).ConfigureAwait(false);")
            .AppendLine("        return result;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public static " + nativeServiceType + " CreateConfirmationProxy() => new ConfirmationProxy();")
            .AppendLine()
            .AppendLine("    private static void ValidateBinding(")
            .AppendLine("        global::NetLift.LiftAsyncCallbackV1HostDescriptor descriptor) =>")
            .AppendLine("        global::NetLift.LiftAsyncCallbackV1Wire.ValidateBinding(")
            .Append("            descriptor, ").Append(managed.Id).Append("u, ").Append(method.Id).Append("u, ")
            .Append(requestDto.Id).Append("u, ").Append(requestDto.Version).Append("u, ")
            .Append(resultDto.Id).Append("u, ").Append(resultDto.Version).Append("u, ")
            .Append(native.Id).Append("u, ").Append(nativeMethod.Id).AppendLine("u,")
            .AppendLine("            global::NetLift.Generated.Contract.SchemaHash, RequestFields, ResultFields);")
            .AppendLine()
            .AppendLine("    private static void ValidateInvocationContext(")
            .AppendLine("        global::NetLift.LiftInvocationContext invocationContext,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken) =>")
            .AppendLine("        global::NetLift.LiftAsyncCallbackV1Wire.ValidateInvocationContext(")
            .AppendLine("            invocationContext, cancellationToken,")
            .Append("            ").Append(managed.Id).Append("u, ").Append(method.Id).Append("u, ")
            .Append(requestDto.Id).Append("u, ").Append(requestDto.Version).Append("u, ")
            .Append(resultDto.Id).Append("u, ").Append(resultDto.Version).Append("u, ")
            .Append(native.Id).Append("u, ").Append(nativeMethod.Id).AppendLine("u,")
            .AppendLine("            global::NetLift.Generated.Contract.SchemaHash, RequestFields, ResultFields);")
            .AppendLine()
            .Append("    private sealed class ConfirmationProxy : ").Append(nativeServiceType).AppendLine()
            .AppendLine("    {")
            .Append("        public async ").Append(nativeMethod.Symbol.ReturnType
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(' ')
            .Append(EscapeIdentifier(nativeMethod.Name)).AppendLine("(")
            .AppendLine("            string prompt, global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("        {")
            .AppendLine("            var invocationContext = global::NetLift.LiftInvocationContextAccessor.Current;")
            .AppendLine("            ValidateInvocationContext(invocationContext, cancellationToken);")
            .AppendLine("            if (invocationContext.NativeServices is not global::NetLift.ILiftAsyncCallbackV1NativeServiceInvoker nativeServices)")
            .AppendLine("                throw new global::NetLift.LiftAsyncCallbackV1ContractException(")
            .AppendLine("                    \"The native invoker does not support generation-aware AsyncCallbackV1 calls.\");")
            .AppendLine("            var request = global::NetLift.LiftAsyncCallbackV1Wire.EncodeConfirmationRequest(prompt);")
            .Append("            var reply = await nativeServices.InvokeAsync(")
            .Append(native.Id).Append("u, ").Append(nativeMethod.Id)
            .AppendLine("u, request, invocationContext, cancellationToken).ConfigureAwait(false);")
            .AppendLine("            return global::NetLift.LiftNativeReplyDecoder.GetBooleanResult(reply, cancellationToken);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine("}");
    }

    private static void EmitAsyncCallbackV1FactoryRegistration(
        StringBuilder builder,
        string serviceType,
        int managedServiceId,
        int managedMethodId,
        int requestDtoId,
        int requestDtoVersion,
        int resultDtoId,
        int resultDtoVersion,
        int nativeServiceId,
        int nativeMethodId)
    {
        builder.AppendLine()
            .AppendLine("    public static global::System.IDisposable Register<TLease>(")
            .AppendLine("        global::NetLift.ILiftAsyncCallbackV1HostRegistry registry,")
            .Append("        global::System.Func<global::NetLift.LiftInvocationContext, global::System.Threading.CancellationToken, global::System.Threading.Tasks.ValueTask<(")
            .Append(serviceType).AppendLine(" Service, TLease Lease)>> serviceFactory)")
            .AppendLine("        where TLease : class, global::System.IAsyncDisposable")
            .AppendLine("    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(registry);")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(serviceFactory);")
            .AppendLine("        var descriptor = global::NetLift.LiftAsyncCallbackV1Wire.CopyDescriptor(registry.Descriptor);")
            .AppendLine("        ValidateBinding(descriptor);")
            .Append("        return registry.RegisterManagedRoute(").Append(managedServiceId).Append("u, ")
            .Append(managedMethodId).AppendLine("u,")
            .AppendLine("            async (payload, invocationContext, cancellationToken) =>")
            .AppendLine("            {")
            .AppendLine("                global::NetLift.LiftAsyncCallbackV1Wire.ValidateInvocation(")
            .AppendLine("                    invocationContext, descriptor, cancellationToken,")
            .Append("                    ").Append(managedServiceId).Append("u, ").Append(managedMethodId).Append("u, ")
            .Append(requestDtoId).Append("u, ").Append(requestDtoVersion).Append("u, ")
            .Append(resultDtoId).Append("u, ").Append(resultDtoVersion).Append("u, ")
            .Append(nativeServiceId).Append("u, ").Append(nativeMethodId).AppendLine("u,")
            .AppendLine("                    global::NetLift.Generated.Contract.SchemaHash, RequestFields, ResultFields);")
            .AppendLine("                var request = DecodeRequest(payload, invocationContext);")
            .AppendLine("                var created = await AcquireServiceLeaseAsync(serviceFactory, invocationContext, cancellationToken).ConfigureAwait(false);")
            .AppendLine("                return await DispatchAndDisposeAsync(request, created.Service, created.Lease, invocationContext, cancellationToken).ConfigureAwait(false);")
            .AppendLine("            }) ?? throw new global::System.InvalidOperationException(")
            .AppendLine("                \"The AsyncCallbackV1 registry returned no route registration.\");")
            .AppendLine("    }");
    }

    private static void EmitAsyncCallbackV1FactoryHelpers(
        StringBuilder builder,
        string serviceType,
        string requestType,
        int managedServiceId,
        int managedMethodId)
    {
        builder.AppendLine()
            .Append("    private static async global::System.Threading.Tasks.Task<( ")
            .Append(serviceType).AppendLine(" Service, TLease Lease)> AcquireServiceLeaseAsync<TLease>(")
            .Append("        global::System.Func<global::NetLift.LiftInvocationContext, global::System.Threading.CancellationToken, global::System.Threading.Tasks.ValueTask<(")
            .Append(serviceType).AppendLine(" Service, TLease Lease)>> serviceFactory,")
            .AppendLine("        global::NetLift.LiftInvocationContext invocationContext,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("        where TLease : class, global::System.IAsyncDisposable")
            .AppendLine("    {")
            .Append("        (").Append(serviceType).AppendLine(" Service, TLease Lease) created;")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            created = await serviceFactory(invocationContext, cancellationToken).ConfigureAwait(false);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.OperationCanceledException exception) when (")
            .AppendLine("            cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken)")
            .AppendLine("        {")
            .AppendLine("            throw;")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception exception)")
            .AppendLine("        {")
            .Append("            throw new global::NetLift.LiftAsyncCallbackV1ServiceFactoryException(")
            .Append(SymbolDisplay.FormatLiteral(
                $"The AsyncCallbackV1 per-operation service factory failed for route {managedServiceId}/{managedMethodId}.",
                quote: true))
            .AppendLine(", exception);")
            .AppendLine("        }")
            .AppendLine("        if (created.Lease is null)")
            .Append("            throw new global::NetLift.LiftAsyncCallbackV1InvalidServiceLeaseException(")
            .Append(SymbolDisplay.FormatLiteral(
                $"The AsyncCallbackV1 per-operation service factory returned a null lease for route {managedServiceId}/{managedMethodId}.",
                quote: true))
            .AppendLine(");")
            .AppendLine("        if (created.Service is not null) return created;")
            .Append("        var invalidService = new global::NetLift.LiftAsyncCallbackV1InvalidServiceLeaseException(")
            .Append(SymbolDisplay.FormatLiteral(
                $"The AsyncCallbackV1 per-operation service factory returned a null service for route {managedServiceId}/{managedMethodId}.",
                quote: true))
            .AppendLine(");")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            await created.Lease.DisposeAsync().ConfigureAwait(false);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception exception)")
            .AppendLine("        {")
            .Append("            var disposalFailure = new global::NetLift.LiftAsyncCallbackV1ServiceLeaseDisposalException(")
            .Append(SymbolDisplay.FormatLiteral(
                $"The AsyncCallbackV1 service lease disposal failed for route {managedServiceId}/{managedMethodId}.",
                quote: true))
            .AppendLine(", exception);")
            .AppendLine("            throw new global::System.AggregateException(")
            .Append("                \"The invalid service result and its lease cleanup both failed.\", invalidService, disposalFailure);")
            .AppendLine("        }")
            .AppendLine("        throw invalidService;")
            .AppendLine("    }")
            .AppendLine()
            .Append("    private static async global::System.Threading.Tasks.Task<global::System.ReadOnlyMemory<byte>> DispatchAndDisposeAsync<TLease>(")
            .Append("        ").Append(requestType).Append(" request, ").Append(serviceType).AppendLine(" service, TLease lease,")
            .AppendLine("        global::NetLift.LiftInvocationContext invocationContext,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("        where TLease : class, global::System.IAsyncDisposable")
            .AppendLine("    {")
            .AppendLine("        global::System.ReadOnlyMemory<byte> encoded = default;")
            .AppendLine("        global::System.Exception? operationFailure = null;")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var result = await DispatchAsync(request, service, invocationContext, cancellationToken).ConfigureAwait(false);")
            .AppendLine("            encoded = EncodeResult(result, invocationContext);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception exception)")
            .AppendLine("        {")
            .AppendLine("            operationFailure = exception;")
            .AppendLine("        }")
            .AppendLine("        global::System.Exception? disposalFailure = null;")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            await lease.DisposeAsync().ConfigureAwait(false);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception exception)")
            .AppendLine("        {")
            .Append("            disposalFailure = new global::NetLift.LiftAsyncCallbackV1ServiceLeaseDisposalException(")
            .Append(SymbolDisplay.FormatLiteral(
                $"The AsyncCallbackV1 service lease disposal failed for route {managedServiceId}/{managedMethodId}.",
                quote: true))
            .AppendLine(", exception);")
            .AppendLine("        }")
            .AppendLine("        if (operationFailure is not null && disposalFailure is not null)")
            .AppendLine("            throw new global::System.AggregateException(")
            .Append("                ")
            .Append(SymbolDisplay.FormatLiteral(
                $"The AsyncCallbackV1 operation and service lease cleanup both failed for route {managedServiceId}/{managedMethodId}.",
                quote: true))
            .AppendLine(", operationFailure, disposalFailure);")
            .AppendLine("        if (disposalFailure is not null)")
            .AppendLine("            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalFailure).Throw();")
            .AppendLine("        if (operationFailure is not null)")
            .AppendLine("            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationFailure).Throw();")
            .AppendLine("        return encoded;")
            .AppendLine("    }");
    }

    private static void EmitAsyncCallbackFieldArray(
        StringBuilder builder,
        string name,
        IReadOnlyList<FieldData> fields)
    {
        builder.Append("    private static readonly global::NetLift.LiftAsyncCallbackV1Field[] ")
            .Append(name).AppendLine(" =")
            .AppendLine("    {");
        foreach (var field in fields)
        {
            builder.Append("        new global::NetLift.LiftAsyncCallbackV1Field(")
                .Append(field.Id).Append(", ")
                .Append(field.Type!.Kind switch
                {
                    "string" => "1",
                    "int32" => "2",
                    "bool" => "3",
                    _ => throw new InvalidOperationException("Unsupported AsyncCallbackV1 field reached code generation.")
                }).Append(", ")
                .Append(field.Type.Nullable ? "true" : "false").Append(", ")
                .Append(field.Optional ? "true" : "false").Append(", ")
                .Append(FormatAsyncCallbackDefault(field)).AppendLine("),");
        }
        builder.AppendLine("    };");
    }

    private static string FormatAsyncCallbackDefault(FieldData field)
    {
        if (!field.Optional || field.DefaultValue is null)
            return "null";
        return field.DefaultValue switch
        {
            string text => SymbolDisplay.FormatLiteral(text, quote: true),
            int integer => integer.ToString(CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => throw new InvalidOperationException("Unsupported AsyncCallbackV1 default reached code generation.")
        };
    }

    private static string EmitDtoConstructorArguments(
        DtoData dto,
        Func<FieldData, string> valueExpression)
    {
        return string.Join(", ", dto.CodecConstructorParameters!
            .Select(parameter =>
            {
                var field = FindFieldForParameter(dto.Fields, parameter)!;
                return valueExpression(field);
            }));
    }

    private static void EmitCodecs(
        StringBuilder builder,
        IReadOnlyList<DtoData> dtos,
        IReadOnlyList<ServiceData> services)
    {
        builder.AppendLine("public enum CodecStatus { Success, Malformed, BufferTooLarge, InvalidValue }")
            .AppendLine("public sealed class CodecException : global::System.Exception")
            .AppendLine("{")
            .AppendLine("    public CodecStatus Status { get; }")
            .AppendLine("    public CodecException(CodecStatus status) : base(\"Generated contract codec rejected a value.\") => Status = status;")
            .AppendLine("}")
            .AppendLine("public static class Codec")
            .AppendLine("{")
            .AppendLine("    public const int MaxBufferBytes = 4096;")
            .AppendLine("    private static readonly global::System.Text.UTF8Encoding StrictUtf8 = new(false, true);")
            .AppendLine("    private static void WriteHeader(global::System.Span<byte> output, ref int offset, ushort id, uint length)")
            .AppendLine("    {")
            .AppendLine("        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(offset, 2), id);")
            .AppendLine("        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(offset + 2, 4), length);")
            .AppendLine("        offset += 6;")
            .AppendLine("    }")
            .AppendLine("    private static bool TryReadHeader(global::System.ReadOnlySpan<byte> input, ref int offset, ushort expectedId, out uint length)")
            .AppendLine("    {")
            .AppendLine("        length = 0;")
            .AppendLine("        if (input.Length - offset < 6 || global::System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(offset, 2)) != expectedId)")
            .AppendLine("            return false;")
            .AppendLine("        length = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(offset + 2, 4));")
            .AppendLine("        offset += 6;")
            .AppendLine("        return true;")
            .AppendLine("    }")
            .AppendLine("    private static bool TryReadBytes(global::System.ReadOnlySpan<byte> input, ref int offset, uint length, out global::System.ReadOnlySpan<byte> bytes)")
            .AppendLine("    {")
            .AppendLine("        bytes = default;")
            .AppendLine("        if (length > MaxBufferBytes || length > (uint)(input.Length - offset))")
            .AppendLine("            return false;")
            .AppendLine("        bytes = input.Slice(offset, (int)length);")
            .AppendLine("        offset += (int)length;")
            .AppendLine("        return true;")
            .AppendLine("    }");

        foreach (var dto in dtos.Where(dto => dto.CodecConstructorParameters is not null &&
                     dto.Fields.All(field => field.Type is not null && field.Id is > 0 and <= ushort.MaxValue) &&
                     dto.Fields.Select(field => field.Id).Distinct().Count() == dto.Fields.Length))
            EmitDtoCodec(builder, dto);
        foreach (var (service, method) in services.Where(service => service.Kind == "native")
                     .SelectMany(service => service.Methods.Select(method => (Service: service, Method: method))))
            EmitNativeRequestCodec(builder, service, method);

        builder.AppendLine("}");
    }

    private static void EmitNativeRequestCodec(StringBuilder builder, ServiceData service, MethodData method)
    {
        var parameter = method.Parameters[0];
        var name = EscapeIdentifier(parameter.Name);
        var typeName = parameter.Symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        builder.Append("    public static CodecStatus TryEncodeRequest_").Append(service.Id).Append('_')
            .Append(method.Id).Append('(').Append(typeName).Append(' ').Append(name)
            .AppendLine(", out byte[] encoded)")
            .AppendLine("    {")
            .AppendLine("        encoded = global::System.Array.Empty<byte>();");
        if (!parameter.Type.Nullable)
            builder.Append("        if (").Append(name).AppendLine(" is null) return CodecStatus.InvalidValue;");
        builder.AppendLine("        int fieldLength = -1;")
            .AppendLine("        try")
            .AppendLine("        {")
            .Append("            if (").Append(name).Append(" is not null) fieldLength = StrictUtf8.GetByteCount(")
            .Append(name).AppendLine(");")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Text.EncoderFallbackException)")
            .AppendLine("        {")
            .AppendLine("            return CodecStatus.InvalidValue;")
            .AppendLine("        }")
            .AppendLine("        int encodedLength;")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            encodedLength = checked(6 + global::System.Math.Max(fieldLength, 0));")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.OverflowException)")
            .AppendLine("        {")
            .AppendLine("            return CodecStatus.BufferTooLarge;")
            .AppendLine("        }")
            .AppendLine("        if (encodedLength > MaxBufferBytes) return CodecStatus.BufferTooLarge;")
            .AppendLine("        var buffer = new byte[encodedLength];")
            .AppendLine("        int offset = 0;")
            .Append("        if (").Append(name).AppendLine(" is null)")
            .AppendLine("        {")
            .AppendLine("            WriteHeader(buffer, ref offset, 1, uint.MaxValue);")
            .AppendLine("        }")
            .AppendLine("        else")
            .AppendLine("        {")
            .AppendLine("            WriteHeader(buffer, ref offset, 1, (uint)fieldLength);")
            .Append("            StrictUtf8.GetBytes(").Append(name)
            .AppendLine(", new global::System.Span<byte>(buffer, offset, fieldLength));")
            .AppendLine("        }")
            .AppendLine("        encoded = buffer;")
            .AppendLine("        return CodecStatus.Success;")
            .AppendLine("    }");
    }

    private static void EmitDtoCodec(StringBuilder builder, DtoData dto)
    {
        var dtoType = dto.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        builder.Append("    public static CodecStatus TryEncode_Dto_").Append(dto.Id)
            .Append("( ").Append(dtoType).AppendLine(" value, out byte[] encoded)")
            .AppendLine("    {")
            .AppendLine("        encoded = global::System.Array.Empty<byte>();")
            .AppendLine("        if (value is null) return CodecStatus.InvalidValue;")
            .AppendLine("        int encodedLength = 0;");

        foreach (var field in dto.Fields)
        {
            if (field.Type!.Kind == "string")
            {
                if (!field.Type.Nullable)
                    builder.Append("        if (value.").Append(EscapeIdentifier(field.Name))
                        .AppendLine(" is null) return CodecStatus.InvalidValue;");
                builder.Append("        int fieldLength_").Append(field.Id).AppendLine(" = -1;");
            }
        }

        builder.AppendLine("        try")
            .AppendLine("        {");
        foreach (var field in dto.Fields)
        {
            if (field.Type!.Kind == "string")
            {
                builder.Append("            if (value.").Append(EscapeIdentifier(field.Name))
                    .Append(" is not null) fieldLength_").Append(field.Id)
                    .Append(" = StrictUtf8.GetByteCount(value.").Append(EscapeIdentifier(field.Name)).AppendLine(");")
                    .Append("            encodedLength = checked(encodedLength + 6 + global::System.Math.Max(fieldLength_")
                    .Append(field.Id).AppendLine(", 0));");
            }
            else
            {
                builder.Append("            encodedLength = checked(encodedLength + 6 + ")
                    .Append(WireWidth(field.Type.Kind)).AppendLine(");");
            }
        }

        builder.AppendLine("        }")
            .AppendLine("        catch (global::System.Text.EncoderFallbackException)")
            .AppendLine("        {")
            .AppendLine("            return CodecStatus.InvalidValue;")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.OverflowException)")
            .AppendLine("        {")
            .AppendLine("            return CodecStatus.BufferTooLarge;")
            .AppendLine("        }")
            .AppendLine("        if (encodedLength > MaxBufferBytes) return CodecStatus.BufferTooLarge;")
            .AppendLine("        var buffer = new byte[encodedLength];")
            .AppendLine("        int offset = 0;");

        foreach (var field in dto.Fields)
            EmitEncodeField(builder, field);

        builder.AppendLine("        encoded = buffer;")
            .AppendLine("        return CodecStatus.Success;")
            .AppendLine("    }")
            .Append("    public static CodecStatus TryDecode_Dto_").Append(dto.Id)
            .Append("(byte[]? source, out ").Append(dtoType).AppendLine("? value)")
            .AppendLine("    {")
            .AppendLine("        value = null;")
            .AppendLine("        if (source is null) return CodecStatus.InvalidValue;")
            .AppendLine("        if (source.Length > MaxBufferBytes) return CodecStatus.BufferTooLarge;")
            .AppendLine("        global::System.ReadOnlySpan<byte> input = new global::System.ReadOnlySpan<byte>(source);")
            .AppendLine("        int offset = 0;");

        foreach (var field in dto.Fields)
        {
            var typeName = field.Symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            builder.Append("        ").Append(typeName).Append(" field_").Append(field.Id).AppendLine(" = default!;");
            builder.Append("        if (!TryReadHeader(input, ref offset, ").Append(field.Id)
                .Append(", out uint length_").Append(field.Id).AppendLine(")) return CodecStatus.Malformed;");
            if (field.Type!.Kind == "string")
            {
                builder.Append("        if (length_").Append(field.Id).AppendLine(" == uint.MaxValue)")
                    .AppendLine("        {");
                if (field.Type.Nullable)
                    builder.Append("            field_").Append(field.Id).AppendLine(" = null!;");
                else
                    builder.AppendLine("            return CodecStatus.Malformed;");
                builder.AppendLine("        }")
                    .AppendLine("        else")
                    .AppendLine("        {")
                    .Append("            if (!TryReadBytes(input, ref offset, length_").Append(field.Id)
                    .Append(", out var bytes_").Append(field.Id).AppendLine(")) return CodecStatus.Malformed;")
                    .AppendLine("            try")
                    .AppendLine("            {")
                    .Append("                field_").Append(field.Id).Append(" = StrictUtf8.GetString(bytes_")
                    .Append(field.Id).AppendLine(");")
                    .AppendLine("            }")
                    .AppendLine("            catch (global::System.Text.DecoderFallbackException)")
                    .AppendLine("            {")
                    .AppendLine("                return CodecStatus.Malformed;")
                    .AppendLine("            }")
                    .AppendLine("        }");
            }
            else
            {
                var width = WireWidth(field.Type.Kind);
                builder.Append("        if (length_").Append(field.Id).Append(" != ").Append(width)
                    .Append(" || !TryReadBytes(input, ref offset, length_").Append(field.Id)
                    .Append(", out var bytes_").Append(field.Id).AppendLine(")) return CodecStatus.Malformed;");
                EmitDecodeValue(builder, field);
            }
        }

        builder.AppendLine("        if (offset != input.Length) return CodecStatus.Malformed;");
        var constructorArguments = dto.CodecConstructorParameters!
            .Select(parameter =>
            {
                var field = FindFieldForParameter(dto.Fields, parameter)!;
                return EscapeIdentifier(parameter.Name) + ": field_" + field.Id;
            });
        builder.Append("        value = new ").Append(dtoType).Append('(')
            .Append(string.Join(", ", constructorArguments)).AppendLine(");")
            .AppendLine("        return CodecStatus.Success;")
            .AppendLine("    }");
    }

    private static void EmitEncodeField(StringBuilder builder, FieldData field)
    {
        var property = "value." + EscapeIdentifier(field.Name);
        if (field.Type!.Kind == "string")
        {
            builder.Append("        if (").Append(property).AppendLine(" is null)")
                .AppendLine("        {")
                .Append("            WriteHeader(buffer, ref offset, ").Append(field.Id)
                .AppendLine(", uint.MaxValue);")
                .AppendLine("        }")
                .AppendLine("        else")
                .AppendLine("        {")
                .Append("            WriteHeader(buffer, ref offset, ").Append(field.Id)
                .Append(", (uint)fieldLength_").Append(field.Id).AppendLine(");")
                .Append("            StrictUtf8.GetBytes(").Append(property).Append(", new global::System.Span<byte>(buffer, offset, fieldLength_")
                .Append(field.Id).AppendLine("));")
                .Append("            offset += fieldLength_").Append(field.Id).AppendLine(";")
                .AppendLine("        }");
            return;
        }

        var width = WireWidth(field.Type.Kind);
        builder.Append("        WriteHeader(buffer, ref offset, ").Append(field.Id)
            .Append(", ").Append(width).AppendLine("u);");
        var slice = $"new global::System.Span<byte>(buffer, offset, {width})";
        var value = property;
        switch (field.Type.Kind)
        {
            case "bool":
                builder.Append("        ").Append(slice).Append("[0] = ").Append(value)
                    .AppendLine(" ? (byte)1 : (byte)0;");
                break;
            case "uint8":
                builder.Append("        ").Append(slice).Append("[0] = ").Append(value).AppendLine(";");
                break;
            case "int8":
                builder.Append("        ").Append(slice).Append("[0] = unchecked((byte)").Append(value).AppendLine(");");
                break;
            case "int16":
                builder.Append("        global::System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(")
                    .Append(slice).Append(", ").Append(value).AppendLine(");");
                break;
            case "uint16":
                builder.Append("        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(")
                    .Append(slice).Append(", ").Append(value).AppendLine(");");
                break;
            case "int32":
                builder.Append("        global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(")
                    .Append(slice).Append(", ").Append(value).AppendLine(");");
                break;
            case "uint32":
                builder.Append("        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(")
                    .Append(slice).Append(", ").Append(value).AppendLine(");");
                break;
            case "int64":
                builder.Append("        global::System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(")
                    .Append(slice).Append(", ").Append(value).AppendLine(");");
                break;
            case "uint64":
                builder.Append("        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(")
                    .Append(slice).Append(", ").Append(value).AppendLine(");");
                break;
            case "float32":
                builder.Append("        global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(")
                    .Append(slice).Append(", global::System.BitConverter.SingleToInt32Bits(").Append(value).AppendLine("));");
                break;
            case "float64":
                builder.Append("        global::System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(")
                    .Append(slice).Append(", global::System.BitConverter.DoubleToInt64Bits(").Append(value).AppendLine("));");
                break;
        }
        builder.Append("        offset += ").Append(width).AppendLine(";");
    }

    private static void EmitDecodeValue(StringBuilder builder, FieldData field)
    {
        var value = "bytes_" + field.Id + "[0]";
        switch (field.Type!.Kind)
        {
            case "bool":
                builder.Append("        if (").Append(value).AppendLine(" > 1) return CodecStatus.Malformed;")
                    .Append("        field_").Append(field.Id).Append(" = ").Append(value).AppendLine(" == 1;");
                break;
            case "uint8":
                builder.Append("        field_").Append(field.Id).Append(" = ").Append(value).AppendLine(";");
                break;
            case "int8":
                builder.Append("        field_").Append(field.Id).Append(" = unchecked((sbyte)").Append(value).AppendLine(");");
                break;
            case "int16":
                AppendReadPrimitive(builder, field, "ReadInt16LittleEndian");
                break;
            case "uint16":
                AppendReadPrimitive(builder, field, "ReadUInt16LittleEndian");
                break;
            case "int32":
                AppendReadPrimitive(builder, field, "ReadInt32LittleEndian");
                break;
            case "uint32":
                AppendReadPrimitive(builder, field, "ReadUInt32LittleEndian");
                break;
            case "int64":
                AppendReadPrimitive(builder, field, "ReadInt64LittleEndian");
                break;
            case "uint64":
                AppendReadPrimitive(builder, field, "ReadUInt64LittleEndian");
                break;
            case "float32":
                builder.Append("        field_").Append(field.Id)
                    .Append(" = global::System.BitConverter.Int32BitsToSingle(global::System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes_")
                    .Append(field.Id).AppendLine("));");
                break;
            case "float64":
                builder.Append("        field_").Append(field.Id)
                    .Append(" = global::System.BitConverter.Int64BitsToDouble(global::System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes_")
                    .Append(field.Id).AppendLine("));");
                break;
        }
    }

    private static void AppendReadPrimitive(StringBuilder builder, FieldData field, string method) =>
        builder.Append("        field_").Append(field.Id)
            .Append(" = global::System.Buffers.Binary.BinaryPrimitives.").Append(method)
            .Append("(bytes_").Append(field.Id).AppendLine(");");

    private static int WireWidth(string kind) => kind switch
    {
        "bool" or "uint8" or "int8" => 1,
        "int16" or "uint16" => 2,
        "int32" or "uint32" or "float32" => 4,
        "int64" or "uint64" or "float64" => 8,
        _ => throw new InvalidOperationException($"No fixed wire width for '{kind}'.")
    };

    private static string EscapeIdentifier(string value) =>
        SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None ? value : "@" + value;

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static string StringConstructorArgument(AttributeData attribute) =>
        attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "";

    private static int ConstructorIntArgument(AttributeData attribute) =>
        attribute.ConstructorArguments.FirstOrDefault().Value is int value ? value : 0;

    private static int NamedIntArgument(AttributeData attribute, string name, int defaultValue = 0)
    {
        foreach (var argument in attribute.NamedArguments)
            if (argument.Key == name && argument.Value.Value is int value)
                return value;
        return defaultValue;
    }

    private static void Report(SourceProductionContext context, ISymbol symbol, string message) =>
        context.ReportDiagnostic(Diagnostic.Create(InvalidContract, symbol.Locations.FirstOrDefault(), message));

    private sealed record ServiceData(int Id, string Name, int Version, string Kind, string? ReplyProtocol,
        INamedTypeSymbol Symbol, List<MethodData> Methods, List<StreamData> Streams, List<EventData> Events);
    private sealed record MethodData(int Id, string Name, IMethodSymbol Symbol, List<ParameterData> Parameters,
        ContractType? ReturnType, bool ReturnsVoid, bool IsAsync, bool SupportsCancellation);
    private sealed record StreamData(int Id, string Name, IMethodSymbol Symbol, List<ParameterData> Parameters,
        ContractType ItemType, bool SupportsCancellation);
    private sealed record EventData(int Id, string Name, IEventSymbol Symbol, ContractType ItemType);
    private sealed record ParameterData(string Name, ContractType Type, IParameterSymbol Symbol);
    private sealed record ContractRevisionData(int Major, int Minor, Location? Location, string? Error);
    private sealed record ContractProfileData(int Profile, Location? Location, string? Error);
    private sealed record DtoData(int Id, string Name, int Version, INamedTypeSymbol Symbol, FieldData[] Fields,
        IPropertySymbol[] UnaccountedAnnotatedFields, IParameterSymbol[]? CodecConstructorParameters);
    private sealed record FieldData(int Id, string Name, IPropertySymbol Symbol, ContractType? Type,
        bool Optional, bool HasDefault, object? DefaultValue);
    private sealed record ContractType(string Kind, string? Name, bool Nullable, int? TypeId = null);
    private sealed record SchemaData(int SchemaVersion, string? SchemaHash, SchemaService[] Services, SchemaDto[] Dtos);
    private sealed record TypedSchemaData(int SchemaVersion, string? SchemaHash,
        TypedSchemaService[] Services, SchemaDto[] Dtos);
    private sealed record StreamSchemaData(int SchemaVersion, string? SchemaHash,
        StreamSchemaService[] Services, SchemaDto[] Dtos);
    private sealed record SchemaService(int Id, string Name, int Version, string Kind, SchemaMethod[] Methods);
    private sealed record TypedSchemaService(int Id, string Name, int Version, string Kind, SchemaMethod[] Methods,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyProtocol);
    private sealed record StreamSchemaService(int Id, string Name, int Version, string Kind, SchemaMethod[] Methods,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyProtocol,
        SchemaSubscription[] Subscriptions);
    private sealed record SchemaSubscription(int Id, string Name, string Kind, bool SupportsCancellation,
        SchemaParameter[] Parameters, ContractType ItemType);
    private sealed record SchemaMethod(int Id, string Name, string Kind, bool SupportsCancellation,
        SchemaParameter[] Parameters, ContractType? ReturnType);
    private sealed record SchemaParameter(string Name, ContractType Type);
    private sealed record SchemaDto(int Id, string Name, int Version, SchemaField[] Fields);
    private sealed record NegotiationManifestData(string SchemaHash, NegotiationSchemaData Schema);
    private sealed record AsyncCallbackNegotiationManifestData(
        string SchemaHash, AsyncCallbackNegotiationSchemaData Schema);
    private sealed record NegotiationSchemaData(uint AbiMajor, uint SchemaVersion,
        NegotiationRevisionData ContractRevision, uint FeatureBits, uint RequiredFeatureBits,
        bool AdditiveCapable, NegotiationServiceData Service, NegotiationMethodData Method,
        NegotiationDtoData Dto);
    private sealed record AsyncCallbackNegotiationSchemaData(uint AbiMajor, uint SchemaVersion,
        string Profile, NegotiationRevisionData ContractRevision, uint FeatureBits, uint RequiredFeatureBits,
        bool AdditiveCapable, NegotiationServiceData Service, AsyncCallbackManagedMethodData Method,
        AsyncCallbackNativeServiceData[] NativeServices, NegotiationDtoData[] Dtos);
    private sealed record NegotiationRevisionData(uint Major, uint Minor);
    private sealed record NegotiationServiceData(uint Id, string Name, uint Version);
    private sealed record NegotiationMethodData(uint Id, string Name, string Kind,
        bool SupportsCancellation, uint InputDtoId);
    private sealed record AsyncCallbackManagedMethodData(uint Id, string Name, string Kind,
        bool SupportsCancellation, uint RequestDtoId, uint ResultDtoId);
    private sealed record AsyncCallbackNativeServiceData(uint Id, string Name, uint Version,
        string ReplyProtocol, AsyncCallbackNativeMethodData[] Methods);
    private sealed record AsyncCallbackNativeMethodData(uint Id, string Name, string Kind,
        bool SupportsCancellation, string InputType, string ResultType);
    private sealed record NegotiationDtoData(uint Id, string Name, uint Version,
        NegotiationFieldData[] Fields);
    private sealed record NegotiationFieldData(uint Id, string Name, string Type,
        bool Nullable, bool Optional, object? Default);

    private sealed class NamedTypeSymbolComparer : IEqualityComparer<INamedTypeSymbol>
    {
        public static NamedTypeSymbolComparer Instance { get; } = new();
        public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y) => SymbolEqualityComparer.Default.Equals(x, y);
        public int GetHashCode(INamedTypeSymbol symbol) => SymbolEqualityComparer.Default.GetHashCode(symbol);
    }

    private sealed class NamedTypeArrayComparer : IEqualityComparer<ImmutableArray<INamedTypeSymbol>>
    {
        public static NamedTypeArrayComparer Instance { get; } = new();

        public bool Equals(ImmutableArray<INamedTypeSymbol> x, ImmutableArray<INamedTypeSymbol> y)
        {
            if (x.Length != y.Length)
                return false;
            for (var index = 0; index < x.Length; index++)
                if (!string.Equals(
                        StableTypeFingerprint(x[index]),
                        StableTypeFingerprint(y[index]),
                        StringComparison.Ordinal))
                    return false;
            return true;
        }

        public int GetHashCode(ImmutableArray<INamedTypeSymbol> symbols)
        {
            var hash = new HashCode();
            foreach (var symbol in symbols)
                hash.Add(StableTypeFingerprint(symbol), StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private static string StableTypeFingerprint(INamedTypeSymbol symbol)
    {
        var builder = new StringBuilder();
        AppendFingerprintPart(builder, symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        AppendFingerprintPart(builder, symbol.ContainingAssembly.Identity.ToString());
        AppendFingerprintPart(builder, symbol.TypeKind.ToString());
        AppendFingerprintPart(builder, symbol.DeclaredAccessibility.ToString());
        AppendFingerprintPart(builder, symbol.IsRecord.ToString(CultureInfo.InvariantCulture));
        AppendAttributesFingerprint(builder, symbol.GetAttributes());

        foreach (var member in symbol.GetMembers()
                     .Select(StableMemberFingerprint)
                     .OrderBy(fingerprint => fingerprint, StringComparer.Ordinal))
            AppendFingerprintPart(builder, member);

        return builder.ToString();
    }

    private static string StableMemberFingerprint(ISymbol member)
    {
        var builder = new StringBuilder();
        AppendFingerprintPart(builder, member.Kind.ToString());
        AppendFingerprintPart(builder, member.MetadataName);
        AppendFingerprintPart(builder, member.DeclaredAccessibility.ToString());
        AppendFingerprintPart(builder, member.IsStatic.ToString(CultureInfo.InvariantCulture));
        AppendAttributesFingerprint(builder, member.GetAttributes());

        switch (member)
        {
            case IMethodSymbol method:
                AppendFingerprintPart(builder, method.MethodKind.ToString());
                AppendFingerprintPart(builder, method.Arity.ToString(CultureInfo.InvariantCulture));
                AppendFingerprintPart(builder, StableTypeSignature(method.ReturnType));
                AppendFingerprintPart(builder, method.ReturnsByRef.ToString(CultureInfo.InvariantCulture));
                AppendFingerprintPart(builder, method.ReturnsByRefReadonly.ToString(CultureInfo.InvariantCulture));
                foreach (var parameter in method.Parameters)
                {
                    AppendFingerprintPart(builder, parameter.Name);
                    AppendFingerprintPart(builder, parameter.RefKind.ToString());
                    AppendFingerprintPart(builder, StableTypeSignature(parameter.Type));
                }
                break;
            case IPropertySymbol property:
                AppendFingerprintPart(builder, StableTypeSignature(property.Type));
                AppendFingerprintPart(builder, property.GetMethod?.DeclaredAccessibility.ToString() ?? "");
                AppendFingerprintPart(builder, property.SetMethod?.DeclaredAccessibility.ToString() ?? "");
                AppendFingerprintPart(builder, property.SetMethod?.IsInitOnly.ToString(CultureInfo.InvariantCulture) ?? "");
                break;
            case IFieldSymbol field:
                AppendFingerprintPart(builder, StableTypeSignature(field.Type));
                AppendFingerprintPart(builder, field.IsImplicitlyDeclared.ToString(CultureInfo.InvariantCulture));
                break;
            case IEventSymbol eventSymbol:
                AppendFingerprintPart(builder, StableTypeSignature(eventSymbol.Type));
                break;
        }

        return builder.ToString();
    }

    private static string StableTypeSignature(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "|" +
        type.NullableAnnotation + "|" + type.SpecialType;

    private static void AppendAttributesFingerprint(StringBuilder builder, ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            AppendFingerprintPart(builder, attribute.AttributeClass?.ToDisplayString() ?? "");
            foreach (var argument in attribute.ConstructorArguments)
                AppendTypedConstantFingerprint(builder, argument);
            foreach (var argument in attribute.NamedArguments.OrderBy(argument => argument.Key, StringComparer.Ordinal))
            {
                AppendFingerprintPart(builder, argument.Key);
                AppendTypedConstantFingerprint(builder, argument.Value);
            }
        }
    }

    private static void AppendTypedConstantFingerprint(StringBuilder builder, TypedConstant constant)
    {
        AppendFingerprintPart(builder, constant.Kind.ToString());
        AppendFingerprintPart(builder, constant.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "");
        if (constant.Kind == TypedConstantKind.Array)
        {
            foreach (var value in constant.Values)
                AppendTypedConstantFingerprint(builder, value);
        }
        else
        {
            AppendFingerprintPart(builder,
                Convert.ToString(constant.Value, CultureInfo.InvariantCulture) ?? "<null>");
        }
    }

    private static void AppendFingerprintPart(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    private sealed record SchemaField(int Id, string Name, ContractType? Type);
}
