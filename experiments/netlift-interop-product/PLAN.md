# NetLift Interop Product Plan

## Purpose

This document proposes a clean, isolated product direction for NetLift as a native interop system for .NET libraries consumed from Kotlin/Android and Swift/iOS/macOS projects.

The core idea is:

- Start from a normal .NET library
- Automatically inspect public metadata and project the eligible API surface
- Generate idiomatic Kotlin and Swift APIs from a single canonical contract model
- Publish stable Android and Apple product integrations through Gradle / Maven / SwiftPM
- Treat the native app as the owner of UI, lifecycle, process bootstrap, and app signing
- Keep .NET as the managed business/runtime layer, not the UI layer

This is intentionally a separate product from the existing repo's busy,
implementation-heavy research work. New implementation should begin in the
clean product repository described below; this research repo supplies evidence
and selected source inputs, not the product tree.

## Strategic direction

### Product thesis

NetLift should be the default way for a developer to expose a .NET service contract to native app code without wiring the entire API by hand. The developer writes a normal .NET library with ordinary public types and methods, and NetLift generates corresponding Kotlin and Swift projections as part of the native build.

The system should be:

- automatic in the common case
- explicit in edge cases
- strict about unsupported members
- stable across versions via contract locks and compatibility checks
- backed by build tooling and published packages, not one-off scripts

### Non-goals

This product does not aim to:

- project arbitrary CLR APIs without policy boundaries
- automatically map every .NET concept to native types
- support runtime reflection or plugin loading as a primary model
- act as a managed UI runtime for Android/iOS
- replace the native app runtime, compiler toolchain, or app packaging model

## Product architecture

### 1. Contract compiler

The system should begin with a deterministic contract compiler that inspects a built .NET assembly or Roslyn compilation and emits a canonical, versioned contract IR.

The compiler should support two modes:

- Roslyn-based inspection for project-local dev builds and IDE diagnostics
- PE/metadata-based inspection for final published assemblies and binary compatibility checks

It should compute:

- type graph
- method signatures
- DTO payload shape
- nullability metadata
- async and cancellation semantics
- stable display names and stable numeric IDs
- source locations for diagnostics
- contract versioning and compatibility metadata

The compiler produces a normalized IR used by all backends.

### 2. Compatibility lock / API contract

This is the single most important product requirement after the ability to inspect CLR metadata.

The system must not generate method IDs purely by declaration order. Instead it should assign stable IDs and a deterministic manifest. That manifest should be persisted as part of the package or generated output, with a compatibility check against prior versions.

This creates a product-safe pipeline:

- add new members without breakage
- fail loudly on renames/removals/signature drift
- compare compatibility against previous published package versions
- use a stable schema hash for generated outputs and runtime validation

### 3. Kotlin/Android integration

Android should be the canonical Java/Kotlin target for the first version. The product should provide:

- Gradle plugin for Android apps and libraries
- published Maven runtime AAR and Gradle plugin
- per-variant generated source wiring through AGP source-set APIs
- native-side JNI/Kotlin bridge for app lifecycle and callback dispatch
- app-specific Kotlin wrapper types generated into build output

The Gradle plugin should own:

- the .NET build / publish step
- contract generation
- validation of generated outputs
- version locking and hashing
- source set registration
- staging of runtime assets and artifacts to the app module

### 4. Swift/iOS integration

Apple support should use the same compiler and IR, with a distinct Swift backend. The product should provide:

- SwiftPM build tool plugin for package-based apps
- package-owned Xcode integration where app payload staging needs more than
  declared plugin output
- published Swift package with a binary runtime and compiler tool
- app-specific Swift facade generated into plugin/derived output
- generated support for async/throws semantics, typed result models, and cancellation-aware APIs

Important: generated Swift works well via SwiftPM build tool plugins, while
generated C/header files are not cleanly attached to C targets. The product
therefore uses one generic precompiled C ABI in the runtime XCFramework and
generates app-specific Swift only.

### 5. Runtime bridge and lifecycle policy

The product must define a clear runtime boundary:

- native app owns app lifecycle, UI, process bootstrap, and final packaging
- .NET owns business services and managed logic
- native bridge owns callback registration and thread dispatch
- generated code owns typed translation between .NET and native languages

The bridge should expose a small set of neutral runtime concepts:

- service call invocation
- typed result decoding
- cancellation support
- callback registration
- ownership/lifetime rules
- structured error surfaces

The project should not promise an unconstrained map from arbitrary .NET objects into native languages. The first version should support a bounded, well-defined subset.

### 6. Workload-supported native library mode

NetLift should not permanently copy private startup code from `dotnet/macios`
or `dotnet/android`. The durable solution is a supported workload output mode
that builds the same platform-integrated managed closure as a .NET app while
leaving the operating-system entry point under native app control.

This is materially different from the existing Mono runtime library mode and
from a generic CoreCLR host:

- Mono library mode can package managed code and the Mono runtime into a
  native library and expose `UnmanagedCallersOnly` entry points.
- NativeAOT can produce a native library or Apple framework containing
  exported managed functions.
- A generic CoreCLR host can initialize the CLR and load ordinary managed
  assemblies.

Those mechanisms do not, by themselves, provide the Apple registrar,
Objective-C peer map, ARC/GC reference tracking, Android typemaps,
Java.Interop peer management, generated Java callable wrappers, or the
platform workload's final trimming/native-link closure. NetLift's
platform-bound projection profile needs a **workload library mode**, not only
a runtime library mode.

The conceptual build switch is:

```xml
<NativeHostMode>Library</NativeHostMode>
```

The property name is provisional. Its contract is not:
"compile a .NET class library." Its contract is:

> Build the workload-managed application closure as a native-consumable
> component, retain all registrar/typemap/runtime artifacts, suppress
> ownership of the OS application entry point, and emit a supported explicit
> initialization API.

#### Shared workload contract

Both workloads should implement the following behavior:

1. Preserve the platform workload's normal discovery, trimming, registration,
   AOT/R2R, native-linking, runtime configuration, and binding-runtime steps.
2. Suppress only the generated OS entry point and automatic ownership of app
   lifecycle.
3. Emit an app-specific native component plus a machine-readable component
   manifest. The component is not a universal NetLift runtime package; it is
   the final workload closure for one native application and one contract.
4. Expose an idempotent initialization API with explicit success/error
   results. Concurrent calls must converge on one initialization operation;
   a failed initialization must remain diagnosable and must not masquerade as
   success.
5. Initialize the runtime, platform peer system, registrar/typemaps,
   generated registration, and module tables before any projected
   platform-object call can execute.
6. Allow the native app to own `main`, SwiftUI `@main`, `UIApplicationMain`,
   Android `Application`, activities, services, receivers, and UI lifecycle.
7. Treat the runtime and platform peer system as process-lifetime state.
   Version 1 must not promise unload, restart, or replacement of the runtime
   closure in the same process.
8. Keep managed platform types build-time-known. Library mode does not make
   arbitrary `net11.0-ios` or `net11.0-android` assemblies loadable after
   startup.

The generated component manifest should record:

- workload and .NET runtime versions
- runtime flavor
- target platform, RID, architecture, and minimum OS version
- registrar/typemap mode
- managed assembly and native library inventory
- runtime configuration and feature switches
- exported initialization ABI version
- required linker flags, frameworks, resources, and consumer keep rules
- whether platform-object projection is enabled

NetLift records its ABI/compiler/schema versions in its own manifest and
references the workload component manifest by hash. The upstream workload
manifest must remain useful to non-NetLift consumers.

#### Apple workload changes

Today `dotnet/macios` generates a native `main` in `Target.GenerateMain`.
That generated source configures globals and callbacks such as
`xamarin_setup`, `xamarin_register_assemblies`, and
`xamarin_register_modules`, then calls `xamarin_main` in app or extension
launch mode. `xamarin_main` performs VM and Objective-C bridge
initialization, registers assemblies and the entry assembly, creates GC
support, and finally invokes the managed entry point or extension main.

Apple library mode should separate those responsibilities:

1. Add a workload build mode for a native-owned host. Continue running the
   static/managed-static registrar, linker, interpreter/R2R/AOT, runtime
   configuration, module-registration, and native-link tasks.
2. Generate the setup globals, trusted-platform-assembly data, registrar
   references, R2R module table, and callback wiring, but do not emit a
   native `main`.
3. Split the runtime startup path into supported internal phases:
   - configure generated app/runtime data
   - initialize the VM
   - initialize the Objective-C bridge and peer tracking
   - register assemblies, modules, registrar data, and the managed platform
     closure
   - optionally invoke a managed application entry point
4. Preserve the existing `xamarin_main` behavior by implementing it as the
   compatibility composition of initialize + invoke-main. New library mode
   calls initialization only.
5. Export a small stable C API from the workload-produced component, for
   example:

   ```c
   typedef struct {
       uint32_t size;
       uint32_t abi_version;
       const char *bundle_path;
       const char *runtime_config_name;
       void (*log_callback)(int level, const char *message);
   } dotnet_apple_initialize_options_v1;

   int32_t dotnet_apple_initialize_v1(
       const dotnet_apple_initialize_options_v1 *options,
       dotnet_apple_error_v1 *error);
   ```

   The exact API belongs to the workload and should be general-purpose, not
   NetLift-branded. NetLift binds its function table only after this call
   succeeds.
6. Emit a framework/static-framework plus resources and manifest that Xcode
   can link into a Swift/Objective-C app. An XCFramework is the distribution
   envelope across device/simulator architectures; each slice remains the
   app-specific workload output.
7. Require initialization before the first `Microsoft.iOS` object conversion.
   The native app still creates the SwiftUI scene, app delegate, windows, and
   views.

This is not equivalent to taking an ordinary `net11.0-ios` DLL and loading it
into plain CoreCLR. The framework must contain or link the workload-generated
registrar, Objective-C bridge, R2R/interpreter/AOT products, and the exact
host-shared `Microsoft.iOS` implementation used to create peers.

The Apple implementation should initially support one explicit
process-lifetime initialization on the main thread. Shutdown APIs may flush
NetLift endpoints and callbacks, but they must not imply that CoreCLR/MonoVM,
registrar state, Objective-C class registration, or peer maps can be unloaded.

#### Android workload changes

Today the .NET Android startup path is already close to an embeddable shape,
but it is automatically activated as an application. The generated
`MonoRuntimeProvider` calls `MonoPackageManager.LoadApplication` from
`ContentProvider.attachInfo`. That method loads `libmonodroid`, gathers APK
and split paths, selects the application class loader, calls
`Runtime.initInternal`, and then calls
`ApplicationRegistration.registerApplications`. The native CoreCLR host
initializes the runtime and invokes `JNIEnvInit.Initialize`, which creates the
Java.Interop runtime, sets the current `JniRuntime`, loads typemaps, and
registers native methods.

Android library mode should preserve that machinery but make activation and
packaging library-friendly:

1. Add a native-owned-host mode that still produces:
   - `libmonodroid` and the generated app shared library
   - runtime native libraries
   - assembly stores and runtime configuration
   - typemap/JNI remapping data
   - generated Java callable wrappers and marshal methods
   - `ApplicationRegistration`
   - Java.Interop managed initialization
   - consumer R8/ProGuard rules
2. Emit the final app-specific closure as an AAR or as a workload component
   bundle that the NetLift Gradle plugin deterministically wraps in an AAR.
   The preferred upstream result is an AAR because it can carry JNI
   libraries, Java/Kotlin bootstrap classes, resources, manifest metadata,
   and consumer keep rules in one standard artifact.
3. Make the generated runtime `ContentProvider` optional. Explicit mode must
   not inject providers merely to acquire app startup ownership.
4. Expose an idempotent Java API, with a Kotlin facade, such as:

   ```kotlin
   DotnetAndroidRuntime.initialize(
       applicationContext = applicationContext,
       classLoader = applicationContext.classLoader
   )
   ```

   The implementation should derive APK/split paths, native library
   directories, files/cache directories, locale, emulator limits, and the
   correct application class loader using the same generated/runtime code as
   normal .NET Android startup.
5. Call `ApplicationRegistration.registerApplications` and complete
   Java.Interop initialization before returning success. A caller must never
   observe a partially initialized peer runtime.
6. Offer provider or AndroidX Startup integration only as opt-in convenience
   modes layered over the same initializer. NetLift should use explicit
   initialization so ordering is visible and testable.
7. Treat each Android process independently. If the application declares
   components in secondary processes, each process requiring managed code
   must initialize its own runtime closure with that process's context and
   class loader.
8. Keep the native app's `Application` and component classes authoritative.
   Managed `[Activity]`, `[Service]`, or Java-callable types are supported only
   when they were included in the build-time-generated manifest/typemap/JCW
   closure; they do not become runtime-discoverable plugins.

The generated Kotlin initializer is a workload bootstrap API, not NetLift's
public service API. NetLift's Maven runtime and Gradle plugin should compose
it and expose the simpler product-facing `NetLiftRuntime.initialize` surface.

#### Runtime-flavor boundaries

The build mode should be expressed at the workload level even though its
implementation differs by runtime:

- **CoreCLR:** initialize one hosted runtime, then initialize the platform
  binding bridge and generated registration. No in-process unload promise.
- **MonoVM:** use the workload's existing AOT/interpreter and platform bridge
  initialization. Mono runtime library mode is useful implementation prior
  art, but workload registrar/typemap integration must still be added.
- **NativeAOT:** emit a native library/framework with exported entry points
  and generated platform registration. There may be no separately hosted VM,
  but Objective-C/Java peer initialization and workload-generated metadata
  still require an explicit supported composition point.

NetLift should not expose runtime-flavor differences in generated
Swift/Kotlin service APIs. The component manifest and support matrix record
them, and package tests exercise each advertised flavor.

#### In-repository proof first; upstream later

The maintainable division is:

- `dotnet/runtime` owns generic runtime library/hosting primitives.
- `dotnet/macios` owns Apple registrar, peer runtime, native linking, startup
  separation, and workload component output.
- `dotnet/android` owns Android typemaps, Java.Interop startup, JCWs,
  application registration, assembly stores, and workload component output.
- NetLift owns contract compilation, generated service projection, its stable
  function-table ABI, package UX, and Gradle/SwiftPM composition.

The first proof should live in this repository and use the installed
`Microsoft.iOS` and `Microsoft.Android` workload packs. Do not begin by
forking either workload, building replacement packs, or asking contributors
to install a custom SDK.

The in-repository proof may use local MSBuild targets, component packagers,
generated-output capture, and small bootstrap shims to compose the workload's
existing outputs into native-owned apps. It should avoid copying upstream
runtime implementation source where installed headers/libraries and generated
artifacts are sufficient.

Every unsupported/private dependency used by the proof must be recorded:

- workload target/task or internal property invoked
- generated file or native library consumed
- private symbol or initialization ordering assumption
- source repository and commit where the behavior is implemented
- desired supported build output/API that would replace the dependency

This turns the proof into an evidence-backed upstream proposal rather than an
early fork-maintenance commitment.

Use one temporary, isolated proof root:

```text
experiments/workload-library-mode/
  README.md                    scope, commands, evidence, cleanup ledger
  shared/                      manifest schema and initialization-state tests
  apple/
    managed-component/        workload project and local component targets
    native-app/               SwiftUI-owned app/entry point
    tests/                     package, symbol, identity, lifecycle checks
  android/
    managed-component/        workload project and local component targets
    native-app/               Kotlin/Compose-owned app/components
    tests/                     AAR, typemap, identity, lifecycle checks
```

This is not a new permanent product tree. It has one narrow question:

> Can the current installed workloads' generated runtime, registrar/typemap,
> and peer artifacts be recomposed into a native-owned application without a
> workload-generated OS entry point?

After the proof:

1. promote reusable NetLift-owned component packaging, manifests, tests, and
   bootstrap facade code into final `src/`, platform package, and test
   locations
2. delete the temporary proof apps, captured outputs, duplicated bootstrap
   paths, and unsuccessful alternatives
3. write focused upstream design issues using the measured private-hook
   inventory and working native-host samples
4. fork or patch `dotnet/macios`/`dotnet/android` only if upstream
   implementation work is then required to replace private hooks
5. migrate to supported workload outputs when available and delete local
   compatibility shims

A stable platform-object support claim still requires supported upstream
hooks or an explicit long-term commitment to maintaining the necessary
integration. The in-repository proof is for learning and validation; it does
not turn private workload internals into a product contract.

#### Workload library-mode acceptance tests

For each platform and runtime flavor advertised:

- a native-authored app owns the real OS entry point and lifecycle types
- the app initializes the workload component explicitly exactly once
- generated registrar/typemap/peer artifacts are present and audited
- a portable projected service call succeeds
- an existing native `UIView`/`View` round-trips through the managed peer map
  with identity preserved
- wrong native classes fail with a translated error rather than a crash
- callbacks, cancellation, ARC/GC or JNI/GC pressure, and app lifecycle
  recreation return all NetLift leases/references to baseline
- release trimming, R8, signing, and physical-device execution succeed
- no copied private workload bootstrap source is compiled into NetLift
- no generated .NET-owned `main`, `AppDelegate`, `Application`, or activity
  replaces the native app's entry/lifecycle ownership

### 7. Native type strategy and bridge discipline

The DNNE architecture assessment was useful because it makes an important
distinction explicit: a managed type and the native type used in an export do
not have to be the same declaration. DNNE's `C99Type` and `C99DeclCode`
concepts can change the C declaration text that native callers see.

They do **not** implement marshalling. In particular:

- DNNE does not consume .NET `CustomMarshaller` or `NativeMarshalling`
  conversion semantics when generating a reverse export.
- Without an explicit native type override, unsupported non-primitive value
  types are declaration gaps (for example, `/* SUPPLY TYPE */`), not
  automatically generated marshalling solutions.
- `C99Type("UIView *")`, `C99Type("jobject")`, or
  `C99Type("struct T")` changes only the emitted native type spelling.
- It does not generate conversion code, validate layout, retain/release an
  object, call a source-generated custom marshaller, or establish native peer
  identity.
- `UnmanagedCallersOnly` remains an unmanaged entry boundary. Its actual
  managed signature must remain legal for direct unmanaged invocation;
  changing the generated header cannot make a non-blittable signature legal.
- .NET custom marshallers primarily participate in generated
  `LibraryImport`/P/Invoke stubs. NetLift cannot assume those stubs exist for
  arbitrary native-to-managed exports.

NetLift should adopt that separation as a core compiler concept rather than
copying DNNE's attributes directly into every consumer API. The interop
problem is not "how do we pass CLR objects across a native boundary?" It is
"how do we define a stable native value model that is safe, typed, and
versioned across both sides of the boundary?"

The product should not expose CLR object graphs or arbitrary runtime
constructs directly to Swift or Kotlin. Instead it should generate a curated
native contract surface built from a canonical schema. Portable APIs should
use value-like, serializable, and lifetime-safe concepts; platform object
peers require an explicit workload-integrated adapter.

#### Four distinct projection concepts

The IR and generator must model four different concepts instead of collapsing
them into a type-name substitution:

1. **Managed semantic type** - the type authored by the .NET developer, such
   as `DateTimeOffset`, `Guid`, `OrderRequest`, `UIKit.UIView`,
   `Android.Views.View`, or `Stream`.
2. **Stable bridge representation** - the ABI-safe shape used across the
   native boundary, such as fixed-width integers, byte spans, UTF-8 slices,
   generated C-layout structs, opaque handles, or encoded payloads.
3. **Projected platform type** - the idiomatic type presented to the app,
   such as Swift `Date`, `UUID`, `Data`, and `URL`, or Kotlin `Instant`,
   `UUID`, `ByteArray`, and `Uri`.
4. **Generated conversion and lifetime program** - the managed thunk,
   Objective-C/JNI shim, facade conversion, validation, ownership transfer,
   retain/release, thread-affinity, exception translation, and cleanup logic
   that safely moves between the first three concepts.

For example:

```text
.NET DateTimeOffset
    -> bridge { unixTimeNanoseconds: Int64, offsetMinutes: Int16 }
    -> Swift Date / Kotlin Instant
    -> generated checked conversion in both directions

.NET Guid
    -> bridge 16-byte value
    -> Swift UUID / java.util.UUID
    -> generated byte-order and validity conversion

.NET ReadOnlyMemory<byte>
    -> bridge borrowed-or-owned byte slice with explicit lifetime
    -> Swift Data / Kotlin ByteArray
    -> generated copy/borrow/release path
```

This separation lets Swift and Kotlin expose different native types without
changing the managed contract or the stable bridge ABI. The fourth concept is
not metadata decoration: it is executable generated code and must be tested
as part of the contract.

#### DNNE's role

DNNE is useful as:

- evidence that metadata-driven managed exports and generated native
  declarations are practical
- prior art for scanning `UnmanagedCallersOnly`, mapping primitive/blittable
  types, spelling calling conventions, and deterministically emitting native
  declarations
- a possible source of bounded emitter code or design ideas for generated C
  wrappers and headers

DNNE must **not** become NetLift's runtime host, mobile packaging layer,
marshaller, or contract/projection system. Its normal self-starting
`nethost`/`hostfxr` path conflicts with NetLift's architecture:

- the native app owns one process-wide CoreCLR host
- NetLift activates that runtime once using platform-specific host bootstrap
- all projected modules bind through negotiated, versioned function tables
- initialization and invocation failures return explicit status/error values
- callback, cancellation, trust, drain, and release rules remain under
  NetLift's lifecycle protocol

NetLift must not ship or invoke DNNE's `platform.c` bootstrap, allow each
module to start its own runtime, or expose a growing set of unversioned
per-method native symbols as the product ABI.

The bounded reuse decision is therefore:

- first own the small NetLift emitter and stable function-table ABI
- optionally borrow or fork DNNE's scanner, primitive type/calling-convention
  tables, deterministic header writer, or wrapper-emission structure
- if any DNNE-derived emitter is retained, run it in a NetLift-specific mode
  whose wrappers bind to the already initialized NetLift runtime/function
  table
- never make DNNE attributes or bootstrap behavior part of the public product
  contract

Given the existing manifest hashes, host trust, mobile lifecycle, callbacks,
async operations, and compatibility requirements, adapting DNNE wholesale is
expected to be more complex than owning this bounded emitter.

#### Shared design principles

- CLR metadata is the canonical source of truth for types and members, but the generated native API is intentionally not a raw mirror of CLR types.
- The generated bridge should operate on explicit value objects, enums, primitive equivalents, data-record DTOs, and asynchronous service contracts.
- The boundary should have a stable descriptor or schema manifest so native consumers can validate support without depending on runtime reflection.
- Any concept that carries instance identity, unmanaged memory, or arbitrary object graphs is treated as unsupported unless there is a dedicated, explicit adapter.
- Native type lifetimes must be explicit: registrations, leases, callbacks, and request/response ownership must be accounted for and aggressively validated.

#### Native type adapter model

NetLift should provide a compile-time type adapter registry. Each adapter
defines:

- the managed type or generic shape it recognizes
- the canonical IR representation
- the bridge representation and ownership category
- managed-to-bridge and bridge-to-managed conversion
- Swift type spelling and conversion code
- Kotlin/JVM type spelling and conversion code
- required imports, native declarations, and runtime capabilities
- compatibility identity and version

Built-in adapters should cover common cross-platform types. Product or app
authors may register explicit adapters for domain or platform types, but
custom adapters must be compile-time generator inputs; arbitrary runtime
marshaller discovery would undermine trimming, AOT, diagnostics, and
deterministic builds.

Adapters should be classified by ownership:

- **inline value**: fixed-size scalar or struct copied across the boundary
- **owned buffer**: callee returns memory with a generated release operation
- **borrowed view**: valid only for the duration of the call
- **opaque handle**: stable token backed by a generated retain/release table
- **encoded value**: schema-derived bytes decoded into an idiomatic native type

The manifest must record the adapter ID, adapter version, bridge shape, and
ownership category. Changing any of these is an ABI compatibility event.

#### Two projection profiles

The product needs two explicit profiles rather than one rule for all native
types:

1. **Portable projection** is the default. It accepts value-like contracts
   that can be projected on both Apple and Android without either platform
   workload. These contracts can remain ordinary `net11.0` assemblies.
2. **Platform-bound projection** is opt-in. It permits recognized Apple or
   Android binding types, but the adapter assembly becomes part of the final
   platform app's workload-generated registration, trimming, native-link, and
   runtime closure. It is not an arbitrary DLL loaded after startup.

A recommended solution shape is:

```text
Orders.Core/                   net11.0 portable business contract
Orders.AppleAdapter/           net11.0-ios platform-bound adapter
Orders.AndroidAdapter/         net11.0-android platform-bound adapter
OrdersApp/                     native app + workload-produced platform closure
```

This preserves the simple and broadly reusable path while allowing advanced
APIs to remain strongly typed in C# and native code.

#### Swift/iOS-specific type model

For Swift, the native contract should look like:

- structs for DTOs and request/response payloads
- enums for discriminated values and status codes
- optionals for nullable values
- `async throws` for service calls
- `AsyncSequence` / stream abstractions for long-lived event streams
- generated Swift wrappers around opaque native handles, not direct CLR references

The product should avoid exposing raw `IntPtr`, pointer-heavy types, or unmanaged object references as first-class Swift APIs. Instead, the generated Swift facade should present idiomatic Swift values while the internal bridge uses a stable protocol representation and a private runtime handle.

This is the practical answer to the Apple-side native-type problem: keep the semantic type system in the generated Swift layer, keep the raw CLR representation in the managed/runtime layer, and only surface closed, typed native values to the app.

Apple framework types such as `URL`, `Date`, `UUID`, `Data`, and selected
CoreGraphics value types should be implemented as built-in projection
adapters.

Objective-C-compatible reference objects such as `UIKit.UIView` require a
separate **Apple platform-peer adapter**. A projected method can remain
strongly typed:

```csharp
public void Attach(UIKit.UIView view);
```

while the generated layers are:

```text
Swift API                 attach(view: UIView)
Objective-C shim          attachView(UIView *view)
stable NetLift ABI        attach_view(void *objcObject)
managed entry thunk       Runtime.GetNSObjectChecked<UIView>(handle)
managed API               Attach(UIView view)
```

This depends on the .NET iOS workload's process-wide Objective-C integration:

- native-object-to-managed-peer identity map
- ARC/GC reference tracking
- Objective-C class maps and wrapper factories
- block/delegate marshalling and trampolines
- static/partial-static registrar output
- trimming, native-linking, and ReadyToRun/interpreter closure

Therefore a `net11.0-ios` adapter cannot simply be loaded into today's plain
NetLift CoreCLR host. `Microsoft.iOS` and its runtime glue must be supplied by
the Apple host and shared process-wide. The adapter must be known to the
whole-app .NET iOS build so the registrar and linker see it.

Passing an existing UIKit object is the first supported reference-object
scenario. Returning managed-created `NSObject` subclasses, exposing
`[Export]` members, or discovering new registered types after signing is a
later and substantially harder capability.

Every Apple platform-peer mapping must specify:

- Objective-C compatibility and expected native class
- `+0` borrowed versus `+1` transferred ownership
- whether the managed side may retain the peer beyond the call
- `@MainActor` or other thread/isolation requirements
- whether registrar-generated classes/selectors are required
- host-shared assembly and native-framework dependencies

Pure Swift structs with payloads, actors, generic types, and non-Objective-C
protocols do not use this object-peer path. They require generated bridge
representations or native-owned service interfaces.

#### Android/Kotlin-specific type model

For Android, the same principle applies but with JVM/Kotlin semantics:

- `data class` for DTOs and records
- `enum class` for typed statuses and flags
- nullable values and Kotlin null safety
- `suspend` functions for async service calls
- `Flow` for long-lived streams or event subscriptions
- generated wrappers over opaque native handles rather than direct JVM references to managed objects

The Android interop boundary should not attempt to share arbitrary CLR objects across JNI. Java/Kotlin should see a stable, value-oriented view of the .NET contract. The bridge should own the lifetime and identity rules behind the scenes, with generated Kotlin code exposing safe, idiomatic types rather than raw JNI handles.

This matters especially for Android because object lifetimes and callback ownership are more sensitive to Activity recreation, coroutine cancellation, and JNI global reference churn. The bridge must separate:

- value transport
- callback registration
- requested-operation lifetime
- native release and teardown paths

A direct CLR object graph is a non-starter; an explicit opaque-handle model plus generated Kotlin/Swift typed facades is the viable path.

Android/JVM types need the same adapter concept as Swift, not a separate
ad-hoc JNI marshaller. Built-in adapters should include types such as
`java.util.UUID`, `java.time.Instant`, `ByteArray`, and carefully selected
Android value types such as `android.net.Uri`.

Java-compatible reference objects such as `android.view.View` can use an
**Android platform-peer adapter** analogous to the Apple peer adapter. The
generated path is:

```text
Kotlin API                attach(view: android.view.View)
JNI C/C++ shim            receives JNIEnv* and jobject
stable NetLift ABI        forwards an opaque native reference/handle
managed entry thunk       resolves the Java.Interop Android.Views.View peer
managed API               Attach(Android.Views.View view)
```

The `jobject` spelling alone supplies none of this behavior. It is generated
conversion/lifetime code owned by NetLift and the initialized .NET Android
runtime.

This requires the .NET Android workload's process-wide integration:

- JNI peer identity and reference management
- Java class-to-managed-type maps
- generated Java callable wrappers and marshal methods where required
- native registration and typemap artifacts
- trimming/AOT roots and packaged Android runtime support
- Android binding assemblies shared by the host

A `net11.0-android` adapter therefore follows the same whole-app rule as the
Apple adapter. Copying `Mono.Android`/platform reference assemblies beside an
arbitrary plugin is not enough, and a private assembly-load context must not
create a second copy of the platform binding runtime.

Accepting an existing `View` is easier than exposing a managed-created
`View` subclass or Java-callable managed type. The latter requires the
workload-generated Java wrapper/registration closure and should be a later
capability.

`Context`, `Activity`, and `View` are also lifecycle-bearing objects, so
generated APIs must not encourage long-lived retention. The adapter must
declare whether a parameter is call-scoped, weakly held, or retained through
an explicit lease. Where object passage is unnecessary, prefer a
Kotlin-owned service registered with NetLift and exchange portable values.

Pure Kotlin concepts such as suspend-function objects, inline/value classes,
Compose functions/state, and Kotlin-only metadata-driven abstractions do not
automatically map through JNI. They require a Kotlin facade over a stable
bridge representation or a native-owned service interface.

The JNI implementation must distinguish:

- `JNIEnv*` is thread-local and must never be cached or used from another
  thread
- local references, which never escape their native call frame
- global or weak-global references, created with `NewGlobalRef` or
  `NewWeakGlobalRef` for async retention and released with the matching
  `DeleteGlobalRef`/`DeleteWeakGlobalRef`, which are owned by a registration
  lease
- native threads, which attach/detach through the process-cached `JavaVM`
- class lookup, which uses the correct application class loader rather than
  assuming `FindClass` works on arbitrary native threads
- pending Java exceptions, which are checked and translated before further
  JNI work or return through the NetLift ABI
- Kotlin coroutine jobs, which participate in revoke/drain/release
- native table-call cleanup, which must finish before final release

Generated `suspend` and `Flow` APIs must propagate cancellation into the
managed operation, suppress stale replies by registration generation, and
drain active callbacks before deleting JNI global references. Generated
packaging must also contribute narrowly scoped R8 keep rules for generated
entry points and callback targets. Activity recreation replaces
Activity-scoped registrations without restarting the process-wide CoreCLR.

#### Platform object rules

Platform object projection is permitted only when all of the following are
true:

- the native object model has a workload-supported managed peer system
- the platform adapter is included in the app-wide workload build closure
- the host owns one shared platform runtime and binding assembly set
- the generated ABI carries an opaque native reference, never a CLR object
- type checking uses the workload runtime's peer lookup, not manual wrapper
  construction
- ownership, thread affinity, identity, and retention are declared
- exceptions are converted to status/error values before returning through
  C or JNI

This makes platform-object projection a deliberate capability, not a general
escape hatch from the portable type system.

#### Concrete product rule

The generator should enforce a strict type taxonomy:

- supported: primitives, strings, enum values, nullable value types, DTO records, arrays/lists/collections with bounded element types, async operation patterns, streams/events
- supported in the platform-bound profile: recognized Objective-C or
  Java.Interop peer types with app-build participation and explicit
  ownership/thread metadata
- supported with warnings: choice-like payloads, optional metadata, custom encodings, tolerantly-ignored unused fields
- unsupported: arbitrary object graph references, unmanaged pointers, by-ref parameters, delegate values, dynamic objects, reflection-heavy APIs, framework-only abstractions without a native analogue

That taxonomy should be enforced by the generator, not left as a runtime accident.

The product should therefore treat the cross-language bridge as a generated, versioned ABI contract, not as a natural reflection of CLR type identity.

#### Native-type acceptance tests

Every built-in adapter should have generated round-trip and negative tests
across all four projection concepts:

- managed value -> bridge -> Swift value -> bridge -> managed value
- managed value -> bridge -> Kotlin value -> bridge -> managed value
- null, empty, minimum, maximum, Unicode, malformed, and unknown-version cases
- ownership tests for borrowed, owned, and opaque-handle representations
- compatibility tests proving adapter or bridge-shape changes are detected
- Swift concurrency and Kotlin coroutine cancellation/lifecycle tests

The same fixture values should run through both platform generators so a type
cannot quietly acquire different wire semantics on Android and Apple.

Platform-peer adapters additionally require:

- same native object maps to the same compatible managed peer
- wrong native class is rejected before entering the user API
- borrowed and retained calls survive forced ARC/GC or JNI/GC pressure
- main-thread/UI-thread requirements are enforced
- app teardown/recreation cannot deliver callbacks to a released generation
- final peer/global-reference counts return to baseline

## Implementation plan

This section is the executable delivery plan. A work package is complete only
when its product code, package, tests, consumer sample, documentation, and
cleanup criteria all pass. A proof that remains in a `spikes/` directory is
evidence, not a completed product capability.

### Delivery rules

1. **Build vertical product slices.** Every slice ends in a package resolved
   by a clean external consumer. Do not build all compiler layers first and
   postpone packaging to the end.
2. **Publish immutable prereleases early.** Use versions such as
   `0.1.0-alpha.12`; never overwrite an existing version. Maven, NuGet,
   SwiftPM tags, runtime binaries, compiler tools, and manifests use the same
   version.
3. **No product `spikes/` directory.** Exploratory code lives only on a short
   branch or under an ignored session/worktree artifact directory. Before
   merge, either move the accepted implementation and regression into its
   final package/test location or delete the experiment.
4. **One implementation per responsibility.** Do not retain OLD/NEW product
   paths after a migration gate. Compatibility fixtures may remain only
   under tests and may not be compiled into product artifacts.
5. **Samples consume packages.** Product samples may not use project
   references to runtime/plugin projects, copied bootstrap files, local
   Maven repositories, or generated sources checked into the app.
6. **Generated files stay generated.** Put all app-specific schema, Swift,
   Kotlin, managed dispatch, runtime staging, and manifests under `obj/`,
   Gradle build output, SwiftPM plugin output, or Xcode derived output.
7. **Unsupported platform claims remain visible.** Simulator/emulator
   support may be published as preview support, but it must not be described
   as physical-device or store-ready support until those gates pass.

### Target repositories and source layout

Create a clean product repository, provisionally
`Redth/netlift-sdk`. Keep this repository as the research/evidence archive;
do not copy its entire `spikes/`, `tests/`, or historical docs trees into the
product repository.

The product repository should contain only:

```text
/
  README.md
  LICENSE
  NOTICE
  Package.swift                     Apple package entry point
  Directory.Build.props
  Directory.Packages.props
  global.json
  src/
    dotnet/
      NetLift.Sdk/                  attributes, analyzer/generator, MSBuild integration
      NetLift.Compiler/             shared compiler/IR library
      NetLift.Tool/                 CLI used by native build integrations
      NetLift.ManagedHost/          process-wide managed host/runtime internals
    native/
      include/netlift.h             frozen public C ABI
      core/                         platform-neutral host/lifecycle implementation
    android/
      runtime/                      AAR: JNI host, Kotlin runtime API, packaged .NET runtime
      gradle-plugin/                AGP variant integration and app payload generation
    apple/
      Sources/NetLift/              public Swift runtime API
      Sources/NetLiftNative/        Objective-C/C adapter over the fixed ABI
      Plugins/NetLiftPlugin/        SwiftPM build-tool plugin
      Artifacts/                    generated locally; release assets are not committed
  samples/
    orders/
      managed/                      one plain customer-authored .NET library
      android/                      package-consuming Compose app
      apple/                        package-consuming SwiftUI app
  tests/
    compiler/
    managed-host/
    native-core/
    android/
    apple/
    package-consumers/              clean remote-feed/tag consumers
  eng/
    build/
    packaging/
    verify/
  docs/
    architecture.md
    compatibility.md
    publishing.md
    support.md
```

Do not create parallel `Contracts`, `Generator`, `Projection`, and
`SchemaDiff` implementations that independently understand the contract.
Consolidate contract parsing, IR, validation, schema hashing, compatibility,
and backend selection in `NetLift.Compiler`. `NetLift.Sdk` and
`NetLift.Tool` call that shared implementation.

The current research sources are migration inputs:

| Current evidence | Product destination | Migration rule |
| --- | --- | --- |
| `src/NetLift.Contracts` | `NetLift.Sdk` | Preserve the minimal public authoring API; remove profile-specific test contracts from the public package. |
| `src/NetLift.Generator` | `NetLift.Sdk` + `NetLift.Compiler` | Keep Roslyn diagnostics and managed dispatch generation; move schema logic into the shared compiler. |
| `src/NetLift.Projection` and `NetLift.SchemaDiff` | `NetLift.Compiler` + `NetLift.Tool` | Merge into one command surface and delete duplicate parsers/models. |
| `src/NetLift.Negotiation` | `NetLift.ManagedHost` or compiler compatibility library | Keep only protocol code required by the frozen product ABI. |
| `src/NetLift.ManagedHost` | `NetLift.ManagedHost` | Replace test publishers/providers with one product authority and loader path. |
| `src/NetLift.NativeHost.V5` | `src/native` | Promote only the accepted core/lifecycle/facade pieces; remove private/test provider seams from product builds. |
| `samples/Orders.AsyncBusiness` | `samples/orders/managed` | Use as the single authored managed sample. |
| Android AAR and build-loop spikes | Android runtime/plugin tests | Port the smallest package/build-graph implementation and focused regressions, not the spike projects. |
| Apple host and build-loop spikes | Apple package/plugin tests | Port the contract-neutral package and derived-source approach; do not copy fixture-specific hosts. |

### Published artifacts

Freeze these names before the first preview. The names below are the proposed
initial coordinates:

| Ecosystem | Artifact | Proposed identity | Consumer purpose |
| --- | --- | --- | --- |
| NuGet | Managed SDK | `NetLift.Sdk` | Contract API, analyzer/source generator, and MSBuild hooks. |
| NuGet | Compiler tool | `NetLift.Tool` | Versioned `dotnet tool` and CI/debug entry point. Native plugins normally resolve the matching packaged compiler automatically. |
| Maven | Android runtime | `io.github.redth.netlift:runtime-android` | AAR containing Kotlin runtime API, JNI host, native libraries, managed host, and runtime assets; never app business assemblies. |
| Maven | Gradle plugin | `io.github.redth.netlift:gradle-plugin` | AGP variant tasks, compiler invocation, payload staging, and generated Kotlin registration. |
| Gradle plugin marker | Plugin ID | `io.github.redth.netlift` | Customer-facing plugin application. |
| SwiftPM | Swift package | `https://github.com/Redth/netlift-sdk` | Products `NetLift` and `NetLiftRuntime`; plugin `NetLiftPlugin`. |
| GitHub release asset | Apple runtime | `NetLiftRuntime-<version>.xcframework.zip` | Binary target used by the Swift package. |
| GitHub release asset | Compiler tool | `netlift-compiler-<version>.artifactbundle.zip` | Executable tool used by the SwiftPM plugin. |

The Gradle plugin should add the matching runtime dependency automatically.
The Swift package should expose the runtime and build plugin from one package
dependency. Customers should not select internal native/managed components.

### Preview feeds and promotion

Use public infrastructure controlled by the project from the first usable
vertical slice:

- Publish prerelease `NetLift.Sdk` and `NetLift.Tool` packages to NuGet.org.
  Reserve package IDs before implementation begins. Prerelease packages are
  public and immutable.
- Publish Maven artifacts to a public static repository at
  `https://redth.github.io/netlift-sdk/maven/`, deployed from the product
  repository's Pages workflow. This avoids authentication for public
  consumers while iteration is rapid.
- Publish Apple XCFramework and compiler artifact-bundle ZIPs as immutable
  GitHub Release assets in the product repository.
- Maintain `Redth/netlift-sdk` as the small distribution repository whose
  `Package.swift` contains the released binary target URLs and checksums.
  Tag it with the exact same semantic version. Generate this repository's
  manifest, README, license, and tag from the source release workflow; do not
  develop a second runtime implementation there.
- Add Maven Central and the Gradle Plugin Portal only after the preview
  package layout is stable. Promotion changes the repository/channel, not
  package identities or artifact contents.

Version channels:

| Channel | Example | Trigger | Policy |
| --- | --- | --- | --- |
| Development prerelease | `0.1.0-alpha.12` | Manual workflow or accepted release-candidate commit | Public, immutable, frequent; support matrix may be limited. |
| Release candidate | `0.1.0-rc.1` | Signed version tag | Full remote-consumer matrix; no known package-layout changes. |
| Stable | `0.1.0` | Explicit promotion approval | Exact release-candidate artifacts promoted without rebuilding. |

Do not use mutable Maven `SNAPSHOT` versions or retag Swift versions.

### Release automation

Create release automation before the first runtime feature so each vertical
slice is exercised through the real distribution path.

Required workflows:

1. **`ci.yml`**
   - build/test managed compiler and host
   - compile/test native core on supported desktop hosts
   - build Android AAR/plugin and run package-content audits
   - build/test Swift package and plugin
   - build samples against locally staged packages only for PR speed
   - run repository-shape checks that reject committed derived output,
     test-only provider code in product packages, and a top-level `spikes/`
     directory
2. **`publish-preview.yml`**
   - accept one version and source commit
   - build every artifact once
   - generate SHA-256 manifest, SBOM, licenses/notices, and GitHub artifact
     attestations
   - publish NuGet prereleases
   - deploy the Maven repository through GitHub Pages
   - upload XCFramework/compiler bundles to a GitHub prerelease
   - update the Swift distribution repository with exact URLs/checksums and
     create the matching tag
   - invoke remote consumer validation after all feeds are visible
3. **`validate-published.yml`**
   - run in clean workspaces with no source checkout project references
   - restore NuGet packages from NuGet.org
   - resolve Maven artifacts from the public Pages URL
   - resolve the Swift package by Git tag
   - build and run the Orders consumers
   - archive package manifests and resolved dependency graphs
4. **`promote.yml`**
   - take an already validated release-candidate artifact set
   - verify all checksums against the stored release manifest
   - publish/copy the exact bytes to stable channels
   - never rebuild during promotion

Initial secrets and permissions:

- `NUGET_API_KEY` scoped to the NetLift package IDs
- a GitHub App or fine-grained token scoped only to updating/tagging
  `Redth/netlift-sdk`
- GitHub Pages `pages:write` and `id-token:write`
- release `contents:write` and artifact-attestation permissions
- no Apple signing identity is needed to publish the unsigned XCFramework;
  device sample signing credentials are a separate protected environment
- Maven Central credentials and PGP signing keys are added only when Central
  publication begins

### Dependency order

```text
W0 product boundary and repository
  -> W1 release infrastructure
  -> W2 compiler vertical slice
      -> W3 stable host/ABI vertical slice
          -> W3A in-repository workload library-mode proof
              -> W4 Android runtime AAR
                  -> W5 Gradle plugin + first Maven preview
              -> W6 Apple runtime/package feasibility
                  -> W7 SwiftPM plugin + first Swift preview
W5 + W7
  -> W8 async/callback/stream product profile
  -> W9 compatibility and type-adapter expansion
  -> W10 platform-object adapters
  -> W11 device, trust, and stable-release gates
```

Android and Apple package work may proceed in parallel only after W3 freezes
the first internal host contract. Neither platform may create a private
variant of the canonical IR or lifecycle protocol.

## Work packages

### W0 - Establish the clean product boundary

**Primary owner:** repository/product architecture.

**Depends on:** none.

**Implementation steps:**

1. Create the clean product repository and apply the target layout above.
2. Add `global.json`, central package versions, formatting, nullable, warning,
   native compiler, Kotlin, Gradle, Swift, and Xcode version pins.
3. Add a repository-shape verifier that rejects:
   - `spikes/`, `.artifacts/`, build output, generated projections, APK/AAB,
     XCFramework ZIPs, runtime packs, credentials, and test signing keys
   - source references back into this research repository
   - product package source lists containing `TestOnly`, fixture, or spike
     paths
4. Write a migration ledger containing only the current files/concepts
   selected for extraction and the regression that justifies each one.
5. Copy `Orders.AsyncBusiness` as the initial managed sample, preserving its
   small customer-authored shape.
6. Add a single top-level verification command (`./eng/verify.sh` or
   equivalent) that orchestrates product checks without embedding proof
   logic in samples.

**Deliverables:**

- clean repository skeleton
- source migration ledger
- toolchain pins and repository-shape CI
- one managed sample that builds without native projects

**Exit criteria:**

- clean clone contains no generated binary/runtime artifacts
- no product project references the research repository
- CI builds the empty package skeleton on macOS and Linux
- source tree and package-source inventories are deterministic

**Cleanup gate:** delete any copied source that is not yet owned by a final
project; the new repository must not begin as a dump of current `src/` or
`spikes/`.

### W1 - Make publication a day-one capability

**Primary owner:** release engineering.

**Depends on:** W0.

**Implementation steps:**

1. Reserve NuGet package IDs and create the public Maven Pages location and
   Swift distribution repository.
2. Create minimal valid NuGet, AAR, Gradle-plugin, XCFramework, compiler
   artifact-bundle, and Swift package outputs with shared version metadata.
3. Implement release-manifest generation containing source commit, package
   version, ABI version, compiler version, .NET runtime version, target
   architectures, checksums, and dependency licenses.
4. Implement `publish-preview.yml` and publish a `0.0.1-alpha.1` feed
   plumbing release. It is explicitly a packaging smoke, not a usable SDK.
5. Implement clean remote resolution tests for all ecosystems.
6. Add retention and unlisting policy: failed previews may be unlisted but
   immutable bytes/tags are never replaced.

**Deliverables:**

- working public prerelease feeds/tags
- release manifest, SBOM, checksum, and attestation generation
- remote restore/resolve smoke tests

**Exit criteria:**

- a machine with no source checkout resolves all bootstrap artifacts publicly
- package versions and checksums agree across ecosystems
- rerunning publication for the same version fails rather than overwrites
- secrets are confined to protected release environments

**Cleanup gate:** remove bootstrap artifacts from current indexes or mark
them unlisted once the first usable preview supersedes them; do not retain
special bootstrap-only build code.

### W2 - Deliver one canonical compiler vertical slice

**Primary owner:** managed compiler.

**Depends on:** W1.

**Inputs from current research:**

- Roslyn discovery/diagnostics from `NetLift.Generator`
- schema/projection code from `NetLift.Projection`
- compatibility rules from `NetLift.SchemaDiff`
- incremental invalidation behavior from `spikes/build-dev-loop`

**Implementation steps:**

1. Define one versioned IR model containing stable service/type/member/field
   IDs, nullability, async shape, adapter identity, ownership, and source
   locations.
2. Define one versioned `netlift.json` project configuration understood by
   the CLI and Swift plugin. Gradle's typed extension maps to the same option
   model. It contains the managed project path, contract roots, generated
   namespace/module names, and optional compatibility lock path.
3. Implement deterministic assembly/Roslyn discovery and root selection.
4. Support the first bounded contract:
   - primitives, strings, enums
   - records/DTOs
   - nullable fields
   - one `Task<T>` service method
   - `CancellationToken`
5. Emit from the same IR:
   - canonical manifest and compatibility lock
   - managed dispatcher/source
   - Swift DTO/client source
   - Kotlin DTO/client source
6. Merge compatibility checking into `NetLift.Tool`; remove separate parser
   and schema ownership from the generator/backends.
7. Implement content-addressed outputs and declared input/output metadata so
   implementation-only managed edits do not regenerate native sources.
8. Package the analyzer/generator/MSBuild integration as `NetLift.Sdk` and
   the CLI as `NetLift.Tool`.

**Deliverables:**

- `NetLift.Sdk` prerelease
- `NetLift.Tool` prerelease
- canonical IR/manifest specification
- versioned `netlift.json` configuration schema
- generated Swift/Kotlin compile fixtures from one Orders contract

**Exit criteria:**

- clean, implementation-only, contract-change, invalid-contract, and deleted-
  output repair cases match the existing build-loop evidence
- Swift and Kotlin outputs contain the same IDs and schema hash
- two builds in different absolute directories produce byte-identical output
- unsupported signatures fail at the C# declaration and are never omitted
- compatibility lock detects remove/rename/renumber/signature changes

**Cleanup gate:** the product repository contains no independent projection
or schema-diff model and no fixture-specific code path.

### W3 - Freeze the first product host and generic ABI

**Implementation status:** accepted first request/response revision.

The accepted V1 surface is intentionally narrower than the original candidate
list below. It freezes host/endpoint creation, numeric byte invocation,
operation-token cancellation, outstanding-work observation, drain/close, and
owned-buffer release in one 80-byte function table. Callback registration,
streaming, and asynchronous completion extend the size-versioned table in W8;
they are not speculative V1 slots. App-private dynamic module loading remains
in W6. Mobile V1 attaches through the workload-owned component closure and
does not start or load a second runtime.

The implementation is under `product/include/netlift.h`,
`product/src/NetLift.NativeCore`, and `product/src/NetLift.Runtime`. C11/C++17
layout and lifecycle tests cover negotiation, malformed capacity, stale
handles, double close/release, owned error buffers, concurrent cancellation,
busy drain, and zero-work close. The Android workload proof consumes the same
managed endpoint authority through a generated contract adapter; Orders
remains an acceptance fixture rather than runtime logic.

**Primary owner:** native core and managed host.

**Depends on:** W2.

**Inputs from current research:**

- `NetLift.NativeHost.V5` core/lifecycle/facade candidates
- `NetLift.ManagedHost` endpoint authority, module loading, and leases
- buffer ownership, optional negotiation, async lifecycle, and loader identity
  regressions

**Implementation steps:**

1. Reduce the public C ABI to a generic, versioned function table:
   initialize host, create/start endpoint, invoke by numeric ID, cancel,
   register/revoke callbacks, drain, close, and free owned buffers.
2. Keep DTO/member-specific details out of exported C symbols. Generated
   Swift/Kotlin clients call the generic runtime; generated managed dispatch
   handles contract-specific routing.
3. Replace test publishers/providers with one product endpoint authority and
   one product provider interface.
4. Define exact ownership and status behavior for every pointer, buffer,
   token, generation, callback, and close path.
5. Define one process-wide runtime-adapter contract. On platform-bound mobile
   builds, attach to the workload-initialized runtime and shared platform
   binding assembly set from W3A; do not start a second CLR. A generic
   CoreCLR bootstrap may remain only for portable hosts/desktop tests.
   Exclude DNNE `platform.c` and per-module runtime startup.
6. Implement product module loading with explicit shared assembly identity,
   app-private dependency closure, retained noncollectible lifetime, and
   quarantine on uncertain cleanup.
7. Freeze an internal ABI revision only after C11/C++17 layout tests,
   malformed table tests, callback races, cancellation, and zero-lease drain
   pass.

**Deliverables:**

- `include/netlift.h`
- platform-neutral native core library
- managed host package payload
- host ABI and ownership specification

**Exit criteria:**

- OLD/NEW schema fixtures run through the same compiled product host
- no test-only compile define, source, provider, or fixture contract appears
  in product binaries
- close/revoke/ack/release race suite reaches zero live leases
- ABI header compiles as C11 and C++17 and has asserted sizes/offsets
- all failures return defined status/errors; no process abort fallback

**Cleanup gate:** remove superseded private facades and legacy product paths;
retain old layouts only as test fixtures outside package source lists.

### W3A - Prove workload library mode in this repository

**Primary owner:** platform workload/runtime integration.

**Depends on:** W3.

This work package proves the required behavior against installed
`Microsoft.iOS` and `Microsoft.Android` workloads without first forking or
rebuilding `dotnet/macios` or `dotnet/android`. It identifies the smallest
supported upstream changes needed later.

**Implementation steps:**

1. Write one cross-platform design note defining the native-owned-host mode,
   initialization state machine, error model, component manifest, runtime
   flavor boundaries, and no-unload guarantee.
2. Create only `experiments/workload-library-mode/` for the proof. Add a
   cleanup ledger on day one listing every file as promote, regression-test,
   or delete.
3. Pin the SDK/workload versions used by the proof and add an inventory command
   that records installed packs, native libraries, targets/tasks, generated
   files, symbols, and hashes. Do not commit copied runtime packs or captured
   build output.
4. Build the Apple proof from current workload outputs:
   - use a small `net11.0-ios` managed component project so the workload sees
     all platform-bound assemblies and types
   - invoke/capture the existing registrar, linker, runtime configuration,
     and R2R/interpreter/AOT outputs through local MSBuild targets
   - generate local setup/module tables and a small explicit initializer
     facade without emitting or using the workload-generated native `main`
   - package the result as an app-specific framework/static framework
   - link it into a SwiftUI app that owns `@main`, app delegate/scenes, and UI
5. Build the Android proof from current workload outputs:
   - use a small `net11.0-android` managed component project so typemaps, JCWs,
     marshal methods, assembly stores, and app shared libraries are generated
   - collect those outputs through local MSBuild/Gradle integration
   - package them into an app-specific AAR/component without relying on the
     generated runtime `ContentProvider`
   - add a local explicit Java initializer and Kotlin facade that perform the
     current `MonoPackageManager`/`Runtime.initInternal`/
     `ApplicationRegistration` sequence
   - consume the component from a Kotlin/Compose app that owns `Application`,
     activities, and lifecycle
6. Keep the proof contract-neutral at the workload layer. Use one tiny
   portable call plus one existing `UIView`/Android `View` parameter only as
   acceptance fixtures; do not add generator or Orders-specific behavior to
   the workload-mode bootstrap.
7. Test CoreCLR first because it matches the existing host evidence. Record
   MonoVM and NativeAOT as separate follow-up matrices rather than silently
   assuming the same local composition works.
8. Produce a private-hook/gap report that maps every local workaround to the
   ideal `dotnet/macios` or `dotnet/android` property, task output, packaging
   feature, or initialization API.
9. Decide independently for Apple and Android whether the proof is sufficient
   to productize locally, requires a small upstream hook, or requires a
   larger upstream library-mode implementation.

**Deliverables:**

- isolated in-repository workload-library-mode proof
- Apple native-host framework/component output
- Android native-host AAR/component output
- native-owned SwiftUI and Kotlin/Compose proof apps
- deterministic artifact inventories and component manifests
- private-hook/gap report with proposed upstream replacements
- promote/delete cleanup ledger

**Exit criteria:**

- native SwiftUI and Kotlin/Compose apps own their entry points and initialize
  the locally composed workload component explicitly
- registrar/typemap/peer identity works for one existing `UIView` and
  `android.view.View`
- initialization is idempotent, thread-safe, diagnosable, and does not expose
  partial platform-runtime state
- signed physical-device runs pass before either platform-object path is
  promoted beyond experimental
- workload/component manifests are deterministic and package-auditable
- no custom workload installation or forked SDK is required to build the
  proof
- every private hook and copied bootstrap fragment has a documented upstream
  replacement target

**Cleanup gate:** after the decision, promote accepted NetLift-owned code and
focused regressions to final locations, then delete
`experiments/workload-library-mode/` in full. Do not retain parallel proof
apps, captured workload outputs, copied runtime packs, failed packaging
variants, or both local and upstream bootstrap paths.

### W4 - Produce the product Android runtime AAR

**Primary owner:** Android runtime.

**Depends on:** W3 and the Android slice of W3A.

**Inputs from current research:**

- generic local-Maven AAR packaging
- package auditor and runtime/app asset separation
- JNI lifetime, Activity recreation, R8, and high-cycle drain regressions

**Implementation steps:**

1. Create the reusable `runtime-android` AAR from the product native core,
   Kotlin runtime facade, and workload bootstrap integration; do not adapt the
   test provider or copy private workload bootstrap code.
2. Keep app business assemblies, generated typemaps/JCWs, assembly stores,
   and the app-specific workload component out of the published AAR. W5
   generates that component per app/variant and stages it beside the generic
   runtime AAR.
3. Expose the Kotlin `NetLiftRuntime` startup/error API over the workload's
   explicit initializer and validate that the app-specific component
   manifest matches the generic runtime package.
4. Implement generic JNI calls over numeric service/member IDs and byte/value
   payloads; no Orders symbol or generated contract enters the AAR.
5. Implement JNI thread attachment, class-loader use, Java exception
   translation, global-reference leases, callback generations, and
   revoke/drain/release.
6. Generate narrow consumer R8 rules for runtime entry points.
7. Port the AAR content auditor and fail packaging on app DLLs, generated
   contract files, test hooks, duplicate runtime roots, or unexpected native
   libraries.
8. Publish the AAR to the public Maven preview feed.

**Deliverables:**

- `io.github.redth.netlift:runtime-android:<version>`
- package-content auditor
- Android runtime unit/native/instrumentation tests

**Exit criteria:**

- clean external Gradle project resolves the AAR remotely
- Debug and minified Release start one runtime and invoke a generic fixture
- AAR has no app-specific assembly/schema/generated source
- callback/global-reference counters return to baseline after recreation and
  stress
- package audit passes from the downloaded AAR, not the build directory

**Cleanup gate:** delete the copied Android spike host/provider after every
accepted behavior has either moved to runtime code or a focused regression.

**Implementation status on the current branch:**

- `product/android/runtime-android` is a standalone Gradle library with its
  own wrapper and Maven publication. The local preview coordinate is
  `io.github.redth.netlift:runtime-android:0.1.0-alpha.1`.
- The published AAR contains only the generic Kotlin facade, JNI adapter,
  native ABI core, compatibility metadata, and consumer R8 rules. Its auditor
  rejects customer contracts, managed assemblies, workload runtime assets,
  duplicate roots, and unexpected native libraries.
- The workload proof now builds a separate app-specific component AAR
  containing CoreCLR, workload Java peers, typemaps, assembly store, managed
  payload, and generated managed bridge. The sample resolves the generic
  runtime by Maven coordinate and combines the two packages only at the app
  boundary.
- Debug and R8-minified Release packages pass the merged-package audit and run
  the generated Orders client through the numeric generic route. Acceptance
  covers arbitrary UTF-8 request data, managed failure translation, explicit
  idempotent initialization, Android peer identity, native-thread
  attach/detach, repeated Activity recreation, and a stable baseline of two
  process-lifetime JNI global references.
- V1 has no callback or streaming ABI slots, so callback generations and
  revoke/drain behavior are not fabricated in the request/response package.
  They remain a later versioned ABI extension. Existing workload `[Export]`
  delegates used by the app-specific bridge remain explicitly process-rooted.
- The first immutable preview is published from the root of the public
  `Redth/netlift-sdk` Pages repository. The runtime resolves at
  `https://redth.github.io/netlift-sdk/maven/`, and the merged-package auditor
  downloads the public AAR and SHA-256 checksum. No Maven Central account,
  signing key, or Apple credentials are required for this preview.

### W5 - Deliver the Gradle plugin and first usable Maven preview

**Primary owner:** Android build integration.

**Depends on:** W2 and W4.

**Implementation steps:**

1. Implement plugin ID `io.github.redth.netlift`.
2. Add a small `netlift { managedProject.set(...); contractRoots.set(...) }`
   extension.
3. Register AGP variant-aware tasks with declared inputs/outputs:
   - restore/build the managed project
   - invoke the matching compiler
   - invoke the Android workload native-host mode to build the app-specific
     component AAR/bundle
   - generate Kotlin under variant build output
   - add the app-specific workload component to the variant's packaged
     native/assets/Java inputs
   - validate runtime/compiler/manifest versions
4. Add the matching runtime AAR dependency automatically.
5. Make configuration-cache and build-cache behavior explicit; avoid task
   execution during Gradle configuration.
6. Build the Orders Compose sample using only remote Maven/NuGet artifacts.
7. Publish the plugin implementation and marker beside runtime AAR
   `0.1.0-alpha.1`; corrected plugin preview `0.1.0-alpha.2` remains immutable.

**Deliverables:**

- remotely published Gradle plugin and marker
- package-consuming Compose sample
- generated Kotlin client calling real Orders business code

**Exit criteria:**

- a clean checkout needs only standard Gradle Build/Run after one-time .NET
  SDK setup
- implementation-only C# edit rebuilds managed payload but skips projection
- contract edit regenerates Kotlin and recompiles the app
- deleted/corrupt generated output is repaired
- Debug and minified Release emulator runs pass async call, cancellation, and
  one managed-origin callback
- the sample has no copied JNI/C/runtime source and no local repository

**Cleanup gate:** remove handwritten Orders Kotlin codecs/facades from the
customer path; retain only focused stress fixtures that exercise behavior not
covered by the generated client.

**Implementation status on the current branch:**

- `product/android/netlift-gradle-plugin` implements plugin ID
  `io.github.redth.netlift`, typed configuration, AGP variant source/dependency
  wiring, automatic runtime dependency selection, and declared task
  inputs/outputs.
- The plugin bundles the matching .NET metadata compiler and promoted,
  parameterized Android workload component packager. It no longer invokes the
  proof-only builder or requires a NetLift checkout for those tools.
- The public Pages Maven repository serves the implementation JAR and standard
  plugin marker. `0.1.0-alpha.7` is the current plugin preview and selects
  runtime `0.1.0-alpha.2`; it adds cancellable unary calls, native-owned
  callbacks, and demand-aware streams. Earlier previews remain immutable.
- The Android sample has no handwritten Gradle execution task, generated
  source directory, runtime dependency, or file AAR dependency. Applying and
  configuring the published plugin owns those integrations.
- Clean Debug and R8-minified Release builds pass from a fresh Gradle home
  using only the public Maven repository.
- Gradle up-to-date checks, deleted-output repair, and configuration-cache
  reuse pass. Remote build-cache reuse is deliberately disabled until the
  installed .NET/Android workload closure is modeled as a task input.
- The plugin now accepts only the portable `managedProject` and contract roots
  for managed composition. It deterministically requires one public,
  parameterless implementation per rooted service, emits a multi-service
  dispatcher and managed JNI bridge, and synthesizes the `net10.0-android`
  workload project outside source control.
- The superseded handwritten Android managed-component proof was deleted.
  Clean Debug, R8-minified Release, package audits, configuration-cache reuse,
  and emulator execution all pass through the generated component.

### W6 - Settle Apple product-host feasibility and package shape

**Primary owner:** Apple runtime.

**Depends on:** W3 and the Apple slice of W3A.

The existing research does not establish a supported physical-iOS CoreCLR
product. This work package is an explicit decision gate, not permission to
hide that limitation behind the Swift package.

**Implementation steps:**

1. Build the reusable contract-neutral `NetLiftRuntime.xcframework` from the
   fixed NetLift ABI and native facade. Do not extract or preserve a private
   copy of `xamarin_main`, registrar bootstrap, or R2R callback setup.
2. Define the supported preview matrix separately for:
   - macOS
   - iOS simulator
   - signed iOS device
3. Use the Apple workload native-host mode to build a separate app-specific
   managed component framework/static framework containing the managed
   closure, registrar, R2R/interpreter/AOT products, and workload manifest.
   The generic published XCFramework must not contain customer assemblies.
4. Establish the runtime/no-JIT/R2R/interpreter and registrar/link strategy
   for each target. If a supported native Swift-owned CoreCLR path cannot be
   established for device, record iOS device as unsupported and keep the
   package preview limited to proven targets.
5. Prove the Xcode integration that links/embeds the app-specific component.
   SwiftPM build-tool plugins cannot dynamically add a generated framework or
   static archive as a link input, so platform-bound mode requires a
   package-owned Xcode build-tool integration or generated Xcode build phase.
   Do not hide this constraint behind undeclared linker paths.
6. Audit both framework classes: generic runtime versus app-specific managed
   component. Verify slices, linked libraries, runtime assets, symbols,
   customer assembly placement, and absence of test hooks.
7. Upload only the generic contract-neutral XCFramework ZIP and checksum as a
   preview release asset. The app-specific component is generated in the
   consuming app's build output.

**Deliverables:**

- contract-neutral XCFramework
- deterministic app-specific managed component output
- supported Xcode link/embed integration for that component
- explicit Apple support decision and package manifest
- downloaded-artifact audit

**Exit criteria:**

- macOS and/or iOS simulator consumer starts the runtime through the product
  API according to the declared preview matrix
- package contains no Orders code, test provider, debug SPI, or fixture
  registrar output
- app-specific managed component is generated outside source control and is
  linked/embedded without manually copied private bootstrap source
- unsupported device configurations fail at build/configuration time with a
  clear diagnostic
- signed device support is not claimed without an actual device run

**Cleanup gate:** remove test-only Apple provider and fixture adapter sources
from the product package; rejected runtime approaches remain in research
history, not the product tree.

**Completed simulator preview:**

- Published contract-neutral `NetLiftRuntime` `0.1.0-alpha.1` as a Swift
  package backed by an immutable XCFramework ZIP on the public SDK Pages site.
- Declared support only for Apple Silicon iOS Simulator 15.0 or later.
  Physical iOS, macOS, and x86_64 simulator remain unsupported.
- Added deterministic Apple component generation from the portable managed
  project: compiler output, service construction, managed Objective-C adapter,
  synthesized `net11.0-ios` workload project, registrar/R2R/CoreCLR closure,
  and Swift-owned final executable all live outside source control.
- Audited the generic/app-specific boundary and the exact downloaded public
  artifact.
- Executed the generated Swift Orders client against the portable
  `OrdersService` on an iOS 26.5 ARM64 simulator.
- Deleted the superseded checked-in Apple managed component and proof-only
  package builder.

### W7 - Deliver the SwiftPM plugin and first usable Swift preview

**Primary owner:** Apple build integration.

**Depends on:** W2 and W6.

**Implementation steps:**

1. Package the compiler as a versioned SwiftPM executable artifact bundle.
2. Implement `NetLiftPlugin` with declared managed/project/config inputs and
   generated Swift/manifest outputs in the plugin work directory.
3. Read a target-owned `netlift.json`; reject missing/out-of-package paths,
   unknown fields, incompatible config versions, and paths outside the
   declared package/Xcode input boundary.
4. Avoid generated app-specific C. Keep the C ABI generic and precompiled in
   `NetLiftRuntime.xcframework`; generate only Swift DTOs, codecs, service
   clients, and app payload metadata.
5. If Xcode target integration needs staging beyond SwiftPM plugin outputs,
   provide one package-owned command/plugin path rather than customer-authored
   shell phases.
6. Generate `async throws` APIs and typed startup errors over the runtime.
7. Keep the first portable-profile preview on the generic runtime path. If a
   sample enables a platform-bound adapter, invoke the W6 package-owned Xcode
   integration to build and link/embed the app-specific workload component;
   generated Swift source alone is not sufficient to add a linker input.
8. Build the Orders SwiftUI sample using the tagged remote Swift package and
   NuGet-managed project dependency only.
9. Publish/tag the Swift package in the public `Redth/netlift-sdk`
   repository.

**Deliverables:**

- tagged Swift package
- runtime XCFramework binary target
- compiler executable artifact bundle
- package-consuming SwiftUI sample

**Exit criteria:**

- `swift package resolve` and Xcode resolve the remote tag and checksums
- implementation-only C# edit does not regenerate Swift
- contract edit regenerates and recompiles Swift
- missing/corrupt output repairs deterministically
- supported preview targets execute an Orders call and typed error path
- the sample has no copied host/C/runtime source or custom shell generator

**Cleanup gate:** remove the wrapper-staged artifact-only and aggregate-phase
implementations from the product path; preserve their useful behavior as
plugin/package regression tests.

**Completed Swift preview:**

- Published immutable `0.1.0-alpha.5` in `Redth/netlift-sdk` with the generic
  runtime XCFramework and self-contained `osx-arm64` compiler artifact bundle.
  It adds cancellable unary calls, native-owned callbacks, and demand-aware
  streams; `0.1.0-alpha.4` remains immutable.
- Added `NetLiftPlugin`, strict target-owned `netlift.json` parsing, package
  boundary enforcement, sandbox-safe managed build output, and generated
  Swift/manifest outputs.
- Added a remote-tag-consuming Orders package with no copied runtime, host,
  compiler, or generator source.
- Verified implementation-only edits preserve generated Swift content and
  timestamp, public contract edits regenerate, missing output repairs, and
  malformed or out-of-package configuration fails.
- Downloaded the exact Pages-hosted alpha artifacts and verified both SwiftPM
  checksums.
- Fed the exact plugin-generated Swift contract into the W6 package-owned
  component integration and executed successful and typed-error Orders calls
  on the iOS 26.5 ARM64 simulator.
- Physical iOS, macOS, and x86_64 simulator remain explicitly unsupported.

### W8 - Productize async, callbacks, cancellation, and streams

**Primary owner:** shared runtime/lifecycle.

**Depends on:** W5 and W7.

**Implementation steps:**

1. Generalize generated `Task<T>` clients to Kotlin `suspend` and Swift
   `async throws`.
2. Generate native-owned service protocols/interfaces for managed-origin
   requests.
3. Implement registration token + generation, nonblocking revoke, external
   drain wait, stale-reply rejection, and exactly-once final release.
4. Add cold, demand-aware stream projection to Kotlin `Flow` and Swift
   `AsyncSequence`.
5. Keep platform scheduling in platform adapters (`MainActor`/Main
   dispatcher); managed business code remains scheduler-neutral.
6. Replace sample-owned callback adapters with generated/package-owned
   implementations.
7. Publish a new prerelease and rerun remote consumer tests.

**Deliverables:**

- generated async clients, native-service owners, and stream facades
- shared lifecycle specification
- package-consuming Orders confirmation and status stream

**Exit criteria:**

- accept, decline, managed failure, native handler failure, cancellation,
  revoke, close-while-pending, and overflow paths pass on both platforms
- no callback arrives after final release
- close-from-handler cannot await itself
- 10,000-cycle fake transport lifetime tests and bounded real platform stress
  return live counters to baseline

**Cleanup gate:** delete handwritten generated-facade substitutes and all
default-off product feature forks; one lifecycle is selected by the package.

**Completed W8 lifecycle foundation:**

- Added nonblocking managed endpoint close requests that reject new work and
  cancel every registered unary operation.
- Added externally awaited drain completion; `DisposeAsync` now waits until
  operation ownership returns to baseline instead of returning after only
  requesting cancellation.
- Rejected close waits from inside the endpoint's own handler while preserving
  the safe request-close-from-handler path.
- Carried Android unary operation tokens from the coroutine through JNI into
  the generated managed bridge, moved the blocking JNI entry off the caller
  dispatcher, and routed coroutine cancellation back to the endpoint token.
- Carried Apple unary operation tokens from Swift into the generated
  Objective-C bridge, moved the blocking managed entry off the calling actor,
  and routed Swift task cancellation back to the endpoint token.
- Executed real 30-second managed Orders delays from the clean consumers,
  cancelled them after 100 ms, and observed `Cancellation: passed` on the API
  36 ARM64 emulator and iOS 26.5 ARM64 simulator. Android JNI global
  references returned to the process baseline of two.
- Covered pending close, cancellation, self-wait rejection, and 10,000
  sequential operation lifetimes in the product runtime tests.
- Added explicit managed-owned and native-owned contract roots. The compiler
  now emits Kotlin owner interfaces, Swift owner protocols, and typed managed
  native-service proxies while limiting managed implementation discovery and
  dispatch bridges to managed-owned roots.
- Added registration token + generation lifecycle in the shared runtime:
  nonblocking revoke, external release waits, stale-generation reply
  rejection, handler self-wait rejection, bounded overflow, and exactly-once
  transport release.
- Covered native registration reuse and 10,000
  register/invoke/revoke/release cycles with a platform-neutral fake
  transport, and type-checked the generated Swift native-owner protocol.
- Connected Android native-owned services end to end: generated Kotlin
  registration helpers, managed constructor injection and typed proxies,
  adapter-specific P/Invoke, JNI global-reference registrations, operation
  cancellation, generation-safe revoke, and final reference release.
- Executed confirm, decline, native failure, cancellation, and
  revoke-while-pending callback paths on the API 36 ARM64 emulator. The JNI
  global-reference count returned to the process baseline after final release.
- Connected Apple native-owned services through generated route constants,
  constructor-injected managed proxies, exported Swift C entry points,
  cancellation-aware task registrations, generation-safe revoke, and weak
  component-link stubs replaced by the final app's strong symbols.
- Executed confirm, decline, native failure, cancellation, and
  revoke-while-pending callback paths on the iOS 26.5 ARM64 simulator. The
  live Swift registration count returned to zero after final release.

- Added a shared pull-based stream registry with cold subscription creation,
  one item per explicit read, one active read per generation, bounded
  capacity, stale-generation rejection, terminal failure propagation,
  cancellation-aware revoke, and exactly-once async-enumerator disposal.
- Covered cold start, demand, concurrent-read and capacity overflow,
  completion, producer failure, cancellation, stale token reuse, and 10,000
  open/read/revoke cycles in the platform-neutral runtime tests.
- Added `IAsyncEnumerable<T>` contract discovery, manifest and compatibility
  metadata, generated byte-stream dispatch, Kotlin `Flow<T>`, and Swift
  `AsyncThrowingStream<T, Error>` projection. Native-owned streaming remains
  rejected until its reverse transport is implemented.
- Connected Android stream open/read/revoke through JNI and the generated
  managed bridge. The package-consuming API 36 ARM64 emulator received the
  three Orders status updates, cancelled a pending 30-second stream, and
  reported `NETLIFT_ANDROID_STREAM_PASSED`.
- Connected Apple stream open/read/revoke through the generated Objective-C
  bridge. The generic Swift unfolding transport performs each read in the
  consumer task, so cancellation revokes the managed subscription without a
  detached producer. The iOS 26.5 ARM64 simulator received the three Orders
  status updates, cancelled a pending 30-second stream, and reported
  `NETLIFT_APPLE_STREAM_PASSED`.

**Completed W8 release:**

- Published Android runtime `0.1.0-alpha.2`, Gradle plugin
  `0.1.0-alpha.7`, and Swift package `0.1.0-alpha.5` through
  `Redth/netlift-sdk`.
- Rebuilt the Android consumer from an empty Gradle home using only the public
  Pages Maven feed, then reran the emulator stream, callback, and cancellation
  acceptance.
- Resolved the Swift package from the public `0.1.0-alpha.5` tag in a clean
  package checkout, compiled its generated streaming and native-owner
  projections, and reran the iOS simulator stream, callback, and cancellation
  acceptance with that remotely generated contract.

### W9 - Expand types and enforce compatibility

**Primary owner:** compiler/type system.

**Depends on:** W8.

**Implementation steps:**

1. Implement built-in adapters for `Guid`/UUID, date/time, byte buffers,
   URI/URL, collections, enums, nullable/optional fields, and result errors.
2. Record adapter ID/version, bridge shape, ownership, and required runtime
   capability in the manifest.
3. Implement additive optional-field negotiation without weakening exact-hash
   startup for peers that do not opt in.
4. Define compatibility policy and CLI output for additions, removals,
   renames, ID changes, required/optional changes, adapter changes, and ABI
   capability changes.
5. Add `netlift compatibility` to CI examples and package release validation.
6. Publish old-client/new-runtime and new-client/old-runtime test packages.

**Deliverables:**

- documented supported type matrix
- compatibility CLI and lock update workflow
- cross-version package fixture matrix

**Exit criteria:**

- C#, Swift, and Kotlin round-trip goldens agree for every adapter
- malformed, unknown-version, ownership, and boundary-value cases fail safely
- compatible additions work only after bilateral capability negotiation
- breaking changes fail CI with source/member diagnostics

**Cleanup gate:** every adapter is implemented through the registry; reject
backend-specific one-off type mappings outside adapter modules.

**Completed W9 foundation:**

- Added manifest format 3 adapter metadata with stable adapter ID/version,
  .NET/Kotlin/Swift type names, canonical wire shape, ownership, and required
  runtime capability.
- Added `netlift.uuid` version 1 for `System.Guid`, `java.util.UUID`, and
  `Foundation.UUID`, encoded as a lowercase JSON UUID string.
- Added versioned timestamp, byte-buffer, and URI adapters for
  `DateTimeOffset`/`OffsetDateTime`/`Date`, `byte[]`/`ByteArray`/`Data`, and
  `Uri`/`URI`/`URL`.
- Added bounded non-null scalar and enum collection fields projected as Kotlin
  `List<T>` and Swift `[T]`, while explicitly rejecting nested DTO,
  adapter-element, nullable-element, and nested-list shapes.
- Added an additive Orders UUID round-trip method without changing existing
  service/member IDs, and recorded the adapter in compatibility lock format 2.
- Added `netlift compatibility <previous-lock> <current-lock>` with nonzero
  failure and source/member/type/adapter diagnostics.
- Added bilateral `optional-fields-v1` lock negotiation: nullable additions
  are compatible only when both peers opt in, while required additions remain
  breaking. Managed old-request/new-runtime and new-response/old-client
  fixtures verify the JSON boundary behavior.
- Verified managed dispatch plus real Android emulator and iOS simulator UUID,
  timestamp, byte-buffer, URI, integer-list, and enum-list round trips while
  retaining the stream, callback, and cancellation gates.
- Added `netlift.result-error` version 1. Android surfaces handler failures as
  `NetLiftRemoteException`; Swift surfaces them as `NetLiftRemoteError`. Both
  preserve the managed status and message while cancellation remains native.
- Added executable malformed UUID, timestamp, base64, and URI decoding tests
  for Swift and Android. Kotlin base64 decoding now validates the canonical
  alphabet and padding before using the Android decoder.
- Added package-consumer cross-version fixtures on Android and Apple:
  legacy requests without the optional field execute against the current
  runtime, and current generated clients decode legacy responses without the
  optional field. Lock tests reject unnegotiated optional additions plus
  adapter version, ownership, and capability mismatches.

**W9 implementation and release complete:**

- Published Android runtime `0.1.0-alpha.3`, corrected Gradle plugin
  `0.1.0-alpha.9`, and Swift package `0.1.0-alpha.6` through
  `Redth/netlift-sdk`. Plugin `0.1.0-alpha.8` remains immutable but is
  superseded because its default runtime selection still referenced
  `0.1.0-alpha.2`.
- Rebuilt the Android consumer from an empty Gradle home using only the public
  Pages Maven feed, then reran typed-error, cross-version, malformed-adapter,
  value-adapter, collection, stream, callback, and cancellation acceptance.
- Resolved the Swift package from the public `0.1.0-alpha.6` tag in a clean
  package checkout, ran its generated client, and rebuilt the iOS simulator
  component using the remotely generated Swift contract. All W9 and W8
  acceptance markers passed.

### W10 - Add optional platform-object adapters

**Primary owner:** platform bindings.

**Depends on:** W9 and the accepted platform slice of W3A.

#### Apple `UIView` slice

1. Add a first-party `net11.0-ios` adapter to the app-wide registrar,
   native-link, and R2R/interpreter closure.
2. Generate:

   ```text
   Swift UIView
     -> Objective-C UIView *
     -> opaque NetLift ABI
     -> Runtime.GetNSObjectChecked<UIView>
     -> lifted C# API
   ```

3. Declare borrowed/retained ownership and `@MainActor` requirements.
4. Test same-peer identity, wrong-class rejection, sync borrow, async retain,
   forced ARC/GC, and return of the same native object.
5. Test managed `[Export]`/subclass support separately; do not imply it from
   accepting an existing UIKit object.

**Progress:** The workload-owned `NetLift.ApplePlatform` foundation now
provides main-thread validation, same-peer lookup through the shared
Objective-C runtime, borrowed leases, retained reference-counted leases, and
generation-safe cleanup. Generated `UIView` facade wiring and simulator/device
acceptance remain before this slice can be advertised as supported.

#### Android `View` slice

1. Add a first-party `net11.0-android` adapter to the app-wide workload
   registration, typemap, trimming, and packaging closure.
2. Generate:

   ```text
   Kotlin android.view.View
     -> JNI JNIEnv* + jobject
     -> opaque NetLift ABI
     -> Java.Interop Android.Views.View peer
     -> lifted C# API
   ```

3. Enforce local/global/weak-global reference and UI-thread rules.
4. Test same-peer identity, wrong-class rejection, async retention, GC
   pressure, Activity recreation, R8, and reference counts returning to
   baseline.
5. Test managed Java-callable subclasses separately; do not imply them from
   accepting an existing `View`.

**Deliverables:**

- optional `NetLift.ApplePlatform` and/or `NetLift.AndroidPlatform` adapter
  packages owned by the corresponding workload build
- generated strongly typed platform-object facade support
- physical-device identity/ownership/lifetime regression suites
- support-matrix entries that distinguish existing-object parameters from
  managed-created subclasses/callback targets

**Exit criteria for either platform:**

- platform adapter is compiled into the final workload closure and does not
  privately load platform binding assemblies
- object identity, ownership, thread affinity, and cleanup are verified on a
  physical device before the adapter is advertised as supported
- if the workload cannot be safely composed with the host, the adapter
  remains excluded while portable projection continues

**Cleanup gate:** no platform-object proof project remains in the product
repository; accepted behavior lives in the platform adapter, sample, and
focused tests.

### W11 - Complete device, trust, and stable-release gates

**Primary owner:** release/platform security.

**Depends on:** all capabilities selected for the stable release.

**Implementation steps:**

1. Run signed physical-device matrices for supported Android API/ABI and Apple
   OS/device combinations.
2. Validate Android AAB/split delivery, signer lineage, installed runtime
   assets, R8, cold start, offline launch, update, and Activity/process
   lifecycle.
3. Validate Apple signing, runtime/no-JIT mode, packaged framework closure,
   offline launch, scene teardown, background/foreground, and distribution
   path.
4. Audit native and managed dependency closure, licenses, symbols, package
   manifests, and app-specific payload isolation.
5. Run remote package consumers against release-candidate feeds/tags.
6. Promote exact release-candidate bytes to stable channels without rebuild.
7. Tag source and distribution repositories and publish support/limitations
   documentation.

**Deliverables:**

- stable package set
- release manifest/provenance and support matrix
- physical-device and remote-consumer release evidence

**Exit criteria:**

- all advertised platforms pass signed physical-device execution
- package consumers reproduce the getting-started guide from clean machines
- stable artifacts match release-candidate checksums exactly
- no unresolved test-only path, fixture payload, or experimental feature flag
  exists in product packages

**Cleanup gate:** remove prerelease-only compatibility branches, stale package
coordinates, superseded docs, and temporary release scripts. The stable tree
contains only current implementation, tests, samples, and durable docs.

## Pull-request completion checklist

Every implementation pull request must answer:

- Which final package owns this code?
- Which current research proof or requirement justifies it?
- Which clean package-consuming test proves it?
- Does it add a second implementation path or feature flag? If so, what is
  removed before merge?
- Are all generated/runtime outputs outside source control?
- Does the package-content audit prove no fixture or app-specific payload
  leaked?
- What public artifact/version first contains the change?
- What documentation or support matrix changed?

A PR that introduces an experiment without either deleting it or promoting it
into final package/test ownership is incomplete.

## Suggested first implementation decisions

### Decision 1: no attributes required for the common case

A developer should not have to add per-method or per-type attributes to use common DTO and service APIs.

### Decision 2: strict eligibility instead of silent omission

Unsupported API shapes should fail the build with actionable diagnostics. This creates trust rather than silent boundary breakage.

### Decision 3: stable contract IDs are mandatory

Without a compatibility lock, generated methods and DTOs will drift across packaging versions and break native consumer builds.

### Decision 4: the generator should operate on metadata, not runtime discovery

Operating against compiled assemblies and metadata is safer than invoking arbitrary user code during generation.

### Decision 5: use publishable packages, not ad hoc scripts

The developer experience should feel like “install package + build app,” not “run a custom generator and manually wire generated sources.”

## Risks and mitigations

### Risk: too much of the CLR leaks into native APIs

Mitigation:

- define an explicit supported subset
- fail on unsupported members
- keep an explicit compatibility manifest

### Risk: SwiftPM generated C is awkward

Mitigation:

- freeze a generic C ABI inside the runtime XCFramework
- generate app-specific Swift only, not app-specific C/header source
- use one package-owned staging command only if app payload staging cannot be
  expressed as declared plugin output

### Risk: version drift between .NET contract and native generated code

Mitigation:

- version lock and schema hashing
- generated-artifact validation during build
- compatibility checks in CI

### Risk: too much custom build logic in app repos

Mitigation:

- build reusable Gradle/Maven and SwiftPM plugins
- hide generation behind standard project configuration

### Risk: package publishing is postponed until architecture feels complete

Mitigation:

- build feed/tag automation in W1
- publish every accepted vertical slice as an immutable prerelease
- require clean remote-feed/tag consumers before a work package exits

### Risk: research code and alternate implementations accumulate

Mitigation:

- keep research in this repository and product code in the clean repository
- prohibit a product `spikes/` directory
- require a cleanup gate and package owner for every implementation PR
- keep compatibility history as fixtures, not compiled alternate product paths

### Risk: Apple device hosting remains infeasible or unsupported

Mitigation:

- make W3A and W6 explicit feasibility/support gates
- publish only the macOS/simulator support that is actually proven
- let Android and portable projection proceed independently
- do not hide a runtime architecture pivot inside packaging work

### Risk: local workload proofs become permanent product infrastructure

Mitigation:

- isolate all proof code under one temporary root with a day-one cleanup
  ledger
- never commit runtime packs or captured generated output
- inventory every private target, symbol, and copied bootstrap fragment
- promote accepted code once into final source/tests and delete the proof tree
- open upstream issues from the measured gap report before treating private
  workload behavior as stable
- require an explicit long-term maintenance decision before stable support if
  supported upstream hooks are unavailable

## Success criteria

The product is successful when:

- a developer adds the published Gradle plugin or tagged Swift package and a
  tiny managed-project configuration block
- a .NET library can be consumed from Android or SwiftUI without handwritten interop glue
- generated Kotlin/Swift APIs are idiomatic and compile cleanly
- unsupported shapes fail with actionable diagnostics
- API compatibility is enforced across versions
- the Orders apps build and run from clean checkouts using only public
  NuGet/Maven/SwiftPM dependencies
- implementation-only edits skip projection while contract edits regenerate
  deterministically
- downloaded package audits prove runtime artifacts are contract-neutral and
  contain no fixture/test sources
- every advertised platform has an explicit support level backed by its
  required simulator/emulator/device evidence
- the product repository contains one current implementation per
  responsibility and no permanent experiment tree

## Recommendation

Begin in a clean product repository and use this repository only as a
read-only source of proven concepts and regression requirements. Build the
release pipeline before the first feature slice, then deliver Android and
Apple as independently publishable vertical slices over the same compiler and
host core.

The immediate next step should be:

1. execute W0 and create the clean product repository
2. execute W1 and prove public prerelease restore/resolve for every ecosystem
3. migrate the compiler vertical slice in W2 without copying the research tree
4. freeze the first generic host contract in W3
5. publish the Android W4/W5 and Apple W6/W7 slices as soon as each clean
   remote consumer passes
