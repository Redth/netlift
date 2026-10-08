# NetLift product slice

This directory is the clean implementation boundary for the product described
in `experiments/netlift-interop-product/PLAN.md`. It intentionally does not
reference research projects, spikes, or proof artifacts.

The first vertical slice compiles an ordinary public .NET service contract
without NetLift attributes into:

- a deterministic canonical manifest and compatibility lock
- stable content-derived service, method, type, and field IDs
- generated Kotlin DTOs and async client
- generated Swift DTOs and async client
- a managed dispatcher that invokes the same service/member IDs

Build and run the slice:

```bash
dotnet build product/NetLift.Product.slnx
dotnet run --project product/tests/NetLift.Compiler.Tests
dotnet run --project product/src/NetLift.Tool -- product/netlift.json
```

Generated output stays under ignored `product/artifacts/`. The compatibility
lock in `product/contracts/` is source and should be reviewed when the public
contract changes.

The solution build also regenerates and compiles the managed dispatcher.
`NetLift.Compiler.Tests` verifies byte determinism across repeated runs and
different absolute assembly paths. The generated Swift source is compiled with
`swiftc` in local validation. Kotlin compilation becomes a required gate when
the projection is connected to the package-consuming Android sample under
`experiments/workload-library-mode/android/native-app`.

The initial codec profile carries UTF-8 JSON inside the generated
`ByteArray`/`Data` transport seam. It is intentionally replaceable without
changing projected Kotlin/Swift APIs or stable route IDs. The Android workload
adapter wraps each managed response in an explicit success/error envelope so
managed exceptions become Kotlin exceptions instead of crossing JNI.
