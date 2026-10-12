# Swift / iOS

[Home](../README.md) | [.NET service](getting-started.md) | [Kotlin / Android](kotlin.md)

The Swift package plugin generates Swift DTOs and `async throws` clients
from your .NET contract. SwiftUI and Xcode still own the UI and app lifecycle.

**Current preview:** SwiftPM source generation and iOS runtime packaging
are separate steps. Adding the package does not, on its own, start .NET
or embed your managed library into an iOS app.

## Project layout

Keep the .NET project inside the Swift package. These examples use the
library from the [shared service guide](getting-started.md):

```text
MyApp/
  Package.swift
  Contracts/
    Contracts.csproj
    HelloService.cs
  Sources/
    MyApp/
      netlift.json
      MyApp.swift
```

## Add the package and plugin

Use Swift tools 6.0 or later. Add this dependency to `Package.swift`:

```swift
.package(
    url: "https://github.com/Redth/netlift-sdk.git",
    exact: "0.1.0-alpha.7"
)
```

Attach the build-tool plugin to the target containing your app's Swift
source:

```swift
.executableTarget(
    name: "MyApp",
    exclude: ["netlift.json"],
    plugins: [
        .plugin(name: "NetLiftPlugin", package: "netlift-sdk"),
    ]
)
```

See the [complete example package manifest](../product/apple/samples/OrdersPreview/Package.swift)
for how these entries fit into `Package(...)`.

## Point the plugin at the .NET project

Create `Sources/MyApp/netlift.json`:

```json
{
  "version": 1,
  "managedProject": "../../Contracts/Contracts.csproj",
  "contractRoots": ["Example.Contracts.IHelloService"],
  "swiftModule": "MyApp"
}
```

`managedProject` is relative to `netlift.json`, not to `Package.swift`.
Build with `swift build`. The plugin builds the managed contract and adds
generated Swift source to your target.

## Use the generated client

The client accepts a transport that connects its typed calls to your
managed runtime:

```swift
import Foundation

func greet(using transport: any NetLiftTransport) async throws -> String {
    let client = HelloServiceClient(transport: transport)
    let response = try await client.greet(
        request: GreetingRequest(name: "World")
    )
    return response.message
}
```

The generated transport protocol is not a runtime initializer. An
in-memory transport is useful for UI previews and client tests, but
does not execute your C# service.

## Run the package example

The [Orders Swift package example](../product/apple/samples/OrdersPreview/README.md)
uses the public package and checks generated clients, native-service
registration, and pull-driven streams:

```bash
swift run --package-path product/apple/samples/OrdersPreview OrdersPreview
```

It uses an in-memory transport. Its passing output proves package
resolution and generated Swift behavior, not execution of managed
business logic in an iOS app.

## Embed .NET in an iOS app

The published runtime contains the contract-neutral C ABI; it does not
contain your managed assemblies, registrar output, or CoreCLR/R2R assets.
Those must be built for the consuming app.

The current source-owned integration is
[`product/apple/build_component.py`](../product/apple/build_component.py).
It generates the managed bridge and packages the workload-built closure
with native Swift app source. The
[Apple component build reference](../product/README.md#apple-runtime-and-generated-component)
contains its full invocation and simulator acceptance path. This is not
yet a one-step SwiftPM/Xcode app template.

Runtime execution is validated only on Apple Silicon iOS simulators.
The package's iOS 15 minimum deployment target does not imply
physical-device, macOS-runtime, or Intel-simulator support.
