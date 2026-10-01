# Async Orders business library (prototype)

This is an ordinary `net11.0` class library. `Contract.cs` opts one
`SubmitAsync` route and one native `ConfirmAsync` service into the
`AsyncCallbackV1` generator profile. `OrdersService` calls the generated
native-service proxy from managed business code before returning a typed
result; the native UI chooses whether to accept or decline. Its frozen
old-profile schema hash is
`d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b`.
The generated dispatcher now invokes only the service under the
validated invocation context; it does not issue a second confirmation.

Build with `dotnet build samples/Orders.AsyncBusiness/Orders.AsyncBusiness.csproj
-c Release`. Run the package-free business-origin callback checks with
`dotnet run --project
tests/NetLift.Samples.AsyncBusiness.Smoke/NetLift.Samples.AsyncBusiness.Smoke.csproj
-c Release`; they cover approval, decline, pre-cancellation, and the
frozen schema hash. Generated C#, manifest, and native projections belong in
`obj/` or the native build's derived artifacts, not in this library's
authored source. The test-only old AsyncRuntime managed host references
this project; its `OrdersSampleAdapter.cs` supplies the fixture-shaped
entry point expected by the existing C/CoreCLR adapter. The new-profile
managed fixture remains separate for old/new compatibility checks.
The existing Xcode SwiftUI and Gradle Compose proof apps stage this DLL
in their OLD managed direction; their NEW managed direction uses the
evolved fixture. Both OLD paths have run on an iOS simulator and
Android emulator with a real managed-origin confirmation, but one
first-run Android Debug Activity-recreation test timed out after
recreation and a positive reply while awaiting the terminal UI state.
The Android app now preserves terminal state across interim reply
updates, tests both orderings, and passed three independent cold-process
Old Debug reruns. The initial failure remains recorded because its
exact interleaving was not captured. A related cancel-acknowledgement
overwrite is also guarded and independently passes both ordering unit
tests and Old/New emulator lifecycle runs.
The exact test-only native build/run commands and results are in the
[Apple spike](../../docs/spikes/apple.md) ("Canonical Orders.AsyncBusiness
Apple consumer") and [Android spike](../../docs/spikes/android.md)
("Orders.AsyncBusiness" sample-backed section). Both require platform
toolchains and isolated build/artifact roots.

This does **not** provide a drop-in mobile host. The current SwiftUI and
Compose runners own substantial handwritten CoreCLR/V2 adapter,
packaging, and UI lifecycle glue; the V2 adapter's ACTIVE gate is
test-owned. The opt-in async profile is still bounded to the frozen
Orders/Confirmation route, flat DTOs, and one typed callback; it is not
yet a generator for arbitrary async service shapes. This library does
not implement the event/stream or arbitrary delegate profiles, nor
does it prove physical-device no-JIT, production
signing, authenticated delivery, or a product ABI.
