# Native-owned workload component contract

## Goal

The proof composes workload-generated managed/platform artifacts into a
component initialized by a native-owned application. It does not introduce a
second CLR, replace the workload peer runtime, or make platform assemblies
runtime-loadable plugins.

## Initialization state machine

Each process has one component instance:

```text
uninitialized -> initializing -> ready
                           \-> failed
```

Rules:

- Initialization is explicit and idempotent.
- The first caller performs initialization.
- Concurrent callers wait for the same operation and observe the same result.
- Calls made before `ready` fail without entering projected user code.
- `failed` retains a structured diagnostic; it is not converted to success by
  later calls.
- Version 1 has no runtime unload or restart operation.
- NetLift endpoint drain/release is separate from process runtime shutdown.

## Native app ownership

The host application owns:

- SwiftUI `@main`, app delegate, scenes, windows, and UI lifecycle
- Android `Application`, activities, services, receivers, and process
  lifecycle
- final signing, packaging, and store submission

The workload component owns:

- one managed runtime closure
- the workload's registrar or typemap/JCW closure
- the Objective-C or Java.Interop peer runtime
- generated assembly/module registration
- runtime configuration and managed assets

## Component output

Each platform build emits:

1. native framework/AAR/component payload
2. `netlift-workload-component.json`
3. symbol and package-content audit
4. private-hook inventory for unsupported workload integration points

The manifest conforms to
[`shared/component-manifest.schema.json`](shared/component-manifest.schema.json).
It identifies the exact SDK, workload manifests, runtime flavor, target, and
payload hashes used by the component.

## Runtime flavor

The first proof uses CoreCLR because it matches the repository's existing
host evidence. MonoVM and NativeAOT are separate compatibility matrices and
must not be inferred from a CoreCLR result.

## Platform peer rule

An existing `UIView` or Android `View` may cross the NetLift ABI only after
the workload peer runtime is ready. The ABI carries an opaque native
reference; the workload resolves the managed peer and enforces identity,
ownership, class validity, and thread requirements.

## Error boundary

Initialization and projected calls return structured status/error data.
Native exceptions, pending JNI exceptions, and managed exceptions must be
translated before returning through the stable NetLift ABI. Proof bootstrap
code must not use an abort as a success-shaped fallback.
