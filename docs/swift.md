# Swift / iOS

The complete setup and code now live in the root
[README: Swift](../README.md#3-connect-swift). It includes the C# service,
Swift package, `netlift.json`, one-time transport installation, and a
complete client preview.

The public package is `0.1.0-alpha.8`. Install the app host's transport once
per generated module, then use `try HelloServiceClient()`. Missing or duplicate
installation throws; explicit initializer injection remains available.

**Source generation is not runtime hosting.** The
[iOS component steps](../README.md#run-managed-code-in-an-ios-app) describe
the separate source-owned build required to execute C# on an Apple Silicon
simulator. SwiftPM alone does not package the managed app or supply a host.

The [Orders package example](../product/apple/samples/OrdersPreview/README.md)
uses an in-memory transport and verifies clients, callbacks, and streams.
It does not execute C#. Platform packaging details remain in the
[product reference](../product/README.md#apple-runtime-and-generated-component).
