# Source ownership before packaging

This map describes the **local research baseline**, not a set of supported
NuGet, SwiftPM, or Maven packages. A file being in `src/` means it is
source-owned and reusable for experiments; it does not mean a product ABI,
installation flow, authenticated loader, or device release is accepted.

| Location | Current responsibility | Boundary before a product package |
| --- | --- | --- |
| [`src/NetLift.Contracts`](../src/NetLift.Contracts/) | Opt-in service/DTO/native-service attributes and managed bridge contracts | The `AsyncCallbackV1` profile is bounded to the frozen Orders-like route; do not imply arbitrary delegate/event shapes are generated. |
| [`src/NetLift.Generator`](../src/NetLift.Generator/) | Roslyn contract and dispatcher emission | Generated C# and manifest live in derived build output; keep generator diagnostics and schema compatibility checks as build gates. |
| [`src/NetLift.Projection`](../src/NetLift.Projection/) and [`src/NetLift.SchemaDiff`](../src/NetLift.SchemaDiff/) | Native projection and schema-diff tools | These are command-line prototypes, not automatic Xcode/Gradle consumer plugins. |
| [`src/NetLift.Negotiation`](../src/NetLift.Negotiation/) | Manifest/preflight rules and compatible-start reference | The old/new reference proof is not authenticated peer selection or product activation. |
| [`src/NetLift.ManagedHost`](../src/NetLift.ManagedHost/) | Managed endpoint authority, binding, transport and AppHost V1 registration/lease internals | Captured registration is internal/default-off; the existing public table remains path-based. Source-owned authority is not a signed-package or product `ACTIVE` decision. |
| [`src/NetLift.NativeHost.V5`](../src/NetLift.NativeHost.V5/) | Contract-neutral V2 core/lifecycle/accessor and private CoreCLR/bootstrap/coordinator experiments | `netlift_host_v5.h` explicitly declares a **provisional, unexported** facade. The private bootstrap and test peer loader are not a supported distributable host. |
| [`samples/Orders.AsyncBusiness`](../samples/Orders.AsyncBusiness/) | Plain `net11.0` developer-authored annotated contract and service | This library is the clearest current API-authoring example, but supplies no drop-in mobile host. |
| [`tests/`](../tests/) and [`spikes/`](../spikes/) | Assertions, test hosts, native apps, platform-specific packaging and negative controls | Keep fixture assemblies, test signing, private providers and copied runtime assets out of a product package. |

The Apple [native SwiftUI proof](../spikes/apple/NativeV5SwiftHost/)
stages the common V2 core but still compiles `TestOnly` CoreCLR/provider
inputs. The Android
[runtime proof](../spikes/android/NetLift.AndroidRuntime/) compiles an
explicitly test-only provider and hard-codes the async host assembly.
Their OLD direction stages `Orders.AsyncBusiness`; NEW remains a separate
fixture compatibility control. Neither app is a small consumer template.
The MAUI-owned SwiftUI alternative is a different host-ownership
architecture and remains build-only.

Before publishing any runtime package, separate the product provider from
these proof apps, define authenticated package/runtime/module identity,
make the private transaction reach a rollback-safe product `ACTIVE`,
freeze the exported ABI, and prove clean native consumer builds and
physical-device gates. Preserve the existing test packages as controls
instead of silently substituting them into the product graph. Consult
the [delivery plan](plan.md) and [current gate ledger](status.md) for the
order and exact evidence.
