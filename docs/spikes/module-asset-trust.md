# App-owned module asset trust spike

**Status:** isolated macOS native-host experiment, verified 2026-09-29; not
product trust, a frozen ABI, or an `ACTIVE` gate. All executable experiment code is in
`spikes/module-asset-trust/`. It does not change the managed source authority,
the API-V1 registration path, mobile packages, or shared manager documents.

## Question and boundary

The current source authority creates an endpoint from a local manifest and
peer pins, while later native API-V1 registration can still supply a module
path and provider. This spike tests the missing ownership boundary: create one
immutable app-owned binding from a canonical local manifest, an absolute
module asset path, and a provider identifier, and reject later filesystem
substitution before attempting a path-based load.

`trust_binding_create` is a private experiment API, not a proposed public
ABI. It requires the manifest and module path to be absolute paths below one
resolved asset root. The manifest has two strict lines:

```text
provider=fixture.provider.v1
module_sha256=<64 lowercase hexadecimal digits>
```

The configured provider must match the manifest, and the module's SHA-256
must match the manifest. The binding retains open descriptors, canonical root
and asset paths, provider, file identities, and content hashes. Revalidation
walks from the retained root directory descriptor, refuses symlinks at every
asset-path component, checks the held and reopened file identities, and
rehashes both files before `dlopen`.

## Checks and demonstrated behavior

The native assertions cover:

* paths outside the configured root and a provider inconsistent with the
  manifest;
* a symlinked module-path component, a FIFO, writable module file, and
  writable parent directory;
* a module whose bytes do not match the manifest's SHA-256;
* manifest and module paths outside the root;
* successful load of the bound module and provider; and
* replacing the module path after binding: revalidation rejects the change,
  the held descriptor still refers to the original inode, and an unguarded
  path-based `dlopen` resolves the replacement module.

The last case is the important limitation: a retained descriptor pins the
opened file identity, but macOS `dlopen(absolute_path, ...)` takes a path, not
that descriptor. This experiment proves that a path replacement can make the
loader select different bytes than the still-open descriptor. The
pre-load recheck catches a swap that already happened; it cannot close the
remaining race between the last check and the loader opening the path, nor
can ordinary path validation make a mutable directory immutable. A package
owner must ensure the app-owned package location cannot be changed by an
attacker during this interval, or use a platform loading mechanism that
actually consumes the verified file identity.

The proof rejects write permission bits and effective write access on the
asset root, traversed asset directories, manifest, and module. This is a
deliberately strict policy: assets with writable modes are refused even if
the current process believes the package is otherwise trusted. `realpath`
resolves the configured root once; path components below that resolved root
are opened without following symlinks. Filesystem ACLs, mount replacement,
process compromise, and privileged writers are outside this small prototype.

## Consistency is not authenticity

SHA-256 here establishes content consistency between the manifest and module,
and detects changes relative to the binding snapshot. It does **not** prove
who authored the manifest or module. An attacker able to replace both with a
matching pair can satisfy this check; a provider string is an identifier, not
a credential, and a provider check after `dlopen` cannot prevent code or
initializers from running during the load. Authenticity requires an
independently trusted package/signing authority and a verification policy
anchored outside the mutable manifest (for example, platform-verified app
package integrity or an explicitly designed signature/key policy). This
spike implements neither signature verification nor a product trust claim.

## Android fd-loader follow-up

**Result:** an isolated API-36 arm64 emulator APK successfully loaded the
original module bytes from a verified fd after the corresponding app-private
path had been replaced with a different module. The app's packaged
`libfixture_v1.so` and `libfixture_v2.so` were extracted to the installed APK's
`nativeLibraryDir` (`useLegacyPackaging = true`), copied into app-private
files, and the v1 file was opened read-only. Java computed SHA-256 for the
packaged v1 module; native code hashed the bytes read from the pinned fd and
compared them with that package-derived digest before the swap. It retained
the fd, renamed the path's v1 inode aside, installed v2 at the original path,
and called `android_dlopen_ext` with `ANDROID_DLEXT_USE_LIBRARY_FD` and that
fd. The loaded code reported v1, returned the bound provider identifier, and
called a dependency packaged in the same APK's app library directory.

The NDK header specifies that `ANDROID_DLEXT_USE_LIBRARY_FD` uses
`library_fd` instead of opening by name, while `filename` remains part of
library identity. It also documents `ANDROID_DLEXT_FORCE_LOAD` and warns that
libraries sharing a `DT_SONAME` can affect dependency resolution; this test
does not exercise a previously loaded duplicate SONAME or use
`ANDROID_DLEXT_FORCE_LOAD`. The private `ANDROID_DLEXT_USE_NAMESPACE` flag is
identified in the header as internal-only and was not used. Provider lookup
occurs after dynamic loading, so it cannot authenticate a module or prevent
its initializers from running. The successful dependency check proves only
that this fixture dependency resolved in this installed app's namespace; it
does not prove arbitrary third-party dependencies, alternate namespaces,
linker configurations, or Android versions.

This is an extracted-library APK mode, not direct loading from an uncompressed
APK asset/file-descriptor offset, an AAB-derived split, or a physical device.
No physical device was used. The test APK was installed only on the connected
API-36 arm64 emulator and uninstalled by the runner afterward.
The APK used AGP's debug signing; the signing identity was not pinned or
independently authenticated. Here "package-derived" means byte consistency
with the installed package, not publisher authenticity.

The exact command used from the workspace root was:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files" \
  bash spikes/module-asset-trust/run-android-tests.sh
```

It ran with NDK `28.2.13676358`, CMake `3.30.3`, Gradle `8.13`, Android Gradle
Plugin `8.11.1`, and the API-36 arm64 emulator. The runner builds offline
using `spikes/android/NetLift.GradleApp/gradlew` against
`spikes/module-asset-trust/android`, selects only an `emulator-*` ADB serial,
refuses a pre-existing test package, and uninstalls its own package on exit.
Both Gradle output and CMake's `buildStagingDirectory` are rooted in the
unique child of `NETLIFT_TRUST_TMPDIR`; the runner fails if AGP recreates
`android/app/.cxx` in the source tree.
Exact native assertion output:

```text
netlift-module-asset-trust-android.SrwTng PASS form package and private module paths
netlift-module-asset-trust-android.SrwTng PASS copy packaged module variants to app-private files
netlift-module-asset-trust-android.SrwTng PASS pin regular module file descriptor
netlift-module-asset-trust-android.SrwTng PASS verified descriptor SHA-256 matches packaged APK module bytes
netlift-module-asset-trust-android.SrwTng PASS swap path after descriptor pin and hash verification
netlift-module-asset-trust-android.SrwTng PASS pinned fd retains original inode while path names replacement
netlift-module-asset-trust-android.SrwTng PASS android_dlopen_ext accepts verified fd after path replacement
netlift-module-asset-trust-android.SrwTng PASS provider symbol matches app-owned selection
netlift-module-asset-trust-android.SrwTng PASS fd loader maps original build rather than swapped path build
netlift-module-asset-trust-android.SrwTng PASS module dependency resolves in installed app linker namespace
RESULT:netlift-module-asset-trust-android.SrwTng:10/10 failures=0
```

See the
[NDK `android_dlextinfo` reference](https://developer.android.com/ndk/reference/structandroid__dlextinfo)
and [Bionic `dlext.h`](https://android.googlesource.com/platform/bionic/+/master/libc/include/android/dlext.h).
This Android result does not establish the macOS loader's behavior, Android
signature/authenticity policy, or product readiness.

## Apple app-bundle signature follow-up

**Result:** a temporary user-owned macOS `.app` bundle with a local module
manifest, a native `libmodule.dylib`, and a package-free .NET 11
`ManagedModule.dll` was signed ad hoc. Strict `codesign` and
`SecStaticCodeCheckValidity` verified the app resource seal. Changing the
managed assembly bytes or replacing the native module path with a symlink
caused subsequent `codesign --verify --deep --strict` to reject the bundle.
The bundle also contained signed v1/v2 module dylibs and a signed
`libmoduledep.dylib`.

The writable-bundle test copied the unchanged signed app, made its bundle and
module path owner-writable, and found that signature verification still
succeeded. The existing module binding rejected that writable bundle. For the
race test, the app first passed strict static code validation and the local
manifest/module hash binding while read-only. The test then changed the
`Frameworks` directory permissions, replaced the module path with another
already-signed nested dylib, and resumed the host. The held descriptor still
identified the original module inode, but path-based `dlopen` loaded the
replacement module (provider unchanged, build 2, dependency result 73). A
later code-signature verification detected the changed resource seal. This
demonstrates the verify-then-load gap in a writable, user-owned bundle; it
does not imply that an attacker can modify a protected system-installed
bundle.

### `/dev/fd/<fd>` loader experiment

The controlled swap was repeated in a fresh process and bundle copy for each
loader path to avoid reusing a previously loaded dyld image:

* Path-based `dlopen(Contents/Frameworks/libmodule.dylib)` loaded replacement
  v2 after the swap, and its dependency returned 73.
* `dlopen("/dev/fd/<pinned-module-fd>")` loaded original v1 even though the
  bundle path named v2. `SecStaticCodeCheckValidity` on a file URL for that
  `/dev/fd` path passed strict validation of the original dylib's ad hoc
  signature; its `@rpath/libmoduledep.dylib` dependency also resolved and
  returned 73.
* After either swap, a new validation of the app bundle rejected the changed
  resource seal.

The runner requires the explicit pinned-`/dev/fd` signature-validation PASS
and the expected original module build for the fd case. A missing signature
PASS, `dlopen` failure, unexpected module/provider/dependency result, or
unclassified nonzero host exit fails the test instead of producing a
success-shaped result.

This is an **observed macOS 26.7 behavior**, not a documented direct-fd dyld
API or a product guarantee. Apple's `dlopen(3)` describes a Mach-O file
specified by path; macOS `fd(4)` describes `/dev/fd/#` entries as filesystem
access to open descriptors. Those references describe the mechanisms
separately and do not contractually guarantee that
`dlopen("/dev/fd/N")` will preserve this behavior on future releases. The
experiment shows that, in this same process, `/dev/fd/N` referred to the
original open vnode and dyld loaded its bytes after the normal bundle path
was replaced.

Dependency resolution remains path-based. The fixture used an app
`@executable_path/../Frameworks` runpath to find its signed dependency; it
did not swap or independently fd-pin that dependency, race its lookup, or
test arbitrary transitive libraries/runpaths. The app's strict static check
validated nested code before the swap; the `/dev/fd` static-code check
validated the original module afterward. Both checks used ad hoc signatures
without a publisher requirement. Shipping use would still need an
authenticated identity, a stable dependency policy, and supported-loader
confirmation from Apple.

The app's signature was **ad hoc** (`Signature=adhoc`,
`TeamIdentifier=not set`). `codesign -d -r-` showed a cdhash-based designated
requirement, not a publisher identity. Ad hoc signing plus static validity
establishes local signature/resource consistency only: it does not identify
or authenticate a publisher. The run did not create a certificate, access a
keychain, invoke Gatekeeper assessment, notarize software, change OS trust
settings, install the app, or load the managed assembly into CoreCLR.

Publisher trust would need a non-ad-hoc signature whose certificate chain is
trusted under the intended macOS distribution policy, plus an explicit
`SecRequirement` checked by `SecStaticCodeCheckValidity` that constrains the
expected code identifier and publisher/team identity. For outside-the-store
macOS distribution, Developer ID signing and notarization/Gatekeeper policy
are separate distribution checks; this experiment tested neither. Even a
publisher-matching requirement does not remove the documented concurrent
modification limitation of static validation: after validation, code and
resource paths must remain immutable through load, or loading must consume the
verified file identity rather than reopen a mutable path.

For iOS distribution, this macOS result does not transfer. App Store
Guideline 2.5.2 requires apps to be self-contained and restricts downloading,
installing, or executing code that changes app functionality. Native code
must be embedded and signed as part of the app distribution under the
applicable Apple signing/provisioning model; there is no macOS Developer ID,
Gatekeeper, or notarization equivalent to infer from this ad hoc test.
No iOS bundle, device, or distribution workflow was exercised.

Apple references:
[SecStaticCodeCheckValidity](https://developer.apple.com/documentation/security/secstaticcodecheckvalidity%28_%3A_%3A_%3A%29.md)
(its API documentation warns that validation is only secure if code is not
concurrently modified),
[Dynamic Library Usage Guidelines](https://developer.apple.com/library/archive/documentation/DeveloperTools/Conceptual/DynamicLibraries/100-Articles/DynamicLibraryUsageGuidelines.html)
(the local macOS `dlopen(3)` and `fd(4)` manuals describe the two path/fd
mechanisms separately, not a combined dyld guarantee),
[Applying Code Requirements](https://developer.apple.com/documentation/security/applying_code_requirements),
[Developer ID](https://developer.apple.com/developer-id/),
[Notarizing macOS software](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution),
and [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/).

The exact command used from the workspace root was:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files" \
  bash spikes/module-asset-trust/apple/run-tests.sh
```

It used Apple clang 21.0.0, macOS 26.7, .NET SDK
`11.0.100-rc.1.26425.128`, `codesign`, and an empty local NuGet source; no
packages were restored from the network. The exact result was:

```text
PASS codesign verifies ad hoc app bundle and sealed resources
PASS signature is ad hoc and has no TeamIdentifier
# designated => cdhash H"85bb8cb96f113acccf3648f1c99fcd20699297ed"
PASS SecStaticCodeCheckValidity strict resource seal (ad hoc)
PASS local manifest provider and native module SHA-256
PASS managed assembly is a regular signed bundle resource
PASS retained module fd still identifies the originally verified inode
PASS loaded provider=fixture.provider.v1 module build=1 dependency=73
PASS codesign detects managed assembly resource content tampering
PASS codesign detects module path replaced by a symlink
PASS unchanged signature verifies while bundle and module path are writable
PASS app-owned module binder rejects writable bundle path
PASS SecStaticCodeCheckValidity strict resource seal (ad hoc)
PASS local manifest provider and native module SHA-256
PASS managed assembly is a regular signed bundle resource
PASS retained module fd still identifies the originally verified inode
PASS loaded provider=fixture.provider.v1 module build=2 dependency=73
PASS subsequent codesign verification detects the path-test module replacement
RESULT path loader resolved expected module build 2 and dependency 73
PASS SecStaticCodeCheckValidity strict resource seal (ad hoc)
PASS local manifest provider and native module SHA-256
PASS managed assembly is a regular signed bundle resource
PASS retained module fd still identifies the originally verified inode
PASS SecStaticCodeCheckValidity on pinned /dev/fd module
PASS loaded provider=fixture.provider.v1 module build=1 dependency=73
PASS subsequent codesign verification detects the fd-test module replacement
RESULT fd loader resolved expected module build 1 and dependency 73
Apple path and /dev/fd loader experiments completed
```

The exact code hash in the ad hoc designated requirement changes on rebuild.
The app bundle and dotnet build outputs were created only under the unique
session scratch directory and removed after the test.

When the product contract is frozen, the concrete integration policy to
consider is:

1. At app-owned `Create`, resolve and bind the trusted local manifest,
   absolute app-owned module asset, and provider together. Keep that binding
   immutable for the endpoint lifetime; later registration must select from
   it and must not accept a replacement path/provider.
2. Require the module and manifest to be inside the installed app's verified
   package root, reject traversal/symlink/non-regular/writable paths, and
   revalidate content and identity immediately before loading. Keep verified
   descriptors alive; prefer an fd-consuming platform loader when available.
3. Treat package/signature verification as the authenticity boundary.
   Manifest hashes are consistency pins, not signatures. Specify separately
   whether app updates, extraction/copying, runtime patching, and package
   directories writable by the app process are supported.

This is an integration recommendation only. It does not define or freeze
public ABI, claim product activation, or establish physical-device support.

## Reproduce

From the workspace root, run:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files" \
  bash spikes/module-asset-trust/run-tests.sh
```

`NETLIFT_TRUST_TMPDIR` is required, must be an absolute per-session scratch
directory, and cannot resolve under shared `/tmp` or `/var/tmp` locations.
The runner creates the directory if needed and removes only its uniquely
named child directory. Use the current session's own artifact directory in
place of the example path; do not use a shared temp directory.

The runner uses Apple Clang, macOS CommonCrypto, `shasum`, and shell tools;
it has no package restore, network, or third-party dependency. Verified on
macOS 26.7, arm64, Apple clang 21.0.0. The exact result was:

```text
PASS create read-only app-owned asset fixture
PASS bind canonical manifest, module path, and provider
PASS revalidate unchanged manifest and module identities plus SHA-256
PASS load only after path and content revalidation
PASS loaded module is the validated module build
PASS create negative-case asset fixture
PASS reject module outside configured root
PASS reject configured provider that differs from manifest
PASS create symlink-component fixture
PASS reject symlink in module path components
PASS create non-regular file fixture
PASS reject FIFO module asset
PASS reject writable module file
PASS reject writable parent directory
PASS create manifest/module hash disagreement fixture
PASS reject module bytes inconsistent with manifest SHA-256
PASS create out-of-root manifest fixture
PASS reject manifest outside configured root
PASS create isolated loader-path fixture
PASS bind original module before swap
PASS retain descriptor for original module identity
PASS replace validated path with a different module file
PASS open descriptor still names original inode after path replacement
PASS detect changed module path before dynamic loading
PASS refuse path-based load after detected swap
PASS path-based dlopen resolves replacement, not the pinned descriptor
26 assertions, 0 failures
```

This is an executable host-side prototype, not a test of the .NET 11 source
authority, an Apple production loader, or Android.
