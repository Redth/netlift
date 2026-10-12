# NetLift

**Native Swift and Kotlin apps. Shared .NET business logic.**

NetLift generates native clients from ordinary C# service contracts. Keep
your UI and app lifecycle in SwiftUI or Kotlin/Compose, and run your .NET
code in the same process. No MAUI UI, web service, or NetLift attributes
required.

## Start building

1. [Write your .NET service](docs/getting-started.md).
2. Connect it to [Kotlin / Android](docs/kotlin.md) or
   [Swift / iOS](docs/swift.md).
3. Try the [Android Hello World app](product/samples/HelloWorld.Android/README.md).

Your shared contract can be as small as:

```csharp
public sealed record GreetingRequest(string Name);
public sealed record GreetingResponse(string Message);

public interface IHelloService
{
    Task<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default);
}
```

NetLift generates typed clients and DTOs, so native callers use their own
language's async APIs:

```kotlin
// Kotlin, from a coroutine
val greeting = client.greet(GreetingRequest(name = "World"))
```

```swift
// Swift, from an async function
let greeting = try await client.greet(request: GreetingRequest(name: "World"))
```

The [getting-started guide](docs/getting-started.md) includes the C#
implementation and project file. The platform guides cover generation and
connecting the client to its runtime transport.

## Packages

| Integration | Current preview |
| --- | --- |
| Android Gradle plugin | `io.github.redth.netlift` (`0.1.0-alpha.11`) |
| Android runtime (added by the plugin) | `io.github.redth.netlift:runtime-android` (`0.1.0-alpha.3`) |
| Swift package | `NetLift` (`0.1.0-alpha.7`) |

Packages and installation snippets are published in
[netlift-sdk](https://github.com/Redth/netlift-sdk). Pin preview versions;
published package versions are immutable.

## Samples

| Sample | What it demonstrates |
| --- | --- |
| [Android Hello World](product/samples/HelloWorld.Android/README.md) | A native Android app calling a real .NET greeting service through the public Gradle plugin |
| [.NET greeting service](product/samples/HelloWorld.Contracts/HelloWorld.cs) | A plain library with request/response DTOs and an async service |
| [Swift package example](product/apple/samples/OrdersPreview/README.md) | Generated Swift clients, callbacks, and streams with an in-memory test transport |

## Preview scope

Android has been validated on API 36 ARM64 emulators, and the iOS runtime
on Apple Silicon simulators. Physical devices and stable releases are not
validated yet. Android generates and packages the managed component during
the Gradle build; SwiftPM generates client source, with a separate explicit
step for iOS managed-component packaging.

## Working on NetLift

[Product source and build instructions](product/README.md) are the current
implementation reference. Earlier feasibility notes live under `docs/`,
`experiments/`, and `spikes/`; they are not app setup instructions or the
current support contract.
