# Incremental build and native debug loop

**Status:** the bounded graph proof, SwiftPM-generated Swift typecheck, and
declared Xcode phase smoke test passed on 2026-09-25. The AGP 8.11.1 variant
source proof compiled generated Kotlin for Debug and Release. An isolated
native macOS host applied a Roslyn method-body metadata delta to a Debug
CoreCLR assembly and preserved managed state. SwiftPM's BuildToolPlugin does
not compile its generated C/header outputs in a C target; an artifact-only
SwiftPM package proves C/Swift compilation, and a separate Xcode-owned macOS
app target now compiles and runs the generated Swift and C/header from
`DerivedSources`. On 2026-09-27 a combined isolated matrix also proved actual
`NetLift.Projection` output through AGP Debug/Release and Xcode-derived
sources across clean, managed-implementation-only, native UI-only,
contract-only, and missing-output-repair edits. IDE indexing/debugger
attachment and automatic watch/IDE delta production remain unproven. No
existing platform app, generator, manager, or `verify.sh` files are changed
by these spikes. A fresh rerun on 2026-09-27 also corrected an Xcode
projection input digest that had embedded absolute source paths and proved
the replacement digest identical across two copied Xcode fixture roots.
The isolated AGP Debug/Release and Xcode matrix also passed a deterministic
invalid-contract failure, verified downstream compile/package/link blocking,
restored the contract with matching generated hashes, and confirmed the next
build skipped unchanged projections.

## Environment and exact commands

The machine differs from the requested tool versions:

| Tool | Observed |
| --- | --- |
| .NET SDK / `dotnet watch` | `11.0.100-rc.1.26425.128` (`3551975be08744f0418857c5bed8ab1545c5dd47`) |
| .NET runtime | `Microsoft.NETCore.App 11.0.0-rc.1.26425.128` |
| macOS | 26.6, arm64 |
| Xcode | `Xcode 27.1`, build `27A9269` (not the requested 26.6) |
| Swift | `Apple Swift version 6.4.0.34.1`, target `arm64-apple-macosx26.0` |
| Java | OpenJDK `21.0.12` |
| Android build tools | Android SDK platform 36; Gradle 8.13 via the existing sample wrapper; no global `gradle` or `kotlinc` |

Versions were captured with `dotnet --info`, `xcodebuild -version`,
`swift --version`, and `java -version`; global `gradle --version` and
`kotlinc -version` were also attempted and were unavailable. The Android
spike used the checked-in Gradle 8.13 wrapper from the existing sample, with
all Gradle caches and generated build outputs redirected under the spike's
`.artifacts/` directory.

The existing Gradle sample declares AGP `8.11.1`, Kotlin `2.2.20`, and Compose
compiler plugin `2.2.20` in
`spikes/android/NetLift.GradleApp/build.gradle.kts`. Those versions informed
the isolated AGP proof below; the existing sample itself was not modified or
built. The incremental proof ran with:

```sh
bash spikes/build-dev-loop/run-incremental-check.sh
```

It compiles a throwaway copy of a plain `net11.0` library, changes only that
copy, and deletes its temporary build outputs on exit. The observed log:

```text
MANAGED-COMPILE: baseline
GENERATOR-RUN: contract sha256=4582e201d30e35ed44c6d43f3bb30dca2e1a321dbd158bcd5afa6c2da12a0387
GENERATOR-SKIP: contract sha256=4582e201d30e35ed44c6d43f3bb30dca2e1a321dbd158bcd5afa6c2da12a0387
== Generator output repair regression ==
GENERATOR-RUN: contract sha256=4582e201d30e35ed44c6d43f3bb30dca2e1a321dbd158bcd5afa6c2da12a0387
PASS generator repaired deleted/corrupt outputs with matching stamp
MANAGED-COMPILE: implementation
GENERATOR-SKIP: contract sha256=4582e201d30e35ed44c6d43f3bb30dca2e1a321dbd158bcd5afa6c2da12a0387
PASS implementation edit rebuilt managed assembly only
MANAGED-COMPILE: contract
GENERATOR-RUN: contract sha256=9a58e3441fa51dc7adb98911342b8ee7f46c916674fa20f030754cd105bd523a
PASS public contract edit rebuilt managed assembly and regenerated schema/Swift/Kotlin outputs
MANAGED-COMPILE-SKIP: native
GENERATOR-SKIP: contract sha256=9a58e3441fa51dc7adb98911342b8ee7f46c916674fa20f030754cd105bd523a
PASS native-only edit typechecked against generated API without managed or generation changes
PASS incremental dependency check
```

## SwiftPM plugin and Xcode phase proofs

The actual projection CLI was also invoked by an isolated SwiftPM
`BuildToolPlugin` fixture:

```sh
bash spikes/build-dev-loop/run-swiftpm-plugin.sh
```

The clean wrapper runs:

```sh
DOTNET_ROOT="$dotnet_root" swift build \
    --package-path "$package" \
    --scratch-path "$scratch" \
    --product ProjectionClient \
    -v
```

The package is under `spikes/build-dev-loop/SwiftPM/`. Its plugin declares
`ProjectionFixture.g.cs`, `NetLift.Projection.csproj`, `Program.cs`, and
`global.json` as build-command inputs and writes generated artifacts only to
the plugin work directory. The driver builds the real
`src/NetLift.Projection/NetLift.Projection.csproj` there, invokes it against
the declared fixture, and checks the fixture/header/manifest schema hash.
The clean run succeeded with schema hash
`743d759cbe27492fc58aa8628b083d3ea275bed9d22006abef4b51445ab9b376`.
`ProjectionClient/PreviewFixture.swift` uses the generated `Point`; a
successful `swift build --product ProjectionClient` therefore typechecks the
generated Swift surface as a SwiftPM target dependency. The observed derived
paths were:

```text
.artifacts/swiftpm-plugin/swiftpm/plugins/outputs/swiftpm/ProjectionClient/destination/NetLiftProjectionPlugin/projection-output
.artifacts/swiftpm-plugin/swiftpm/plugins/outputs/swiftpm/ProjectionCABI/destination/NetLiftProjectionPlugin/projection-output
```

These are inspectable build artifacts for this run, not stable paths for
consumers to hard-code. After an implementation-only edit to the copied
managed fixture, its DLL hash changed; the next `swift build` did not invoke
the projection driver, and generated Swift/header/adapter/manifest hashes
and modification times remained unchanged. The captured log is
`spikes/build-dev-loop/.artifacts/swiftpm-plugin/first-build.log`.

The plugin can reach and build the managed projection project from its
work directory; there was no sandbox or project-input blocker. SwiftPM did
not, however, integrate the generated C/header files into the C source
target. The build log emits `C header file generation not enabled` and
`C source file generation not enabled`; its compile steps show
`Sources/ProjectionCABI/target.c`, not the generated `abi.c`. Thus the
generated header's hash was checked, but this plugin path alone does not
prove SwiftPM C-target consumption. A successful plugin build is not evidence
that generated `abi.c` was compiled or linked. `NetLift.Projection` emits the
Swift surface, C header, and Kotlin surface; the spike's SwiftPM driver emits
the C adapter source against that generated header.

### SwiftPM C target source-consumption proof

The bounded follow-up is repeatable with:

```sh
bash spikes/build-dev-loop/run-swiftpm-source-target-check.sh
```

It keeps the BuildToolPlugin's declared inputs/outputs and measures the
generated-C limitation above, then exercises a reusable SwiftPM C target and
Swift executable target without writing generated files into authored
sources. The wrapper stages the plugin's actual `NetLift.swift`, `netlift.h`,
and driver-generated `abi.c` under
`.artifacts/swiftpm-source-target/contracts/<schema-hash>/`, and builds a
copy of the static `SwiftPM/SourceTarget/Package.swift` template under the
same artifact root. The C target compiles `abi.c` against the actual generated
header; the Swift executable compiles the actual projected Swift types and
imports the typed C `lift_config` surface. The C adapter initializes and
validates ABI version, struct size, and the emitted 32-byte schema hash. It
does not host CoreCLR or implement the LIFT transport.

The exercised acyclic graph is:

```text
contract selection + contract fixtures + projection project/source + global.json
    -> plugin build command (declared inputs/outputs in plugin work directory)
    -> per-schema-hash Swift/header/C-adapter/manifest output
    -> artifact-only SwiftPM Sources/ProjectionCABI + Sources/ProjectionClient
    -> C compile -> Swift compile/link -> executable semantic assertions
```

The clean run used the pinned .NET SDK `11.0.100-rc.1.26425.128`, Swift
`6.4.0.34.1`, and Xcode 27.1. With `baseline` selected, both plugin commands
ran; SwiftPM reported the generated-C/header warnings. The artifact-only
source-target build nevertheless logged `Compile abi.c` and
`Compile NetLift.swift`, then ran:

```text
SWIFTPM_TYPED_CONSUMER_PASS point=7 abi=1 schema=743d759cbe27492fc58aa8628b083d3ea275bed9d22006abef4b51445ab9b376
```

An implementation-only edit in a copied `net11.0` business-library fixture
changed its DLL hash from
`424bb407d83418b060a277424666084525097ad038f6bfae0b1c3b88474a0d7d` to
`c69bb93f652b1e3aa5177c724b01d8878305fa61d1d3de0a92afb98b6cac1117`.
SwiftPM emitted no `PROJECTION-COMMAND-RUN`, preserved the staged projection
outputs, and left the consumer binary hash unchanged at
`9a7f644121bf31790d92e47b6d9f0a965c83bc7fed1f5579d04c0d974c2c5f0b`. The
contract-only switch added a `Vector` DTO, reran the same two plugin commands,
and produced a different schema hash,
`09245cb24d5b16974fb11d690a3de388373395b78446a847c50fbebd75d423c3`.
The C and Swift targets recompiled from that per-contract output and the
consumer ran:

```text
SWIFTPM_TYPED_CONSUMER_PASS point=7 vector=12 abi=1 schema=09245cb24d5b16974fb11d690a3de388373395b78446a847c50fbebd75d423c3
```

Content-only output-set hashes (ordered Swift, C header, C adapter, Swift
manifest, C manifest) were:

| Contract | Schema SHA-256 | Output-set SHA-256 | Result |
| --- | --- | --- | --- |
| Baseline | `743d759cbe27492fc58aa8628b083d3ea275bed9d22006abef4b51445ab9b376` | `b7ace30ac52886a11f67f5930c6f2c25e1e4f20d7c3bb87e0f4f9ed48a216767` | Clean compile and typed consumer run passed |
| Added `Vector` DTO | `09245cb24d5b16974fb11d690a3de388373395b78446a847c50fbebd75d423c3` | `f7af931fefeef38e3cf1cfc04626b0fb69d81d557c8da17420bea18f00bce9a5` | Contract invalidation, C/Swift recompile, and typed consumer run passed |

The selector is restored to `baseline` on exit. Logs and both per-contract
source sets remain under
`spikes/build-dev-loop/.artifacts/swiftpm-source-target/`. This proves
compiler and executable consumption through a wrapper-staged SwiftPM package,
not that SwiftPM's plugin attaches generated C files to its own C target.
It also does not establish Xcode project/app-target membership, IDE navigator
recognition, SwiftUI previews, or app hosting. The separate Xcode aggregate
phase remains a smoke test only. `tool-versions.log`,
`plugin-clean.log`, `plugin-implementation.log`, `plugin-contract.log`, the
three `source-*-build.log` files, and the consumer run logs retain the
toolchain output and each validation result.

To test a declared Xcode build-phase path for those outputs without changing
an Apple host, the spike includes a standalone aggregate target:

```sh
bash spikes/build-dev-loop/run-xcode-phase-check.sh
```

The command builds `ProjectionPhase.xcodeproj` with Xcode 27.1, `-scheme
ProjectionPhase`, and a spike-owned DerivedData directory. Its shell phase
declares the projection project/source, pinned `global.json`, fixture, and
typecheck consumers as inputs; generated Swift, header, and manifest are
declared outputs under `DERIVED_FILE_DIR`. It builds and invokes the same
projection CLI, then `swiftc -typecheck`s the generated Swift with a fixture
and runs `clang -fsyntax-only` on a C consumer that includes the generated
`netlift.h`. This passed with the same schema hash. After changing only the
copied managed implementation, the managed DLL changed while Xcode skipped
the phase and preserved all generated output bytes and mtimes.

The Xcode target is an aggregate smoke target: it proves declared phase
ordering/invalidation and compiler typechecks, not compilation of generated
sources as members of an existing app target. The phase disables Xcode's
user-script sandbox because MSBuild also writes intermediate and restore
artifacts below its DerivedData work directory; its final generated files
remain declared outputs. No Xcode IDE/navigator inspection was performed,
so no navigator visibility claim is made. The exact local logs are
`spikes/build-dev-loop/.artifacts/xcode-phase/first-build.log` and
`spikes/build-dev-loop/.artifacts/xcode-phase/implementation-build.log`.
The wrapper invokes:

```sh
xcodebuild \
    -project "$project" \
    -scheme ProjectionPhase \
    -configuration Debug \
    -derivedDataPath "$derived_data" \
    CODE_SIGNING_ALLOWED=NO
```

### Xcode-owned macOS app target with generated source members

The follow-up app-target fixture is under `spikes/build-dev-loop/XcodeNative/`
and can be rerun with:

```sh
bash spikes/build-dev-loop/run-xcode-native-target-check.sh
```

The wrapper copies the Xcode project into
`spikes/build-dev-loop/.artifacts/xcode-native-target/Project/` so all
fixture edits are isolated there, then invokes:

```sh
xcodebuild \
    -project "$build_project/ProjectionHost.xcodeproj" \
    -scheme ProjectionHost \
    -configuration Debug \
    -derivedDataPath "$derived_data" \
    NETLIFT_REPOSITORY_ROOT="$repo_root" \
    CODE_SIGNING_ALLOWED=NO
```

The `ProjectionHost` macOS application target declares `NetLift.swift`,
`NativeMain.swift`, and `abi.c` as `PBXSourcesBuildPhase` members whose
file-reference paths resolve below
`$(DERIVED_FILE_DIR)/NetLiftProjection/`. The same phase declares
`netlift.h` as a generated output; the generated C source and Swift bridging
header both include it. Xcode's build log names the actual DerivedSources
paths for `SwiftCompile` and `CompileC`, and the linked executable runs from
`ProjectionHost.app/Contents/MacOS/ProjectionHost`. This fixture implements
the generated `Fixture.count()` contract method in a small native conformer,
calls it at runtime, and also constructs and reads generated `Point` and
`Vector` DTO values. The C side initializes a typed `lift_config`, validates
its size/version/schema hash, and supplies the schema string asserted by the
Swift consumer.

The acyclic target graph is:

```text
selected contract + fixture + projection source/project + global.json
        + generator/template inputs
    -> declared Xcode generation outputs in DERIVED_FILE_DIR/DerivedSources
    -> Xcode Sources phase compiles generated Swift/C and authored native Swift
    -> Xcode links ProjectionHost.app
    -> executable checks generated method, DTO values, ABI, and schema hash
```

The script phase runs a cheap content-integrity gate on each Xcode build.
Its input fingerprint and the per-output SHA-256 manifest control the costly
generation path: unchanged valid outputs print `XCODE-PROJECTION-SKIP` and
retain their bytes and mtimes; a selected-contract/input change runs the
actual pinned projection CLI. A same-input hash mismatch fails closed rather
than compiling tampered generated files. Missing generated files trigger
regeneration. This is deliberate because the integrity gate must detect
content corruption even when the build system's timestamps and input graph
would otherwise look current.

The original local run used .NET SDK `11.0.100-rc.1.26425.128`, Apple Swift
`6.4.0.34.1`, Xcode `27.1` build `27A9269`, macOS SDK `27.0`, and an arm64
host; it built the macOS app for arm64 and x86_64. Its baseline and
contract-change results were:

| Case | Schema SHA-256 | Generated output-set SHA-256 | App executable SHA-256 | Runtime assertion |
| --- | --- | --- | --- | --- |
| Clean baseline | `743d759cbe27492fc58aa8628b083d3ea275bed9d22006abef4b51445ab9b376` | `10532fb13ede70ed59f18c2e90f126cfdbeedbaec0d808dab0834f7c391f4751` | `bf38cd8e13b4bcb92b4a225c60781a9abfdfab6f5db7ecaaf8a2ffdcade40047` | `point=7 method=7 abi=1` |
| Contract adds `Vector` | `09245cb24d5b16974fb11d690a3de388373395b78446a847c50fbebd75d423c3` | `9f9b9b4876f593bdc39cac8d9a0040e2365dc7af7ab463f52280d3cbdd093241` | `2cbd52b62e8f40244cf391dcb5fbe9c36acb16bbebe1bb3a41c5dfe6c70cc79e` | `point=7 vector=12 method=19 abi=1` |
| Native-only marker edit | unchanged contract hash | unchanged contract output set | `a3406c197f312a25960ee08063412508a7928f74f04e1243f70fc4dc80a216a2` | contract assertion passed; `native=native-v2` |

The copied managed implementation edit changed the library DLL from
`95778f68e7c31ea7a1d78b8a3bf88a387a72ac05ce92aae2cd2475399ba0b2c4` to
`cb17e9c9a01fce99d736709c687e2d99b05b6f2d6a7c29001280e5264541e235`.
The Xcode gate skipped projection, generated member compile tasks were
absent, and the app executable hash remained equal to the baseline. The
contract selection change ran projection, recompiled/linked Swift and C,
and executed the added DTO assertion. Changing only `NativeOnly.swift`
skipped projection and generated member compiles while rebuilding the app.
The negative integrity test appended bytes to generated Swift; the build
failed with `Generated source integrity mismatch for NetLift.swift`. Deleting
that source then regenerated and recompiled it, restoring the original
baseline output hash.

The script's aggregate output-set hash covers generated Swift, header, C
adapter, selected Swift consumer, and manifest in a fixed order. These
27.1-specific hashes are historical: the manager's later pinned 26.6 rerun
refreshed the shared `.artifacts/xcode-native-target/` logs and
`tool-versions.log`, so that directory no longer retains the original
27.1-specific logs. The fixture proves Xcode target source membership,
compilation/linking, and local macOS execution, not indexing or navigator
visibility in the IDE. The `.app` has no UI and was run by invoking its
executable directly; SwiftUI previews, existing app integration, native
runtime hosting, and distribution are not proved. The standard runner
deletes and recreates its artifact directory, so do not use it for the
separate IDE investigation while preserving the manager's current artifacts.

The manager also independently ran the same wrapper with
`DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer`. Xcode
26.6 (`17F113`), Swift 6.3.3 and macOS SDK 26.5 passed the clean Point
build/run, corruption rejection, missing-source repair, implementation-only
projection and generated-compile skip, contract-changed Point+Vector
recompile/run, and native-only rebuild/skip. Baseline and changed schema
hashes remained `743d759c...` and `09245cb2...`; the generated output-set
hashes were `f13f527519fc87e5165a0f56ae0cf4bfb1c47d7b7e6d959ba03295b88f2ff09a`
and `831b51bfc50f6d7c1045b07404a14ad954029862c7080f383a4c7d285be9ee7b`.
The three executable hashes were `baef0e852bd9afac26e56fd696585a192141632b366b46412b6368d2cf6a2596`,
`4605ce5aff4f332f4e8276a2dcc49dbe3de066c137ec7085cbfe4224778ffed1`,
and `4c20450d218611913a003eb022daf5849602b64ecab047837ba3fc5573536f42`.
The pinned rerun log is
`files/xcode-native-target-pinned-manager.log` in the manager session;
the fixture's `.artifacts/xcode-native-target/` holds its detailed logs.
This is still a headless macOS fixture, not iOS app membership or IDE
indexing.

### Xcode IDE navigator/indexing feasibility (not yet observed)

Source-only inspection establishes that the existing fixture is a useful
IDE test subject but does **not** establish IDE visibility. `NetLift.swift`,
`abi.c`, and `NativeMain.swift` each have a `PBXFileReference` and
`PBXBuildFile` entry in the target's Sources phase. However, none of those
generated-file references is a child of a `PBXGroup`; the project root group
contains only `NativeOnly.swift`, `ProjectionBridge.h`, and Products. Thus
the headless build proves target membership and compiler input paths, but
there is no explicit generated-sources group in the project navigator model.
The file references also resolve through `$(DERIVED_FILE_DIR)` into Xcode's
`Intermediates.noindex/.../DerivedSources`, so the IDE's handling of their
location and indexability must be observed rather than inferred. No Xcode
IDE window has been opened for this fixture and no IDE/indexing claim is made.
Apple's [Project Navigator file and group guidance](https://developer.apple.com/documentation/xcode/managing-files-and-folders-in-your-xcode-project)
and [target build-phase guidance](https://developer.apple.com/documentation/xcode/customizing-the-build-phases-of-a-target)
describe separate project-organization and Compile Sources surfaces; that is
why the acceptance check records navigator visibility independently from
successful compilation. Apple's [Run Script phase guidance](https://developer.apple.com/documentation/xcode/running-custom-scripts-during-a-build)
also states that scripts receive the target's build settings and environment,
which supports supplying the repository root through the isolated copied
target settings rather than a CLI-only override. The actual Xcode build log
will still be checked before treating that setup as validated.

The existing `run-xcode-native-target-check.sh` is not safe to reuse for the
IDE experiment: it removes and recreates
`.artifacts/xcode-native-target/`. The IDE attempt must use a separate copied project and generated-source root
under `.artifacts/xcode-ide-indexing/`, leaving the independently verified
native-target artifacts untouched. `OBJROOT` and `SYMROOT` isolate
intermediates/products, but do **not** by themselves establish where Xcode
stores IDE index data. Apple's [Build Settings Reference](https://developer.apple.com/documentation/xcode/build-settings-reference)
documents index-store options such as `INDEX_STORE_COMPRESS` and
`INDEX_STORE_ONLY_PROJECT_FILES`, but does not document
`INDEX_DATA_STORE_DIR`; the copied project does not set that undocumented
knob. The prior headless run's compiler command in `xcode-clean.log` and its
XCBuildData `build-request.json` recorded
`.../DerivedData/Index.noindex/DataStore` when invoked with
`-derivedDataPath`; that evidence does not establish the IDE's index-store
path. The GUI index path will be recorded in
`.artifacts/xcode-ide-indexing/ide-observations.log` from IDE build/index
activity and reported separately. No global Xcode Locations setting will be
changed.
The UI/indexing test is waiting for exclusive shared build-lane coordination;
its build phase invokes both Xcode and the pinned .NET projection CLI.

The isolated runner is
`spikes/build-dev-loop/run-xcode-ide-indexing-check.sh`. It has only had a
shell-syntax check and its `--prepare-only` mode. That mode staged the
isolated project at
`spikes/build-dev-loop/.artifacts/xcode-ide-indexing/Project/ProjectionHost.xcodeproj`
and verified the copied settings. No Xcode/.NET command ran, no DerivedData
was created, and the original native-target artifact root was not touched.
The source-only preparation command was:

```sh
bash spikes/build-dev-loop/run-xcode-ide-indexing-check.sh --prepare-only
```

The manager will run the default headless baseline after the upstream lane is
released, then hand off the stable copied project and artifacts for the IDE
observation. Do not run the default mode independently; it intentionally
refuses to overwrite existing DerivedData. After handoff, inspect that copied
project in Xcode without starting a competing rebuild. This handoff covers
baseline navigator/index observation only; a contract-changed Xcode build
requires a separate coordinated build slot.

The runner's default mode remains available for reproducing the headless
baseline when explicitly coordinated. It refuses to overwrite existing build
artifacts, copies the fixture only if it has not already been prepared, and
changes only the copied project's Debug/Release settings so `OBJROOT`,
`SYMROOT`, and the repository root resolve within the experiment. The
headless build uses `-derivedDataPath` under the experiment root and checks
the build log's actual `OBJROOT`/`SYMROOT`, compiler `-index-store-path`, and
generated output paths. Those checks do not pin or prove the IDE's own
index-store path. Record that actual path from Xcode's build/index activity;
do not change the global Xcode Locations setting. The runner does not launch
Xcode or modify the shared `xcode-native-target` artifacts.

Measurable acceptance criteria for that isolated experiment:

1. Record the exact Xcode version, copied project path, scheme, target,
   configuration, DerivedData path, and the successful build log. Confirm
   generated files exist only below that DerivedData's
   `Intermediates.noindex/.../DerivedSources/NetLiftProjection/`.
2. In the **Project Navigator**, separately from the target's Build Phases
   Compile Sources list, observe visible entries for `NetLift.swift` and
   `abi.c`; observe `netlift.h` too if Xcode presents the generated header
   there. Capture UI evidence showing the project navigator, not just a
   build log or source-phase membership.
3. Open the navigator's generated `NetLift.swift` entry and record its
   resolved editor path. Verify its bytes match `swiftSha256` in the generated
   manifest. A red/missing reference or an editor showing another path fails
   this criterion.
4. In baseline `NativeMain.swift`, use Xcode's indexed symbol navigation
   (Jump to Definition) from a generated DTO use to the generated DTO
   declaration. Record that Xcode opens the same DerivedSources file. The
   contract-changed `Vector` index check requires a separate coordinated
   Xcode build slot; until that slot is granted and run, report that check as
   pending rather than rebuilding during the baseline handoff.
5. Record the actual IDE index-store path shown by Xcode build/index activity,
   separately from the headless `-index-store-path` in `xcode-baseline.log`.
   Do not infer it from `OBJROOT` or `SYMROOT`, and do not change global
   Xcode Locations settings. Record it in
   `.artifacts/xcode-ide-indexing/ide-observations.log`; if it resolves
   outside the experiment root, report that fact rather than claiming IDE
   index isolation. Confirm the Project Navigator/editor and symbol
   navigation are available after a clean IDE open/reindex, and that neither
   generated source nor index output is written into `XcodeNative/` or other
   authored sources. Keep screenshots/accessibility observations, logs,
   paths, and hashes under the separate IDE artifact root where Xcode
   permits.

If generated target members compile but are absent from the Project Navigator
or symbol navigation cannot resolve to their DerivedSources paths, report
that as a failed IDE-visibility/indexing proof—not as a build integration
failure. Any follow-up project-group or path-model change should be tested
on the isolated copy and independently measured; no navigator fix is assumed
by these criteria.

## Isolated real-emitter native graph matrix

The combined reproducible proof is driven by:

```sh
bash spikes/build-dev-loop/run-native-graph-check.sh --prepare-only
bash spikes/build-dev-loop/run-native-graph-check.sh
```

The first command only stages copied fixtures and project files; it does not
run .NET, Gradle, Xcode, or Android build tools. The second command requires
the existing Android SDK (`ANDROID_HOME`), pinned .NET SDK, Swift/Xcode, and
JDK 21. It refuses to overwrite an existing artifact root. For an independent
manager rerun while preserving this run, choose another path under
`spikes/build-dev-loop/.artifacts/`, for example:

```sh
NETLIFT_NATIVE_GRAPH_ARTIFACTS="$PWD/spikes/build-dev-loop/.artifacts/native-graph-manager" \
  bash spikes/build-dev-loop/run-native-graph-check.sh
```

The original matrix output root is
`spikes/build-dev-loop/.artifacts/native-graph/`. The path-digest correction
rerun used the separate root
`spikes/build-dev-loop/.artifacts/native-graph-pathhash-20260927/`; neither
that rerun nor this correction overwrote the original owner or manager
evidence. Each root contains copied AGP/Xcode fixtures, redirected Gradle
app/buildSrc output, Gradle user and project caches, Android user state,
managed fixture, NuGet and .NET CLI state, and Xcode DerivedData/index store.
`NetLift.Projection.csproj` has no `ProjectReference` and the repository has
no `Directory.Build.*` override; both the AGP task and Xcode generator invoke
`dotnet build --artifacts-path` inside their artifact root. The projection
source and SDK pin are read-only inputs. No shared `src/**/bin`,
`src/**/obj`, device, simulator, signing, or `verify.sh` operation is used.
Each run retains its directory map in `isolation-manifest.txt`.

**Independent manager rerun after the path-digest fix:** the complete
matrix passed in a third isolated root,
`spikes/build-dev-loop/.artifacts/native-graph-pathhash-manager-20260927/`
(`files/native-graph-pathhash-final-manager.log`). The projection
source/project and SDK pin stayed byte-stable during the run. Debug and
Release baseline/contract output-set hashes match the frozen owner run;
so do all Xcode baseline/contract source and manifest hashes, including
the corrected `inputsSha256` values `b5fbef283a0ae2e78a333b871f8417d4356d612d78ebd56923d9b59f8b43d03c`
and `632e24c0b51d76ae5c0fdd10f4e20bc8b7969166a80bc473d1f631a4f2efacd3`.
The two roots' copied managed DLL hashes differ; no cross-root
deterministic DLL claim is made. The manager did not install or launch
an Android app, open Xcode's GUI, or run a mobile simulator/device.

The real .NET emitter accepts a selected generated C# contract fixture and an
output directory; it writes `NetLift.kt`, `NetLift.swift`, and `netlift.h`.
The copied AGP task invokes that emitter separately for Debug and Release,
then registers each task's Kotlin output directory with the corresponding
AGP variant source API. The copied Xcode macOS app target declares generated
Swift, C, and header paths under `DERIVED_FILE_DIR` and compiles the Swift/C
files as target Sources. The Xcode spike creates `abi.c` from the emitter's
real generated `netlift.h` to check the typed ABI and schema hash; `abi.c` is
a fixture adapter, **not** a C source produced by `NetLift.Projection`.

The tested dependency edges are:

```text
generated C# contract fixture + projection project/source + global.json
    ├─> AGP Debug task -> Debug Kotlin/header/manifest -> Debug Kotlin compile
    ├─> AGP Release task -> Release Kotlin/header/manifest -> Release Kotlin compile
    └─> Xcode Run Script -> DerivedSources Swift/header + fixture C adapter
                              -> Xcode Swift/C compile -> macOS host assertion

managed implementation edit ──────────> managed DLL only; projections skip
Activity/SwiftUI source edit ──────────> native source compile; projections skip
contract selection/content edit ──────> Debug + Release + Xcode projections rerun
deleted generated source ─────────────> owning projection task repairs its output
```

All Debug/Release invocations use one Gradle task graph with `--parallel` and
both compile tasks requested. The diagnostics show both variant generator
tasks scheduled and invalidated together on the contract edit. The logs do
not establish simultaneous wall-clock execution of the two tasks, so this is
reported as one parallel-capable variant task graph, not a measured
concurrency benchmark.

Observed matrix (all diagnostics are in the named logs below):

| Change | AGP Debug / Release | Xcode target | Result |
| --- | --- | --- | --- |
| Fresh empty output root | Both projection tasks ran; both Kotlin variants compiled. | Real projection ran; generated Swift/C compiled for arm64 and x86_64. | Pass, schema `743d759cbe27492fc58aa8628b083d3ea275bed9d22006abef4b51445ab9b376`; host printed `point=7 method=7 abi=1`. |
| Managed-library implementation only | Both projection and Kotlin compile tasks `UP-TO-DATE`. | `XCODE-PROJECTION-SKIP`; generated Swift/C compile tasks absent. | Pass; managed DLL changed from `7b65567c5445049eff96b904226df5f3df929e9b4e5aa986435970f11f3fda15` to `b04317852ea9f041191eb68f0ca67d8aff0ed540b1273f47885eb991f603db26`; generated file hashes/mtimes stayed stable. |
| Native UI-only edit | Android Activity title changed; both projections `UP-TO-DATE`, both Kotlin compile tasks ran. | Spike-only SwiftUI `PreviewSurface` source edit compiled `NativeOnly.swift`; projection skipped and generated source compile tasks were absent. | Pass for invalidation edges. The fixture does not present the SwiftUI view or prove preview rendering. |
| Contract adds `Vector` DTO | Debug and Release generators and Kotlin compiles both ran in the same `--parallel` invocation. | Real projection ran and generated Swift/C recompiled. | Pass, schema `09245cb24d5b16974fb11d690a3de388373395b78446a847c50fbebd75d423c3`; host printed `point=7 vector=12 method=19 abi=1`. |
| Delete generated Debug `NetLift.kt` and Xcode `NetLift.swift` | Debug projection reran and restored Kotlin; Release output set remained unchanged. Kotlin compile was correctly `UP-TO-DATE` because repaired source bytes matched prior compiled input. | Integrity gate reran real projection and compiled repaired Swift/C outputs. | Pass; regenerated outputs match contract-case hashes. |

Exact output hashes (each file hash is per-file SHA-256; manifest hashes
differ by variant because the manifest includes its variant name):

These are the original owner run's recorded hashes. The later fresh rerun in
"Cross-root Xcode input digest correction" supersedes the Xcode manifest and
Xcode output-set hashes only; generated projection/consumer hashes and AGP
hashes remain the same.

| Output | AGP baseline Debug | AGP baseline Release | AGP contract Debug | AGP contract Release |
| --- | --- | --- | --- | --- |
| `NetLift.kt` | `37d15c919bde4ea213f4613f0a30556c831f385a853a883646f8c743b3820756` | `37d15c919bde4ea213f4613f0a30556c831f385a853a883646f8c743b3820756` | `aa528552a13106f4bc0e5656f6caad45aede812a7461b2af66a39813f6f723ce` | `aa528552a13106f4bc0e5656f6caad45aede812a7461b2af66a39813f6f723ce` |
| `netlift.h` | `dcc9c9327b97eabbc67068182becc3da779ff66703940aa5386cd80ef4d12d91` | `dcc9c9327b97eabbc67068182becc3da779ff66703940aa5386cd80ef4d12d91` | `3fb13ff7341e0a5153c2b6f0bec81dd313d40ad61f4c7c3cd939285a0a30c168` | `3fb13ff7341e0a5153c2b6f0bec81dd313d40ad61f4c7c3cd939285a0a30c168` |
| `projection-manifest.json` | `cb5e456a15556270ca9975c2b7aadacd96be79eadda799c915990e23b7f1f17b` | `acf4e08e24404c674da5755f392df36065dcc7414ded4373db6b4e7ef6d20c83` | `0ba02d54774238a0cec744afda020740e8717a200739ea284de8f69ddb0ec61d` | `d7f37a083680c017e7c34a231f56989c155231b2fe5e64e95463339183565d48` |

| Xcode output | Baseline | Contract adds `Vector` |
| --- | --- | --- |
| `NetLift.swift` | `2f7daebe296fbbf97cef21f415e58137aa928e4f1dac4e65f09bd120689553f7` | `b5efd98bd3fd2daf58b79903d7c9bb0c07392f2157dec0a34d27565a2b6c0c27` |
| `netlift.h` | `dcc9c9327b97eabbc67068182becc3da779ff66703940aa5386cd80ef4d12d91` | `3fb13ff7341e0a5153c2b6f0bec81dd313d40ad61f4c7c3cd939285a0a30c168` |
| fixture `abi.c` | `fd252680ea79daf9ae3399bce31699833a503b4f5ae4f1f022356d6bfcc742ff` | `4dcccb62238c2caeb9e931a3b719b9dce25812b587e42449b2403945ed48039a` |
| generated consumer `NativeMain.swift` | `0033a65d738bdc7a3063733e2189c21be527cfc860ea120616d243203bb551a1` | `17f3812af3afd18e9e995459ddb25fb6a881a28b47ae9a65627a2596e70c3186` |
| `projection-manifest.txt` | `ed7156fbc89149ee979116640b63ff37a4302ea585b2b73197a4420e8db1cda5` | `3350b4e890a87632de7bea3932372875d5c259c1fe7e36a78aea0daed4a4f404` |

The combined output-set hashes are in `summary.txt`; separate per-file hash
lists are retained as `hashes-*-baseline.txt` and `hashes-*-contract.txt`.
The exact runner diagnostics are in `logs/gradle-{clean,implementation,ui-only,contract,missing-debug-repair}.log`,
`logs/xcode-{clean,implementation,ui-seed,ui-only,contract,missing-xcode-repair}.log`,
and the managed/runtime logs. Tool versions, project copies, fixture variants,
output directories, caches, and all logs are retained below the isolated
artifact root, which is frozen for independent rerun.

### Cross-root Xcode input digest correction

The original `XcodeNative/generate-projections.sh` hashed `shasum` output
containing absolute input paths, so byte-identical fixture copies at different
`SRCROOT`s received different `inputsSha256` values. It now calls
`XcodeNative/hash-projection-inputs.sh`, which hashes each input's contents
and then hashes the ordered list of content digests; filenames and absolute
paths are not part of the digest. The helper itself is also a declared digest
input, so changing the hashing implementation invalidates generated output.

The runner makes a second copy of the Xcode fixture under
`xcode-project-path-independence/` and asserts equal digests for both the
baseline and contract-changed selections. Both checks passed:

| Selection | `inputsSha256` in each distinct project root |
| --- | --- |
| Baseline | `b5fbef283a0ae2e78a333b871f8417d4356d612d78ebd56923d9b59f8b43d03c` |
| Contract adds `Vector` | `632e24c0b51d76ae5c0fdd10f4e20bc8b7969166a80bc473d1f631a4f2efacd3` |

The full matrix was rerun with:

```sh
NETLIFT_NATIVE_GRAPH_ARTIFACTS="$PWD/spikes/build-dev-loop/.artifacts/native-graph-pathhash-20260927" \
  bash spikes/build-dev-loop/run-native-graph-check.sh
```

The clean, implementation-only, UI-only, contract-only, and missing-output
repair checks all passed again. The corrected baseline and contract
`projection-manifest.txt` hashes are respectively
`95f5b977517161bcd9fe9d9c45c5b1a5518966d2f23335625da8c03353cc5d77` and
`9713b102e69c22cda54930bb6e90e89f95973d5acb65be821a2a6b894bbbe5d8`;
the matching corrected Xcode output-set hashes are
`5d92cefe6373c043cbddfedbd872a34c29662c9553f7311fbe3e9d8379a4ec5c` and
`9a8397df1c2039caaa664bf30a11411b4d39172882b1b8dc5a943c4aeaf1c8e2`.
The baseline/contract generated Swift, header, fixture C adapter, and
consumer hashes match the manager's pre-fix run; the Xcode manifest differs
there because that retained run contains the old path-sensitive input digest.
The AGP baseline and contract output-set hashes still match the manager run.
The manager's prior outputs are preserved; the new corrected evidence is in
the fresh root's `summary.txt`, `hashes-xcode-*.txt`, path-independence logs,
and build logs.

Managed DLL output is not asserted byte-identical across artifact roots: the
owner rerun's before/after hashes were
`cbbcb42584c5eed3cd95ab46e1b4510c6536cf8ce0af5f1e2c52b257a8fc422b` /
`f75958013a48ed73cf01d555a38c401d89d05ee970080874d6d8b28d0d1393e7`,
while the manager run recorded
`7dc1d70f6679f6a898e6d5ce263ebb2163e65d490fac6a6bc2d64165a46817b4` /
`d405107cb394777edd5094f5c2b7db167eb8ace02d99cd125b8e7d734ba8ae4f`.
Managed artifacts can depend on build-root/PDB/path data; this matrix only
asserts that the managed DLL changes within a run after the implementation
edit, not that DLL bytes are portable across roots.

### Failure-atomic generated-source handoff

The runner now exercises failure and recovery after the contract-changed
projection has already succeeded. In the copied AGP and Xcode fixtures only,
it replaces the `CanonicalJson` raw string with `not-json` while retaining the
fixture's schema-hash constant. The actual emitter fails with
`'not-json' is an invalid JSON literal. Expected the literal 'null'.`; no
compiler or product files are modified.

The fresh full-matrix command was:

```sh
NETLIFT_NATIVE_GRAPH_ARTIFACTS="$PWD/spikes/build-dev-loop/.artifacts/native-graph-failure-final-20260927" \
  bash spikes/build-dev-loop/run-native-graph-check.sh
```

| Edge | Observed failure behavior | Recovery and incremental behavior |
| --- | --- | --- |
| AGP Debug + Release | The runner invoked `--continue :app:assembleDebug :app:assembleRelease`; both `generate*NetLiftProjection` tasks failed on the injected JSON marker. No Kotlin compile, package, or assemble action ran (the tasks were omitted or explicitly skipped), and both generated variant output directories were empty. | Restoring the valid contract reran both generators; each output-set hash exactly matched its pre-failure contract hash. A following Gradle invocation reported both projection and Kotlin compile tasks `UP-TO-DATE`. |
| Xcode macOS target | The Generate projections phase failed on the same JSON marker; Xcode reported `BUILD FAILED`. The generated Swift/C compile steps and `Ld ... ProjectionHost` were absent, and both `DerivedSources/NetLiftProjection` and its staging directory were empty. | Restoring the valid contract reran projection and Swift/C compilation; the host again printed `point=7 vector=12 method=19 abi=1`. The following build logged `XCODE-PROJECTION-SKIP`, with no generated-source compiles and unchanged output hashes/mtimes. |

The repair preserved the successful contract output-set hashes: AGP Debug
`cc508acd558f58fef797044f90f835cf103d7755b63c8285ec36783e46c93994`, AGP
Release `00c98df8717b7235d1aea34439773ff7ab1b43257ce0acaae51af9391be14995`,
and Xcode `9a8397df1c2039caaa664bf30a11411b4d39172882b1b8dc5a943c4aeaf1c8e2`.
The failure, repair, and incremental logs are listed in
`spikes/build-dev-loop/.artifacts/native-graph-failure-final-20260927/failure-atomicity-summary.txt`;
the full task/build diagnostics and exact per-file hashes are retained in the
same root.

This is a **fail-closed build handoff**, not a last-known-good rollback:
the fixture tasks withdraw their old generated files before generation, so a
failed contract leaves the registered output directories empty. The native
compile/package/link consumers are blocked in that build, and a later build
repairs from the restored inputs. Concurrent IDE readers and preservation of
old outputs during a failed generation are not tested.

This is a build/compile/runtime fixture, not full product integration:
Gradle compiles Kotlin generated into its build tree but does not package,
install, or launch an Android app. Xcode directly runs a spike-owned macOS
executable and does not open the IDE; the SwiftUI view is compiled but not
presented. No IDE navigator/indexing, Compose/SwiftUI Preview, device,
simulator, signing, publish, remote build-cache, or actual CoreCLR host is
proved by this matrix. The projection's lack of generated C source is handled
by the explicit Xcode-only ABI adapter described above; no product/compiler
code or default/golden behavior was changed.

## AGP variant-source integration proof

The Android fixture is under `spikes/build-dev-loop/AndroidGradle/`; it does
not change the existing sample under `spikes/android/`. The bounded
end-to-end check is:

```sh
bash spikes/build-dev-loop/run-agp-variant-check.sh
```

It uses the existing `spikes/android/NetLift.GradleApp/gradlew` wrapper
(Gradle 8.13), AGP 8.11.1, Kotlin Android plugin 2.2.20, JDK 21, and Android
SDK platform 36. The wrapper runs these native compile tasks with
`--no-daemon --console=plain`:

```sh
:app:compileDebugKotlin :app:compileReleaseKotlin
```

`GenerateNetLiftProjectionTask` declares the copied contract fixture,
`src/NetLift.Projection/NetLift.Projection.csproj`, `Program.cs`, and the
repository `global.json` as inputs. For each variant it builds the real
`NetLift.Projection` CLI under a variant-specific Gradle output directory,
invokes that CLI on the fixture, verifies the generated C header's schema
hash against the fixture, and writes a Kotlin-source hash manifest. The task
registers its output directory through
`variant.sources.java.addGeneratedSourceDirectory(...)`; the native sample
`MainActivity` references the emitted `Point` DTO, so both Kotlin compile
tasks fail if the registered generated source is not on the variant compile
path. Observed derived directories were:

```text
spikes/build-dev-loop/.artifacts/android-gradle/build/app/generated/java/generateDebugNetLiftProjection
spikes/build-dev-loop/.artifacts/android-gradle/build/app/generated/java/generateReleaseNetLiftProjection
```

The clean Debug/Release compile generated schema hash
`743d759cbe27492fc58aa8628b083d3ea275bed9d22006abef4b51445ab9b376` and
Kotlin SHA-256
`37d15c919bde4ea213f4613f0a30556c831f385a853a883646f8c743b3820756`.
The check then changed only the implementation in a copied plain managed
fixture and confirmed its DLL changed while both projection tasks were
`UP-TO-DATE` and their Kotlin/header hashes remained unchanged. Changing the
fixture service method from `count` to `total` with a recomputed valid
schema hash reran both variant generators and compilers; the new schema hash
was `c9c3fe21234a7647fce0aead017e19c8e35009fec844b1c88d7a77030eb69344`,
and generated Kotlin SHA-256 was
`a25c193abc1bfb1c29545e56d18505147ba8989297b5186a397e52ec25b2331a`.
Deleting Debug's generated `NetLift.kt` reran its generator and restored the
same bytes; Gradle correctly left Kotlin compilation `UP-TO-DATE` because
the restored source content matched the prior compile input.

The wrapper's Gradle distribution, plugin/dependency caches, Android user
state, .NET CLI/NuGet caches, logs, and build outputs all stay under
`spikes/build-dev-loop/.artifacts/android-gradle/`. The retained phase logs
are `logs/clean-baseline.log`, `logs/implementation-only.log`,
`logs/contract-edit.log`, `logs/missing-output-repair.log`,
`logs/managed-baseline.log`, and `logs/managed-implementation.log` in that
directory.

This proves the AGP variant task dependency and Kotlin compiler consumption
for the generated fixture source. It does **not** prove Android Studio
project/navigator recognition because no IDE inspection was performed, and
it does **not** prove CoreCLR loading, managed invocation, a full app package,
installation, or runtime behavior. Only Kotlin compilation ran; no device,
emulator, signing, or publishing action occurred. The fixture and emitter
are a bounded integration proof, not production contract normalization or
complete NetLift product tooling.

The separate shell fixture generator,
`spikes/build-dev-loop/generate-contract.sh`, hashes contract source bytes
and writes `schema.json`, `NetLift.swift`, `NetLift.kt`, plus a contract-hash
stamp. That generator is intentionally marker-only and is used only for the
incremental dependency-routing proof; it is not the `NetLift.Projection`
emitter used by the SwiftPM, Xcode, and AGP experiments. Those integrations
invoke the actual emitter, but their small fixture still does not establish
production contract normalization or a complete ABI parser. The product
schema hash must be over a normalized public-contract representation (stable
exported IDs, types, nullability, and ABI version), not raw C# formatting or
comments as this fixture does.

The generator stages expected output bytes before deciding whether it can
skip. A skip requires a matching stamp and byte-for-byte matches for all
three outputs. It replaces the schema and projections first and publishes
the stamp last. The bounded recovery regression removes `NetLift.swift` and
corrupts `NetLift.kt` after a successful run while leaving the stamp intact;
the next invocation regenerates and byte-compares all outputs to the
pre-corruption copies. This does not make the set of file renames a
multi-file atomic transaction; it ensures a partial publish cannot be
mistaken for a valid completed generation on the next run.

## Proposed acyclic graph

Keep managed compilation independent from native projection generation:

```text
Managed source + project + SDK ───────────────> plain net11.0 DLL + PDB
          │
          └─ exported contract + generator + ABI version
                    └─> canonical schema + schema hash
                           ├─> Swift projection ─> Xcode/Swift compilation
                           └─> Kotlin projection -> Gradle variant compilation

DLL + PDB + runtime-pack/RID/architecture/min-OS inputs
    └─> validated, checksummed runtime staging ─> native app bundle/APK

Swift-only change ────────────────────────────> Xcode target only
Kotlin-only change ───────────────────────────> matching Gradle variant only
```

Required incremental invariants:

* A C# implementation-body edit rebuilds the library and any managed tests,
  but leaves schema/projection hashes unchanged and does not compile Swift or
  Kotlin.
* An exported contract edit rebuilds the library, schema, both native
  projections, and the affected native app targets, in that order.
* A Swift- or Kotlin-only edit runs only that native compilation path.
* Include tool/generator version, bridge ABI, target identity, configuration,
  runtime-pack version, and minimum OS/API in the appropriate task inputs.
  Include the schema hash and checksums of staged outputs in a manifest.
* Compare content hashes, not timestamps alone. Do not rewrite generated
  files or their manifest when their bytes are unchanged. Fail the native
  build early if a required output is missing or the manifest does not match.
* Keep outputs variant- and target-specific, for example
  `obj/netlift/<target>/<configuration>/` for Xcode and
  `build/generated/netlift/<variant>/` for Gradle. Never place generated
  files in checked-in app source directories.

For Swift, attach generation to a Swift package target consumed by the native
Xcode project, using a SwiftPM `BuildToolPlugin` command with explicit
`inputFiles` and `outputFiles`. The plugin writes into its supplied plugin
work directory; those derived sources are part of the build graph without
checking generated code into the package. Do not depend on a particular
`.build`/DerivedData internal folder layout for source discovery. For an
Xcode-project build phase instead, declare input and output file lists under
DerivedData so Xcode can schedule and skip it.

For Android, register a task for each enabled AGP variant through the Variant
API. Wire its `TaskProvider` output directory as a generated source with
`addGeneratedSourceDirectory` (or the corresponding Kotlin plugin source
registration for Kotlin outputs), and keep generated files under that
variant's `build/` tree. This makes the task dependency and derived files
visible to Gradle/IDE tooling instead of copying to `src/`. Verify the Kotlin
registration against the Gradle/AGP/Kotlin plugin versions selected by the
consumer; it was not executable here.

These task branches must not cycle: the managed library build must not consume
Swift/Kotlin output, and the projection task must not depend on the managed
assembly when its true input is the exported contract schema. Runtime staging
does depend on the built managed assembly and the exact compatible
runtime-pack inputs. The schema exporter may run after a managed compile (or
track an explicit contract-only input set); it must hash the normalized
exported contract and preserve the schema file timestamp when that semantic
hash is unchanged, so an implementation edit cannot fan out to native
compilation.

## Native previews

Xcode's SwiftUI preview macros keep a view preview current while source is
edited. A preview should construct the view with a generated service protocol
or interface backed by a deterministic fake, not call the concrete C/CoreCLR
adapter from a view initializer, `body`, or preview entry point. The generated
surface can still be typechecked; the fake supplies preview data and records
actions.

Compose Preview executes in Android Studio's Layoutlib environment, not a
normal app/device process. Android documents no network access, no file
access, and incomplete `Context` APIs; it also recommends passing screen
state/data into previewable composables instead of asking the preview runner
to construct a `ViewModel` and its dependency graph. Use the same generated
API abstraction with a fake and pass state into the previewable composable.

Neither preview should claim to exercise managed business logic or validate
CoreCLR startup/JNI/C ABI. Those require a real app-owned Debug run. Rendering
with a fake remains possible even when the native target has generated API
sources but no runtime staged or no live managed process.

## .NET Hot Reload boundary

The command attempted against the existing native-host library was:

```sh
dotnet watch run --project spikes/desktop-host/NetLift.DesktopBridge/NetLift.DesktopBridge.csproj --non-interactive
```

`dotnet watch` built the library successfully, then reported:

```text
Unable to proceed with project '.../NetLift.DesktopBridge.csproj'.
Ensure you have a runnable project type.
A runnable project should target a runnable TFM (for instance, net11.0) and have OutputType 'Exe'.
The current OutputType is 'Library'.
dotnet watch ❌ [NetLift.DesktopBridge (net11.0)] Exited with error code 1
dotnet watch ⏳ Waiting for a file to change before restarting ...
```

The watcher was stopped after it remained waiting; it did not launch or update
the native host. Microsoft's `dotnet watch` documentation says it runs
`dotnet run` (or `dotnet build`/`dotnet test`) and applies Hot Reload to the
supported application it launches. It is not a launcher for an existing
Xcode/Swift native process or a general hostfxr process-attach/update
protocol.

### Direct method-body delta in an isolated native host

The CoreCLR runtime update mechanism was separately tested without modifying
`spikes/desktop-host/` or any Apple/Android host:

```sh
bash spikes/build-dev-loop/run-native-hot-reload-check.sh
```

The fixture compiles two versions of a plain `net11.0` target assembly with
the SDK's installed Roslyn compiler assemblies, emits a method-body delta,
and loads the baseline assembly into a separate native `hostfxr` executable.
The host sets `DOTNET_MODIFIABLE_ASSEMBLIES=debug` before initializing the
Debug `HostBridge` runtime config. The managed bridge loads the target into
the default load context and calls `MetadataUpdater.ApplyUpdate` with the
metadata, IL, and portable-PDB deltas generated by Roslyn. No NuGet package
or new tool was installed.

The exact observed semantic sequence was:

```text
DELTA-GENERATED: method=Probe.Observe token=0x06000001 metadata=448 il=45 pdb=504
METADATA_UPDATER_SUPPORTED=true
TARGET-ASSEMBLY-LOADED: NetLift.HotReloadTarget, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
OBSERVE_BEFORE=101,102
METADATA-DELTA-APPLIED
OBSERVE_AFTER=203
PASS native-hosted method-body delta preserved managed state.
```

`Probe.Observe` changed from `100 + ++_calls` to `200 + ++_calls`. The two
baseline calls returned 101 and 102; after applying the delta the next call
returned 203, showing that the updated body ran and the loaded assembly's
static counter was preserved. The checked-in SDK is
`11.0.100-rc.1.26425.128`, the runtime is
`Microsoft.NETCore.App 11.0.0-rc.1.26425.128`, and the native compiler is
Apple clang 21.0.0. Tool versions and exact native/reference pack paths are
retained in `spikes/build-dev-loop/.artifacts/native-hot-reload/logs/tool-versions.log`;
build, delta-generation, and host output are in the same `logs/` directory:
`bridge-build.log`, `delta-generator-build.log`, `delta-generation.log`,
`native-build.log`, and `native-host.log`. The runner contains the exact
`dotnet build`, `clang++`, and host invocation commands.

The tested prerequisites were a Debug runtime config with
`MetadataUpdater.IsSupported=true`, `DOTNET_MODIFIABLE_ASSEMBLIES=debug` set
before `hostfxr` initialization, a Roslyn delta generated from the exact same
baseline compilation/assembly bytes loaded by the host, and delivery of all
three delta blobs to that same process. The fixture has no local variables;
the baseline and delta were emitted with portable-PDB support. Only the
existing method body changed.

This proves the .NET 11 Debug runtime in a native-owned process can apply a
compatible, explicitly supplied method-body delta when modifiable assemblies
are enabled. It does **not** prove an IDE debugger can attach to an existing
host, that `dotnet watch` can produce or transport deltas to it, or that the
consumer-owned desktop/mobile apps have such integration. `vsdbg` was not
available on `PATH`; LLDB is a native debugger and was not used as a managed
Edit-and-Continue producer. Only an implementation-body edit with unchanged
types/signatures/layout was tested. Contract, ABI, Release, trimmed,
ReadyToRun, simulator, and device updates were not tested.

Visual Studio's Hot Reload documentation describes managed updates with a
debugger attached for supported .NET applications, but explicitly says
**Attach to Process** scenarios may not support Hot Reload. It also says
Release/custom configurations are unsupported; Debug is required, and
trimming and ReadyToRun are not supported for that workflow. This is a
development-time path, not a device Release/package update path. The docs'
platform support does not by itself prove debugger attachment to this
app-owned native macOS host or to an embedded CoreCLR in an iOS simulator,
Android emulator, or physical device. The direct API test above is a separate
mechanism proof, not evidence of IDE/debugger integration.

`MetadataUpdateHandlerAttribute` is a callback hook after an update has been
produced and applied: `ClearCache(Type[]?)` and `UpdateApplication(Type[]?)`
can invalidate caches or cause a UI to re-render. It does not watch source,
generate metadata deltas, attach a debugger, make unsupported edits legal,
or refresh native projections. `ApplicationPart` is an ASP.NET Core MVC
application discovery abstraction, not a general managed-library hot-swap
mechanism for a native bridge.

Treat implementation-only edits and contract edits differently:

* The isolated direct API proof shows a supported method-body delta can update
  a loaded assembly in a native-owned Debug process when the runtime is
  modifiable and a producer delivers the matching metadata/IL/PDB deltas.
  A product IDE/watch/debugger producer and transport are not integrated.
  Until they are, stop and relaunch the native host after rebuilding rather
  than overwrite a loaded DLL and report success.
* Adding/changing exported types, members, signatures, field layout, or
  bridge ABI affects the schema and generated language projections even if a
  managed compiler/debugger accepts some portion of the edit. Stop the host,
  rebuild the library and schema, regenerate both projections, rebuild the
  native target, validate checksums, then relaunch. Never apply a managed
  body-only Hot Reload result to an out-of-date native ABI.
* Simulator/emulator Debug runs are the appropriate next test environment
  for a future attach experiment. They remain separate from physical-device
  signing/entitlement/runtime-pack validation. Release, trimmed, and
  ReadyToRun builds use stop/build/redeploy; do not promise live update.

The existing `docs/spikes/desktop-host.md` and
`spikes/desktop-host/host.cpp` establish that a native macOS executable can
initialize CoreCLR and call a managed component. The new isolated fixture
adds a custom Roslyn delta producer and in-process `MetadataUpdater` call;
neither implements an IDE debugger attach or `dotnet watch` bridge for the
consumer-owned host. The bounded result is **direct Debug method-body delta
application works in the isolated native host; automatic debugger/watch
integration for the actual apps remains unproven**.

## Acceptance tests and fallback

Before calling the developer loop complete, require:

1. Run the incremental fixture and assert that implementation edits change
   the managed DLL but not schema/projection checksums; contract edits change
   both; native-only edits invoke no C# compiler and do not invoke generation.
2. Add a no-op run assertion that no output changes when all content hashes
   match, including when timestamps are touched without changing bytes.
3. Build/typecheck the generated Swift and Kotlin sources in every configured
   AGP variant and the owned Xcode app target. Confirm generated output paths
   are under DerivedData/Gradle `build/`, not authored sources. The Xcode
   app-target fixture establishes task-produced Swift/C source compilation
   and local execution, but not existing app integration, IDE indexing, or
   navigator visibility. The SwiftPM C/header compiler proof uses an
   artifact-only source-target package; direct BuildToolPlugin-to-C-target
   source registration remains unavailable in the tested toolchain.
4. Run SwiftUI and Compose previews with fake services and fail the preview
   test if it initializes CoreCLR or JNI. Separately run the real app-owned
   Debug bridge test on simulator/emulator.
5. Keep `run-native-hot-reload-check.sh` passing as the runtime-mechanism
   control. For product acceptance, wire a supported debugger or delta
   producer to the consumer-owned native host, edit a managed method body,
   invoke it again, and assert new output without process restart. Then make
   a public contract/ABI edit and require the stop/rebuild/relaunch path with
   matching schema hashes.
6. Repeat native build tests by variant/target in isolated output directories;
   reject missing or mismatched runtime/schema checksums before packaging.
7. Confirm separately that physical-device and Release builds use a full
   rebuild/redeploy path; no simulator result substitutes for signing,
   runtime-pack, trimming, or Release validation.

Until a debugger/delta producer is integrated into the consumer-owned host,
the safe product fallback is: implementation edit -> build managed library
-> stop/restart the native host; contract/ABI edit -> stop -> build managed
library and canonical schema -> regenerate Swift/Kotlin -> validate manifest
-> rebuild native app -> relaunch. Native-only edits stay in the ordinary
Xcode or Gradle incremental loop.

## First-party references

* [Swift Package Manager plugin documentation](https://docs.swift.org/latest/documentation/packagemanagerdocs/plugins/) and the [PackageDescription `BuildToolPlugin` API](https://developer.apple.com/documentation/packagedescription/buildtoolplugin) describe build-tool plugin inputs, outputs, and generated build products.
* [Apple: Running custom scripts during a build](https://developer.apple.com/documentation/xcode/running-custom-scripts-during-a-build) describes Xcode build-phase scripts and declared input/output files.
* [Apple: Previews in Xcode](https://developer.apple.com/documentation/swiftui/previews-in-xcode) documents SwiftUI preview macros and live previews.
* [Android: Compose previews](https://developer.android.com/develop/ui/compose/tooling/previews) documents `@Preview`, Layoutlib, and preview limitations.
* [Android: Extend the Android Gradle plugin](https://developer.android.com/build/extend-agp) describes the public AGP plugin APIs and provider-based task wiring; the AGP `addGeneratedSourceDirectory` variant-source path was exercised with the pinned 8.11.1 build.
* [AGP 8.11 `SourceDirectories`](https://developer.android.com/reference/tools/gradle-api/8.11/com/android/build/api/variant/SourceDirectories) documents the versioned `addGeneratedSourceDirectory` API used by the variant task.
* [Android Gradle Plugin 8.11.0 release notes](https://developer.android.com/build/releases/agp-8-11-0-release-notes) list Gradle 8.13/JDK 17 compatibility requirements for the AGP 8.11 line.
* [Android Gradle Plugin 9.2 `SourceDirectories`](https://developer.android.com/reference/tools/gradle-api/9.2/com/android/build/api/variant/SourceDirectories) documents `addGeneratedSourceDirectory` and why generated output should be wired to a task.
* [Microsoft: `dotnet watch`](https://learn.microsoft.com/dotnet/core/tools/dotnet-watch) documents the launched command and Hot Reload/restart behavior.
* [Microsoft: Hot Reload and Edit and Continue](https://learn.microsoft.com/en-us/visualstudio/debugger/hot-reload?view=vs-2022) documents debugger scenarios, supported platforms, and unsupported configurations/attach scenarios.
* [Microsoft: Extend .NET Hot Reload](https://learn.microsoft.com/en-us/visualstudio/debugger/hot-reload-metadataupdatehandler?view=vs-2022) documents `MetadataUpdateHandlerAttribute` callbacks.
* [Microsoft: `MetadataUpdater.IsSupported`](https://learn.microsoft.com/dotnet/api/system.reflection.metadata.metadataupdater.issupported?view=net-11.0) and [`MetadataUpdater.ApplyUpdate`](https://learn.microsoft.com/dotnet/api/system.reflection.metadata.metadataupdater.applyupdate?view=net-11.0) document runtime capability and applying metadata/IL/PDB deltas.
* [Roslyn: `Compilation.EmitDifference`](https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.compilation.emitdifference?view=roslyn-dotnet-5.0.0) documents generation of Edit-and-Continue metadata, IL, and PDB deltas.
* [Microsoft: Native .NET hosting](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting) documents `nethost`/`hostfxr`, loading managed methods from native code, and hosting limitations.
* [Microsoft: `ApplicationPart`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.applicationparts.applicationpart?view=aspnetcore-10.0) describes the ASP.NET Core MVC-specific abstraction.

## v5 native-host peer-pin provenance proof

The opt-in C/CoreCLR host now consumes its old/new peer hash pins and revision
floors from a generated `peer_provenance.h`, not hash literals in `host.c`.
The separately authored, versioned input is
`spikes/build-dev-loop/NativeHost/peer-provenance.lock`. The staging script
parses each actual generated v5 `CanonicalJson`, computes SHA-256 over
`json.dumps(schema, sort_keys=True, separators=(",", ":"), ensure_ascii=False)`
encoded as UTF-8, and requires that value to match both the envelope's
`schemaHash` and generated `SchemaHash` constant. It rejects unexpected
envelope/schema shapes, duplicate JSON keys, floating-point/nonstandard JSON
values, and any peer or minimum revision component that does not fit `uint32`.
It checks the configured minimum floor and writes a deterministic header plus
a content-hash manifest.
`host.c` uses these macros for both the trusted hash arguments to the frozen
start ABI and the test NLCS peer hellos. The runtime peer manifest/hello does
not supply or override the trusted pin.

The isolated executable proof ran with:

```sh
bash spikes/build-dev-loop/run-v5-peer-provenance-check.sh
```

It completed in
`spikes/build-dev-loop/.artifacts/nativehost-v5-provenance-20260928T152813Z-26567/`.
Both roots built the actual managed fixtures with
`--artifacts-path <root>/dotnet-artifacts`, invoked the native/CoreCLR host,
and passed the compatible-start old/new assertions. The generated inputs were:

| Fixture | Generated source | `schemaHash` | Generated revision | Lock minimum |
|---|---|---|---|---|
| Old | `root-a/dotnet-artifacts/obj/NetLift.Negotiation.NativeHost.Managed.Old/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs` | `7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184` | 1.0 | 1.0 |
| New | `root-a/dotnet-artifacts/obj/NetLift.Negotiation.NativeHost.Managed.New/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs` | `9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245` | 1.1 | 1.1 |

The staged `peer_provenance.h` bytes and staging-manifest bytes matched exactly
between `root-a` and `root-b`. Their SHA-256 values were respectively
`380040653c9d1a060045156acfe26264e6f28a20d8623db7285d59d203bcc562` and
`9c75bfe0b2961664327081ea02b88c05c42973c72d1a9d7c5763cf67b9333265`;
the ordered content input digest was
`83158ff4a72e33b149680388a083e5237d28fa89c1d0a8697c389114a92c0143`.
No managed DLL/PDB cross-root byte-identity claim is made.

The runner then exercised these negative cases in `root-a`:

| Case | Result |
|---|---|
| Tampered new `CanonicalJson` schema (`featureBits` changed while the declared hash and constant were retained) | Rejected because recomputed schema SHA-256 differs |
| Synthetically valid generated hash with contract major `4294967296` | Rejected because the generated revision does not fit `uint32` |
| Malformed contract revision | Rejected as non-canonical major.minor |
| Wrong 64-character peer hash | Rejected against generated new-peer `CanonicalJson` |
| Stale/incompatible floor 2.0 | Rejected because generated new peer is revision 1.1 |
| Minimum revision component `4294967296` | Rejected because lock revisions must fit `uint32` |

For each rejected input, the log had no `NATIVE-HOST-LINK` marker, the isolated
native executable was absent, and both staged pin outputs were withdrawn. The
valid lock then repaired the pin and reran the native host; a following
unchanged invocation emitted `PIN-STAGE-SKIP`, preserved both output mtimes,
and reran the host. Logs and the machine-readable stage manifest are retained
under the root's `root-a/logs/`, `root-b/logs/`, and `root-a/peer-pin/`
directories.
The successful log includes `PASS exported C/CoreCLR compatible-start v5
old/new proof`. Tool versions were .NET SDK
`11.0.100-rc.1.26425.128`, Python `3.14.6`, and Apple clang
`21.0.0 (clang-2100.3.34.2)`.

The first isolated attempt exposed that this SDK's artifacts path places the
fixture output directly under `release/`, not `release/net11.0/`; the runner
was corrected and the unsuccessful root was retained separately. The later
successful matrix above used the corrected paths. The retained failed root is
`spikes/build-dev-loop/.artifacts/nativehost-v5-provenance-20260928T151857Z-21602/`.
The default native-host runner still uses its existing shared build/output
locations when the `NETLIFT_NATIVEHOST_ARTIFACTS` override is absent; this
spike deliberately did not run that shared-output mode. In both modes the
runner now removes its previous native executable before either managed build
or pin staging, preventing an earlier executable from appearing current after
a failed build or pin check. The isolated mode redirects managed and native
outputs under its supplied artifact root.

This is build-time provenance consistency for the C host only. It neither
authenticates the lock nor establishes runtime peer authenticity, cross-version
trust delivery, mobile/Xcode/Gradle generated-source integration, signing, or
release behavior. In particular, it does not change the compiler owner's
work to carry validated peer metadata through other native interfaces.

## v5 native codec consumer staging fixture

The isolated follow-up under `spikes/build-dev-loop/V5NativeConsumer/` is
designed to stage the actual old/new v5 codecs emitted by `NetLift.Projection`
into native-owned consumer targets. Its `codec-schema.lock` is an
app-authored, versioned pin for the generated native codec schema. It is
distinct from both the managed endpoint's `GetManifest` schema and the
native-host peer-provenance lock; it is not a peer trust pin. The staging
helper parses each fixture's generated `CanonicalJson`, recomputes the
canonical schema SHA-256, checks the generated hash constant and contract
revision against the codec lock, and validates the emitted Swift/Kotlin codec
bytes before publishing a provenance manifest.

The fixture's `old` and `new` labels select codec versions for standalone
compile/encode checks; they do not represent a managed-local/native-peer
runtime pairing. Each fixture's managed-generated `CanonicalJson` is only the
declared emitter input; matching it to the independently authored codec lock
is a build-time output-consistency check. It does not treat that hash as
managed-local trust or as the NLPS peer hash. In an actual host call, the NLPS
schema comes from the native peer: old managed/new native uses the new codec,
while new managed/old native uses the old codec. This staging matrix does not
exercise those runtime pairings.

The Xcode fixture declares the selected generated Swift source, copied native
consumer source, and manifest as build-phase outputs under `DERIVED_FILE_DIR`.
The projection runner invokes the frozen `--emit-schema-v5-host-codecs` mode.
The AGP 8.11.1 fixture registers one generation task per Debug/Release variant
through `variant.sources.java.addGeneratedSourceDirectory`; each task stages
the generated Kotlin codec and a typed consumer under Gradle's generated Java
source directory. Kotlin compilation includes a copy of the native runtime's
`NegotiatedPeerContext` source and typechecks the generated encoder's
context-taking call. The Xcode fixture compiles the generated Swift codec and
a typed consumer function that accepts `NetLiftV5PeerContext`; its isolated
`NetLiftV5Host` module shim supplies only the descriptor's compile-time type
shape and is not used to construct a context. These checks do not call an
encoder without a native-issued context. Neither fixture uses testing-only
peer factories, decodes a native peer schema, performs app-to-app exchange,
installs an APK, or runs an emulator/device.

Run the isolated matrix with:

```sh
bash spikes/build-dev-loop/run-v5-native-consumer-check.sh
```

The runner builds the two managed v5 fixture inputs and `NetLift.Projection` with
separate `--artifacts-path` directories under each evidence root, then
exercises clean old/new generation, implementation-only and native UI-only
edits, corrupted schema/pin failure, repair, missing-output repair, unchanged
skip, and two-root generated-byte comparison. Logs, tool versions, copied
generated contracts, Gradle caches/build products, Xcode
DerivedData/OBJROOT/SYMROOT, and result bundles remain under the unique
`spikes/build-dev-loop/.artifacts/native-v5-consumer-<run-id>/` root.

The matrix uses independently app-authored codec pins for the old/new
generated native-codec schemas. The managed `CanonicalJson` files are emitter
inputs only; their hashes are not substituted for the native peer hash.
Generated host-bound APIs are checked for the context-taking signatures, but
actual peer-context issuance, encoder invocation, decode, negotiation, and
cross-version runtime calls remain out of scope. The Xcode descriptor shim
proves that generated source can typecheck against the frozen API shape, not
that the real host runtime module is integrated into the fixture.

### Host-bound codec matrix evidence

After the compiler's idiomatic Kotlin host-context property spelling
correction (`context.serviceId`, `context.methodId`, `context.dtoId`), the
full corrected runner using the frozen `--emit-schema-v5-host-codecs` emitter
passed in this fresh isolated root:

```text
spikes/build-dev-loop/.artifacts/native-v5-hostbound-codec-consumer-20260928T1302/
```

The exact single-entry command used was:

```sh
NETLIFT_NATIVE_GRAPH_ARTIFACTS="/Users/redth/code/netlift/spikes/build-dev-loop/.artifacts/native-v5-hostbound-codec-consumer-20260928T1302" \
  bash spikes/build-dev-loop/run-v5-native-consumer-check.sh
```

For an independent manager rerun, use a new, non-existing root under
`spikes/build-dev-loop/.artifacts/`:

```sh
NETLIFT_NATIVE_GRAPH_ARTIFACTS="/Users/redth/code/netlift/spikes/build-dev-loop/.artifacts/native-v5-hostbound-codec-consumer-manager-$(date -u +%Y%m%dT%H%M%SZ)-$$" \
  bash spikes/build-dev-loop/run-v5-native-consumer-check.sh
```

Observed toolchain: .NET SDK `11.0.100-rc.1.26425.128`, Xcode `27.1`
(`27A9269`), Swift `6.4.0.34.1`, Java `21.0.12`, Gradle `8.13`, and the
existing fixture's AGP `8.11.1` / Kotlin plugin `2.2.20`. The Xcode fixture
pins its headless build to arm64 and builds the descriptor-shape shim as a
static module at the target's macOS 14.0 minimum deployment version. The shim
is explicitly not the real native host runtime module.

Compiler evidence is retained in `root-a/logs/` and `root-b/logs/`:

| Consumer | Observed compile/run evidence |
| --- | --- |
| Xcode arm64 old/new | `xcode-clean-old.log` / `xcode-clean-new.log` contain `SwiftCompile normal arm64 Compiling NetLift.swift` and `NativeMain.swift`; the declared generation phase compiles/links the descriptor-only `NetLiftV5Host` shim before those sources. `xcode-host-old.log` / `xcode-host-new.log` print `NATIVE_V5_CODEC_TYPECHECK_PASS direction=old/new`. |
| AGP Debug and Release old/new | `gradle-clean-old.log` / `gradle-clean-new.log` contain `:app:compileDebugKotlin` and `:app:compileReleaseKotlin`; both variants compile the staged typed context-taking consumer. `NETLIFT-V5-CODEC` records the old/new generated Kotlin SHA for both variants. |
| Implementation-only and UI-only | Xcode codec staging reports `V5-CODEC-SKIP`; both Gradle codec generation tasks are `UP-TO-DATE`. Native UI sources recompile without rewriting generated codec outputs. |
| Fail-closed and repair | A corrupted new `CanonicalJson` fails Xcode generation before any `SwiftCompile`; a wrong new local codec pin fails both Gradle generation variants. The runner verifies generated Kotlin compile tasks do not run after the failure and staged outputs are removed. Restoring valid inputs reproduces the exact prior output hashes. |
| Missing-output and unchanged | Deleting one generated Swift or Kotlin output triggers byte-identical repair. The following build skips generation and preserves output mtimes. |

The exact schema and content-only output-set hashes were identical in
`root-a` and `root-b` (the runner compares the roots before succeeding):

| Native codec selection | Schema SHA-256 | Xcode Swift + manifest + consumer | AGP Debug Kotlin + manifest + consumer | AGP Release Kotlin + manifest + consumer |
| --- | --- | --- | --- | --- |
| Old, revision 1.0 | `7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184` | `d12d339bb206ed9d51827c36f3c67ff075a432c855edce3d6d53147cd132979d` | `17c77a2e2c7da791670e1c15ec1ec109618ecb8b522a95fee30b680f62a7d96d` | `17c77a2e2c7da791670e1c15ec1ec109618ecb8b522a95fee30b680f62a7d96d` |
| New, revision 1.1 | `9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245` | `0ee0615fdfc71cf6b6df7cf46c6b4a9cc8381dba4764853cb740dc3930b352ae` | `f5bf5edeb49cffad23ab7e800d2a3a2e0dd13e36a322f03b9edcc6e66437e4ce` | `f5bf5edeb49cffad23ab7e800d2a3a2e0dd13e36a322f03b9edcc6e66437e4ce` |

The generated Kotlin codec source hash is `521fb4bf22068e7322ad747c7377e09e5e304c95fb9e585b6c217cee4e2724c1`
for old and `137584440646e49310a20ba48487453b1852a68e47e37e2e88c0a48221d7f687`
for new; each value matches Debug and Release in both roots. Swift codec
hashes and native codec schema hashes remain unchanged from the prior
host-bound matrix. The updated Kotlin provenance and combined output-set
hashes reflect the corrected property spelling.

The complete per-file hash record is
`spikes/build-dev-loop/.artifacts/native-v5-hostbound-codec-consumer-20260928T1302/summary.txt`;
detailed logs include
`xcode-clean-old.log`, `xcode-clean-new.log`, `xcode-corrupted-canonical-schema.log`,
`xcode-repair-xcode.log`, `xcode-missing-xcode-output-repair.log`,
`xcode-unchanged-after-repair.log`, and the analogous `gradle-*.log` files.
The runner's final markers are `PASS corrupted schema/pin failed before
native compile; repairs restored outputs`, `PASS missing Xcode Swift and AGP
Kotlin outputs repaired byte-identically`, `PASS unchanged post-repair
Xcode/AGP builds skipped v5 codec generation`, and `PASS staged Swift/Kotlin
bytes and local schema manifests match across roots`.

This remains a compile/typecheck and generated-artifact consistency proof,
not runtime codec invocation, peer-context issuance, decode/negotiation,
managed/native compatibility, app exchange, or device behavior. The executable
only emits a fixture marker after checking the selected schema constant; its
context-taking encoder function is typechecked but not called. No signing or
publishing was performed.
