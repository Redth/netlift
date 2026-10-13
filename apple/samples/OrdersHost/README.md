# Swift-owned workload host

Open `OrdersHost.xcodeproj`, select an Apple Silicon iOS simulator, and
press Run. The native target automatically builds the .NET Orders library,
generates clients and the managed bridge, links the workload runtime, and
embeds the managed payload before signing. No separate host build is needed.

From the repository root:

```bash
xcodebuild \
  -project apple/samples/OrdersHost/OrdersHost.xcodeproj \
  -scheme OrdersHost \
  -destination 'platform=iOS Simulator,id=<simulator-udid>' \
  build
```

Use the SDK pinned in the repository's `global.json`, its iOS workload, and
compatible Xcode. The integration currently supports Debug ARM64 iOS
Simulator; device, Intel simulator, and Release builds fail explicitly.

`netlift.json` references the portable Orders library and managed/native
service roots. `project.yml` includes the shared `NetLiftWorkload.yml`
template and references the existing native acceptance app. Xcode compiles
those native sources; the generated managed project remains an internal
build artifact, not the native app's entry point.

The app checks real managed results, errors, adapters, cancellation,
Swift-owned callbacks, pull-driven streams, and UIKit peer ownership.
Its console reports `NETLIFT_APPLE_CALLBACK_PASSED`,
`NETLIFT_APPLE_STREAM_PASSED`, and `NETLIFT_APPLE_CANCELLATION_PASSED`.
It is an integration/acceptance sample, not a physical-device support claim.

For an existing app, copy the shared template's pre-build and embed phases,
generated source references, framework search path, and linker response-file
setting. Set `NETLIFT_REPOSITORY_ROOT` and use a workload `netlift.json`
(or set `NETLIFT_CONFIG` to its path). Keep your own native delegate and
lifecycle; adapt the sample's bootstrap and transport initialization once.
Subsequent builds of the native app pick up managed changes automatically.

The project is checked in; XcodeGen is needed only when changing its
definition:

```bash
xcodegen generate --spec apple/samples/OrdersHost/project.yml
```
