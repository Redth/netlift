# Share a .NET service with your native app

The complete project layout, `.csproj`, C# contract and implementation, and
native setup now live together in the
[root README](../README.md#1-write-your-net-service).

The .NET library remains ordinary managed code without NetLift attributes or
package references. Select narrow public service roots and use one non-null
request DTO with a `Task<ResponseDto>` or `IAsyncEnumerable<ItemDto>` result.
For simple automatic hosting, provide exactly one public implementation with
a public parameterless constructor.

The [Hello World contract](../product/samples/HelloWorld.Contracts/HelloWorld.cs)
and [Android app](../product/samples/HelloWorld.Android/README.md) are the
smallest real managed example. The
[reusable setup skill](../skills/netlift-native-app/SKILL.md) also covers
existing .NET libraries and native apps.
