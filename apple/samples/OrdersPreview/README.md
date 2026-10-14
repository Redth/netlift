# Swift package example

This example consumes the public NetLift Swift package `0.1.0-alpha.10`.
It demonstrates one-time runtime transport installation, no-argument clients,
typed native-service registration, and pull-driven streams without needing
an iOS app. It also verifies missing/duplicate installation diagnostics and
explicit transport injection for isolated tests.

## Run

Use Swift tools 6.0 or later and the .NET SDK pinned in the repository-root
`global.json`. From the repository root:

```bash
swift run --package-path apple/samples/OrdersPreview OrdersPreview
```

Expected output:

```text
NetLift Swift runtime defaults, callback and demand-aware stream preview passed.
```

## Read the example

| File | Purpose |
| --- | --- |
| [Package.swift](Package.swift) | Public package dependency and build-tool plugin |
| [netlift.json](Sources/OrdersPreview/netlift.json) | Managed project and service roots |
| [Orders.cs](Managed/Orders/Orders.cs) | Plain .NET contracts and service implementation |
| [OrdersPreviewApp.swift](Sources/OrdersPreview/OrdersPreviewApp.swift) | Generated client calls, callback registration, and stream checks |

The Swift code uses an **in-memory test transport**. The plugin builds the
.NET contract to generate source, but the running example does not invoke
the C# implementation. For iOS runtime integration, follow the
[Swift guide](../../../docs/swift.md).
