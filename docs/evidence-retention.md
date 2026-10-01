# Local spike evidence retention

Git contains the authored source, commands, and bounded conclusions, **not**
the raw simulator/emulator or build output. Before archiving the child sessions
below on 2026-10-01, the manager copied each entire `files/` tree into its
own session artifacts directory and compared the SHA-256 of every source and
destination file. The child histories remain restorable; the original
`session-state/<child-id>/files/` paths are no longer reliable.

Retained root on this machine:
`~/.copilot/session-state/7ff0c055-36c9-44dc-a367-aa9ae7b7060b/files/retained-child-evidence/`.
The root is **not in Git**; do not archive the manager session or delete its
files before relocating needed evidence elsewhere. For a rerun, substitute
the current active session's `files/` directory in the recorded commands.

| Archived child | Files copied | Tree SHA-256 |
| --- | ---: | --- |
| Event stream ABI spike (`e4f0427d-38af-401f-8d8b-a1ad22200576`) | 2 | `9738f8e7d5730415efc66de9832ceadc49b7517bf11aa53d34a7f48b7343e7b5` |
| Android signer/split first-host (`d4a4216e-4be4-42e7-852b-f6d87c9fea92`) | 5 | `6184c615fa97b8c56d200145cb263c1eeba76f3c28501612211d45c2ebfb7f86` |
| Native captured registration seam (`8a760cfa-e246-4a5c-b808-af2ee65ee370`) | 586 | `49bc82ac2db37a344044be91049c974b008f3ba32859720d8780c505cd6429b2` |

The tree digest is SHA-256 over each file, sorted by POSIX relative path,
appending `relative_path UTF-8 + NUL + raw 32-byte file SHA-256` for each.
The native seam tree includes 46 MB of **generated** builds as well as the
captured-image fixture; retaining it locally does not make those outputs
redistributable source. The Android child includes all five historical logs,
including `android-split-first-host-final-run-04.log`.

Independent manager logs already under the manager session's `files/` root
remain separate from these child copies:

| Independent receipt | SHA-256 |
| --- | --- |
| `native-captured-registration-seam-manager.log` | `3ed46314180eb77acdd876773f89114b8a1f3947371894303ce9778619e811db` |
| `android-split-first-host-manager.log` | `b36d72d2d7eee401891a7fd297c67d8389f354ae8cefd382ac0e53f83a56b653` |
| `verify-streams-manager.log` | `79c765e302b91ad229ab158b50ba59f44eb10575d432c1fc1073f5ec262c6fde` |
| `optional-native-manager.log` | `73a188535f6afc9dcfdb5250d7a87e94577b427c3ad0a4e0a69ad5e1494465b3` |
| `buffer-managed-manager.log` | `9f1235d1b318e2b487f235aca0b9fb0850841c044f1d2390c996d51ea5174150` |

Two earlier archived children, Native-only baselines and Managed assembly
load identity, had empty `files/` directories; their source and bounded
outcomes are in Git, and the latter report's old scratch path is historical.
The nine other completed children still own raw artifacts and are **not**
cleared for retirement by this ledger. Reproduce the proof rather than
mistaking an archived run artifact for release/device evidence.
