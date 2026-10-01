# Local spike evidence retention

Git contains the authored source, commands, and bounded conclusions, **not**
the raw simulator/emulator or build output. Before archiving these child
sessions on 2026-10-01, the manager copied three entire `files/` trees and
nine selected sets of receipts into its own session artifacts directory,
comparing the SHA-256 of every source and destination file. Two children
had empty `files/` trees. The child histories remain restorable; the
original `session-state/<child-id>/files/` paths are not dependable.

Retained root on this machine:
`~/.copilot/session-state/7ff0c055-36c9-44dc-a367-aa9ae7b7060b/files/retained-child-evidence/`.
The root is **not in Git**; do not archive the manager session or delete its
files before relocating needed evidence elsewhere. For a rerun, substitute
the current active session's `files/` directory in the recorded commands.
Archiving removed the children from active coordination but did **not**
remove their original scratch directories on this machine at the last
inspection. The copies below make later, explicitly targeted cleanup
possible; they do not by themselves reclaim disk space or grant product
provenance. Leave original generated outputs in place until a separate
cleanup decision is made.

| Archived child | Files copied | Tree SHA-256 |
| --- | ---: | --- |
| Event stream ABI spike (`e4f0427d-38af-401f-8d8b-a1ad22200576`) | 2 | `9738f8e7d5730415efc66de9832ceadc49b7517bf11aa53d34a7f48b7343e7b5` |
| Android signer/split first-host (`d4a4216e-4be4-42e7-852b-f6d87c9fea92`) | 5 | `6184c615fa97b8c56d200145cb263c1eeba76f3c28501612211d45c2ebfb7f86` |
| Native captured registration seam (`8a760cfa-e246-4a5c-b808-af2ee65ee370`) | 586 | `49bc82ac2db37a344044be91049c974b008f3ba32859720d8780c505cd6429b2` |

For each retained child directory, the tree digest is SHA-256 over its
copied files, sorted by POSIX relative path, appending
`relative_path UTF-8 + NUL + raw 32-byte file SHA-256` for each.
The native seam tree includes 46 MB of **generated** builds as well as the
captured-image fixture; retaining it locally does not make those outputs
redistributable source. The Android child includes all five historical logs,
including `android-split-first-host-final-run-04.log`.

Nine more completed children were archived after **selected** artifacts,
rather than their entire scratch trees, were copied and individually
SHA-256-compared with the originals. The digest below covers the selected
copies only, with paths relative to each child's retained directory. For
the final five rows, selection was `.log`, `.png`, `.json`, `.txt`, or
`.md` files strictly smaller than 5,000,000 bytes, preserving paths; no
symlinks were selected. This includes metadata under generated trees,
**not** all original files in those trees:

| Archived child | Retained evidence | Files | Selected tree SHA-256 |
| --- | --- | ---: | --- |
| macOS CoreCLR (`1d4e0fbe-b6c2-440a-aad4-8e7ae4662db7`) | Five `coreclr-consumer-*.log` receipts; generated host/app build directories omitted | 5 | `4acdc8878b2126edf9e23ee7ce32891fe698ea297d64617182e6921427220bc9` |
| Module asset trust / macOS NativeAOT (`30594d60-6e7a-456b-b2b7-04b10a6451cc`) | `apple-nativeaot-macos-library/{*.log,run.9tmUU1/,publish/}`; NuGet/Swift caches and intermediate builds omitted | 20 | `743e5b3afd2e2e428ecbd1f53755dc0487a3ab0a1e50a5f9bb648815d0f524da` |
| Native build/debug loop (`df025315-1e36-4ea4-8772-640780c47a4f`) | Final `async-v5-native-matrix-20260928T1525Z/summary.txt` and `root-{a,b}/logs/`; earlier runs, Gradle/Xcode build outputs omitted | 25 | `769aedf46164f694063433bc76f3dbcfe6f669635268967f1fa2576b21a5338e` |
| Apple CoreCLR (`1d2635bf-f83f-43e9-a1a8-b349fe90a1a8`) | 531 `.log`, `.png`, `.json`, `.txt`, `.md` files smaller than 5 MB, with their original relative paths; generated simulator/device binaries and compiler caches omitted | 531 | `d04e31f00525e367b00831be10f17c5238e89a92236ada69ec4799d9b9c89ef4` |
| Contract compiler (`677d34d7-ef98-47fe-b9ed-081edfc603fc`) | Selected `.log`, `.png`, `.json`, `.txt`, `.md` receipts below 5 MB each; generated compiler/build binaries omitted | 2,134 | `25b4e10f834c96bb9b5df67105f50a35cee66a35b005fba413ae208165313aa0` |
| Async native result descriptor (`4d0ea695-6414-4b9d-bb5f-77ef1a81277f`) | Same selected receipt extensions and limit; native/CoreCLR test binaries omitted | 5,770 | `41a009582f84d3ac36597dadc7dfa76e2cba5000b4ca6e1e49bf3119785ebac6` |
| Android CoreCLR (`11a5dd1a-53b1-4770-940c-ba9701dc2585`) | Same selected receipt extensions and limit; APK/AAB/AAR, Gradle binaries and large R8 mapping files omitted | 2,899 | `00ebb4d0b12d2e53ccac5dd8480501e8aacb3af5f829d8551c0f1b3febcceb35` |
| Android async callback app (`af9f8322-1838-4870-9bef-fc462b01b753`) | Same selected receipt extensions and limit; APK/AAB/AAR, Gradle binaries and large R8 mapping files omitted | 7,745 | `346a0e9d7cb56eecd42f206b8d95271749345aa14ce3a9d6af24982181b97df5` |
| Apple async result simulator (`ed48d086-da6d-45b1-85eb-099b5dfef057`) | Same selected receipt extensions and limit; simulator/device app bundles and derived builds omitted | 2,770 | `0bb43c80616ccdb7247f4f181c2a082c05d30cf6795426c53181694de78e6450` |

The copied NativeAOT `run.9tmUU1/NativeAotHost.app` passed
`codesign --verify --deep --strict` after relocation. This preserves the
bounded local positive/negative run materials and published dylib, not a
redistributable, notarized package. The omitted build and package caches
can be regenerated with the source-owned runners and the requirements
recorded in their reports; do not claim the archived original scratch is
still available or that the omitted intermediate bits are preserved.

Independent manager logs already under the manager session's `files/` root
remain separate from these child copies:

| Independent receipt | SHA-256 |
| --- | --- |
| `native-captured-registration-seam-manager.log` | `3ed46314180eb77acdd876773f89114b8a1f3947371894303ce9778619e811db` |
| `android-split-first-host-manager.log` | `b36d72d2d7eee401891a7fd297c67d8389f354ae8cefd382ac0e53f83a56b653` |
| `verify-streams-manager.log` | `79c765e302b91ad229ab158b50ba59f44eb10575d432c1fc1073f5ec262c6fde` |
| `optional-native-manager.log` | `73a188535f6afc9dcfdb5250d7a87e94577b427c3ad0a4e0a69ad5e1494465b3` |
| `buffer-managed-manager.log` | `9f1235d1b318e2b487f235aca0b9fb0850841c044f1d2390c996d51ea5174150` |

The manager root also contains the separate macOS two-contract,
SwiftPM/Xcode build-graph, AGP variant and native hot-reload rerun logs
referenced in [status](status.md). These remain outside Git.

Two earlier archived children, Native-only baselines and Managed assembly
load identity, had empty `files/` directories; their source and bounded
outcomes are in Git, and the latter report's old scratch path is historical.
All 14 child sessions created under this manager were archived: two had
empty `files/`, three have full retained copies, and nine have explicitly
selected retained receipts. **Archiving alone did not remove original
session files** at the last disk inspection, and the selected-copy
policy intentionally does not preserve every original build artifact.
Reproduce a proof rather than mistaking a retained log for its signed
package or device-execution evidence. Original child artifacts should
not be manually removed without a separate targeted inventory and
retention decision.
