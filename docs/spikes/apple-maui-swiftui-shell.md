# MAUI-hosted SwiftUI shell spike

**Status:** source and offline build proof frozen; simulator runtime execution is blocked by an unresponsive local iOS simulator install path. This is exploratory only, not a NetLift product route.

## Question and result

This spike asks whether a .NET 11 MAUI iOS app can own the application lifecycle while presenting a real SwiftUI screen that calls a shared pure `net11.0` business service and receives its typed callback.

The bounded implementation compiles:

- A MAUI `net11.0-ios` app project with `UseMonoRuntime=false` and `PublishAot=false`.
- A separate pure `net11.0` business library with `SubmitService.Submit(bool, ISubmitConfirmation)`.
- A Swift static library containing a real SwiftUI `View` hosted in `UIHostingController`, and a C-compatible callback bridge.
- An unmanaged Cdecl function pointer from the Swift button action to managed code. The managed business service invokes typed `ISubmitConfirmation.Confirm(SubmitResult)` synchronously, which calls back into Swift to update the screen.

The app's MAUI `MainPage` is an empty presentation shell; it is not the sample UI. The actual action buttons, result labels, and close action are SwiftUI-owned. The host roots the managed callback for the process lifetime and the Swift bridge retains per-presentation state until the controller is dismissed.

**Build proof passed, simulator execution did not.** The Debug and Release iOS simulator app bundles compile offline. Their native executables link `SwiftUI.framework`, export the three `nl_maui_swiftui_*` bridge symbols, contain CoreCLR runtime libraries, and include a ReadyToRun framework. I could not install and launch the app to observe the SwiftUI screen or execute a callback: `simctl install` stalled for minutes on the dedicated simulator, and Mobile Canvas installation timed out. Therefore there is no runtime-mode, UI, callback-thread, callback-lifetime, or interactive behavior acceptance result from this spike.

## Files

- `spikes/apple-maui-swiftui-shell/NetLift.MauiSwiftUIShell.csproj` — iOS-only MAUI host; selects CoreCLR, disables NativeAOT, includes the shared service and optionally links the Swift static library.
- `spikes/apple-maui-swiftui-shell/MainPage.xaml` and `MainPage.xaml.cs` — blank MAUI presentation shell and one-time SwiftUI presentation request.
- `spikes/apple-maui-swiftui-shell/NativeSwiftUIHost.cs` — unmanaged function-pointer callback bridge and runtime probe.
- `spikes/apple-maui-swiftui-shell/Business/` — separate, plain `net11.0` business service and typed callback contract.
- `spikes/apple-maui-swiftui-shell/native/SwiftUIShell.swift` — SwiftUI interface and native/managed bridge.
- `spikes/apple-maui-swiftui-shell/build-simulator.sh` — isolated Swift + offline MAUI simulator build.
- `spikes/apple-maui-swiftui-shell/run-simulator.sh` — install and launch helper for a dedicated simulator.

The template's Android, Mac Catalyst, and Windows platform files are present but those targets are excluded from this iOS-only experiment.

## Reproduction

The session-local artifact root used here was:

`/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-maui-swiftui-shell`

Before the first .NET invocation, CLI state, temporary files, and the NuGet global-packages directory were isolated below that root. `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` suppressed only ASP.NET development-certificate first-run creation; certificate revocation, signing, and OS trust checks were not disabled. Restore was restricted to the existing local package cache at `/Users/redth/.nuget/packages`; no package feed was added or contacted.

Installed tools observed:

- .NET SDK `11.0.100-rc.1.26425.128`
- MAUI workload `11.0.0-rc.1.26451.6`
- Xcode `26.6` at `/Applications/Xcode_26.6.app/Contents/Developer`
- iOS 26.5 simulator runtime

With those environment variables set, the local-package baseline build before adding the Swift bridge succeeded with 0 warnings and 0 errors:

```sh
dotnet build /Users/redth/code/netlift/spikes/apple-maui-swiftui-shell/NetLift.MauiSwiftUIShell.csproj \
  --framework net11.0-ios --configuration Debug --runtime iossimulator-arm64 \
  --artifacts-path /Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-maui-swiftui-shell/artifacts \
  -p:RestoreSources=/Users/redth/.nuget/packages
```

The current repeatable build command compiles the Swift archive and then the MAUI app:

```sh
APPLE_SPIKE_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-maui-swiftui-shell \
  sh /Users/redth/code/netlift/spikes/apple-maui-swiftui-shell/build-simulator.sh
```

Final Debug build: **PASS**, 0 warnings, 0 errors, 10.02 seconds (incremental). The earlier integrated Debug build passed with 0 warnings and 0 errors in 14.34 seconds.

Final Release build (including the finalized Swift bridge): **PASS**, 0 warnings, 0 errors, 35.85 seconds:

```sh
dotnet build /Users/redth/code/netlift/spikes/apple-maui-swiftui-shell/NetLift.MauiSwiftUIShell.csproj \
  --framework net11.0-ios --configuration Release --runtime iossimulator-arm64 \
  --artifacts-path /Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-maui-swiftui-shell/release \
  -p:RestoreSources=/Users/redth/.nuget/packages \
  -p:AppleNativeLibrary=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-maui-swiftui-shell/native/libNetLiftSwiftUIShell.a
```

The command writes its full log to `files/apple-maui-swiftui-shell/logs/release-build.log`.

The first build under the active Xcode 27.1 failed as expected because this installed .NET 11 iOS workload requires Xcode 26.6. The machine also has Xcode 26.6 installed, so the build was rerun with `DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer`. That Xcode selection is toolchain selection, not a trust or signing override.

## Simulator attempt

A dedicated iOS 26.5 iPhone 17 Pro simulator was created:

- Name: `NetLiftMauiSwiftUIShell26_5`
- UDID: `130876F9-DE1A-4828-9855-BE5032C62D7D`

`simctl` reported it booted, but `simctl bootstatus ... -b` remained blocked in iOS Data Migration for over 20 minutes. Afterward, `simctl install <UDID> <Debug app bundle>` produced no output and remained blocked for over 5 minutes. The matching Mobile Canvas install request timed out. Those commands were stopped rather than left running. The dedicated simulator was shut down after the attempt. No physical device was used. Because install never completed, no app launch, screenshot, accessibility tree, result-button test, app log, or crash report was obtained.

The install command to retry when the simulator service is responsive is:

```sh
APPLE_SPIKE_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-maui-swiftui-shell \
SIMULATOR_UDID=130876F9-DE1A-4828-9855-BE5032C62D7D \
  sh /Users/redth/code/netlift/spikes/apple-maui-swiftui-shell/run-simulator.sh
```

## Runtime, compilation, and limits

Microsoft's .NET 11 guidance states that CoreCLR is the default MAUI runtime, iOS uses composite partial ReadyToRun in Debug and composite full ReadyToRun in Release, and CoreCLR's interpreter executes code that is not precompiled because iOS does not permit JIT compilation. The app project explicitly selects CoreCLR (`UseMonoRuntime=false`) and leaves `PublishAot=false`; it does not set the legacy Mono-only `UseInterpreter` option.

Observed in the built simulator artifacts:

- Debug executable links `libcoreclr.dylib`, `libxamarin-dotnet-coreclr-debug.dylib`, a `.r2r` framework, and `SwiftUI.framework`.
- Release output contains `libcoreclr.dylib`, `libxamarin-dotnet-coreclr.dylib`, and `Frameworks/NetLift.MauiSwiftUIShell.r2r.framework/NetLift.MauiSwiftUIShell.r2r`.
- Both app executables export `_nl_maui_swiftui_present`, `_nl_maui_swiftui_complete`, and `_nl_maui_swiftui_close`.

These are build/artifact observations, not runtime interpreter telemetry. `NativeSwiftUIHost.Present()` writes a runtime probe (`Mono.Runtime` presence, framework description, and dynamic-code feature flags) into the device log and passes it to the SwiftUI screen, but it was not observed because the app could not be installed. The probe must not be treated as a standalone proof of the absence of JIT; corroborate it with the .NET 11 iOS runtime policy and runtime diagnostics before making that claim.

The project is an exploratory simulator configuration, not device packaging or signing evidence. No physical-device build or install, distribution signing, entitlement review, App Store policy assessment, NetLift CoreCLR host integration, negotiation protocol, or production callback ABI is included. The `SubmitService` is deliberately a local demo service, not the Orders or NetLift sample.

## First-party references

- [.NET MAUI: What's new in .NET 11 — CoreCLR and iOS](https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-11?view=net-maui-10.0#net-for-ios)
- [.NET MAUI: Runtimes and compilation — .NET 11 ReadyToRun and CoreCLR interpreter](https://learn.microsoft.com/dotnet/maui/deployment/runtimes-compilation?view=net-maui-10.0)
- [.NET for iOS: Build items — `NativeReference` and `XcodeProject`](https://learn.microsoft.com/dotnet/ios/building-apps/build-items)
- [.NET MAUI Community Toolkit: Native Library Interop](https://learn.microsoft.com/dotnet/communitytoolkit/maui/native-library-interop/)
