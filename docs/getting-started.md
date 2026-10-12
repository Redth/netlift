# Share a .NET service with your native app

[Home](../README.md) | [Kotlin / Android](kotlin.md) | [Swift / iOS](swift.md)

Your native app owns its UI and lifecycle. Your .NET project is an ordinary
class library containing the business logic and the public contracts you
want to call.

## Create the library

Create `Contracts/Contracts.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Add `Contracts/HelloService.cs`:

```csharp
namespace Example.Contracts;

public sealed record GreetingRequest(string Name);
public sealed record GreetingResponse(string Message);

public interface IHelloService
{
    Task<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class HelloService : IHelloService
{
    public Task<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GreetingResponse($"Hello, {request.Name}!"));
    }
}
```

Build it:

```bash
dotnet build Contracts/Contracts.csproj
```

There is no NetLift package reference or attribute in this library. The
native build integration takes the project path and the fully qualified
contract name, `Example.Contracts.IHelloService`, as inputs.

The Android component generator discovers the service implementation.
For this first example, keep exactly one public implementation with a
public parameterless constructor.

## Connect the native app

**Android:** follow the [Kotlin guide](kotlin.md). The Gradle plugin
generates the client and packages the managed component automatically.
The [Hello World sample](../product/samples/HelloWorld.Android/README.md)
is a complete app with this shape, including toolchain setup and launch
commands.

**Swift:** follow the [Swift guide](swift.md). Keep the managed project
inside the consuming Swift package. The SwiftPM plugin generates the
client; an iOS app additionally needs a managed component and runtime
transport. The guide distinguishes source generation from that packaging
step.

## What gets generated

| C# | Kotlin | Swift |
| --- | --- | --- |
| Request/response records | Typed DTOs | Typed DTOs |
| `Task<T>` service method | Suspending client method | `async throws` client method |
| `IAsyncEnumerable<T>` | Cold `Flow<T>` | Pull-driven `AsyncSequence` |
| Native-owned service interface | Owner interface and registration helper | Owner protocol and registration helper |

Start with a request/response service. The
[Swift package example](../product/apple/samples/OrdersPreview/README.md)
demonstrates callbacks and streams once you are ready for those.

## Toolchain and support

The checked-in samples use the .NET SDK pinned in
[`global.json`](../global.json). The plain library targets `net10.0`;
the generated mobile components use .NET 11 platform workloads.

The verified preview targets are API 36 ARM64 Android emulators and
Apple Silicon iOS simulators. Physical-device support is not validated.
Use the platform guide for its additional build requirements.
