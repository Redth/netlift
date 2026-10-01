#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_RUN="$ROOT/tests/NetLift.Negotiation.NativeHost/run.sh"
APP_LOCK="$ROOT/spikes/build-dev-loop/NativeHost/peer-provenance.lock"
ARTIFACTS_BASE="$ROOT/spikes/build-dev-loop/.artifacts"
RUN_ID="${NETLIFT_V5_PROVENANCE_RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)-$$}"
RUN_ROOT="${NETLIFT_V5_PROVENANCE_ARTIFACTS:-$ARTIFACTS_BASE/nativehost-v5-provenance-$RUN_ID}"

if [[ -e "$RUN_ROOT" ]]; then
    printf 'Refusing to overwrite existing provenance evidence root: %s\n' "$RUN_ROOT" >&2
    exit 1
fi
mkdir -p "$(dirname "$RUN_ROOT")"
mkdir "$RUN_ROOT"

ROOT_A="$RUN_ROOT/root-a"
ROOT_B="$RUN_ROOT/root-b"
mkdir -p "$ROOT_A/inputs" "$ROOT_A/logs" "$ROOT_B/inputs" "$ROOT_B/logs"
cp "$APP_LOCK" "$ROOT_A/inputs/peer-provenance.lock"
cp "$APP_LOCK" "$ROOT_B/inputs/peer-provenance.lock"

printf 'Evidence root: %s\n' "$RUN_ROOT"
{
    printf 'dotnet: '
    dotnet --version
    printf 'python: '
    python3 --version
    printf 'clang: '
    clang --version | head -n 1
} > "$RUN_ROOT/tool-versions.txt"

run_host() {
    local artifacts="$1"
    local lock="$2"
    local log="$3"
    NETLIFT_NATIVEHOST_ARTIFACTS="$artifacts" \
        NETLIFT_V5_PEER_PIN_LOCK="$lock" \
        bash "$HOST_RUN" > "$log" 2>&1
}

run_host "$ROOT_A" "$ROOT_A/inputs/peer-provenance.lock" "$ROOT_A/logs/valid-initial.log"
run_host "$ROOT_B" "$ROOT_B/inputs/peer-provenance.lock" "$ROOT_B/logs/valid-second-root.log"

PIN_A="$ROOT_A/peer-pin"
PIN_B="$ROOT_B/peer-pin"
cmp "$PIN_A/peer_provenance.h" "$PIN_B/peer_provenance.h"
cmp "$PIN_A/peer_provenance.manifest.json" "$PIN_B/peer_provenance.manifest.json"
printf 'PASS deterministic pin header and staging manifest across roots\n'

OLD_GENERATED="$ROOT_A/dotnet-artifacts/obj/NetLift.Negotiation.NativeHost.Managed.Old/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
NEW_GENERATED="$ROOT_A/dotnet-artifacts/obj/NetLift.Negotiation.NativeHost.Managed.New/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
rm -f "$ROOT_A/native-host/netlift-compatible-start-v5-host"
python3 - "$NEW_GENERATED" "$ROOT_A/inputs/tampered-schema.g.cs" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text(encoding="utf-8-sig")
needle = r'\"featureBits\":16'
if source.count(needle) != 1:
    raise SystemExit("could not isolate the new contract's featureBits field")
Path(sys.argv[2]).write_text(source.replace(needle, r'\"featureBits\":17', 1),
                              encoding="utf-8")
PY
if python3 "$ROOT/spikes/build-dev-loop/stage-v5-peer-pin.py" \
    "$ROOT_A/inputs/peer-provenance.lock" "$OLD_GENERATED" \
    "$ROOT_A/inputs/tampered-schema.g.cs" "$PIN_A" \
    > "$ROOT_A/logs/tampered-schema.log" 2>&1; then
    printf 'Expected tampered CanonicalJson schema rejection\n' >&2
    exit 1
fi
if ! grep -q 'CanonicalJson schema SHA-256 does not match declared schemaHash' \
    "$ROOT_A/logs/tampered-schema.log" ||
    grep -q 'NATIVE-HOST-LINK:' "$ROOT_A/logs/tampered-schema.log" ||
    [[ -e "$PIN_A/peer_provenance.h" ||
       -e "$PIN_A/peer_provenance.manifest.json" ||
       -e "$ROOT_A/native-host/netlift-compatible-start-v5-host" ]]; then
    printf 'Tampered schema did not fail closed before native linking\n' >&2
    exit 1
fi
printf 'PASS tampered CanonicalJson schema rejected; stale pin and executable withdrawn\n'

python3 - "$NEW_GENERATED" "$ROOT_A/inputs" "$ROOT_A/inputs/peer-provenance.lock" <<'PY'
from hashlib import sha256
import json
from pathlib import Path
import re
import sys

source_path = Path(sys.argv[1])
output_dir = Path(sys.argv[2])
valid_lock = Path(sys.argv[3]).read_text(encoding="utf-8")
source = source_path.read_text(encoding="utf-8-sig")
literal_pattern = re.compile(r'public const string CanonicalJson = ("(?:\\.|[^"\\])*");')
match = literal_pattern.search(source)
if match is None:
    raise SystemExit("generated CanonicalJson string was not found")
canonical = json.loads(json.loads(match.group(1)))
canonical["schema"]["contractRevision"]["major"] = 4294967296
schema_bytes = json.dumps(
    canonical["schema"],
    sort_keys=True,
    separators=(",", ":"),
    ensure_ascii=False,
    allow_nan=False,
).encode("utf-8")
new_hash = sha256(schema_bytes).hexdigest()
canonical["schemaHash"] = new_hash
canonical_text = json.dumps(canonical, separators=(",", ":"), ensure_ascii=False)
new_literal = json.dumps(canonical_text, ensure_ascii=False)
source = source[:match.start(1)] + new_literal + source[match.end(1):]
source, count = re.subn(
    r'(public const string SchemaHash = ")[0-9a-f]{64}(");',
    rf'\g<1>{new_hash}\g<2>;',
    source,
)
if count != 1:
    raise SystemExit("generated SchemaHash constant was not uniquely found")
(output_dir / "overflow-contract-revision.g.cs").write_text(source, encoding="utf-8")
(output_dir / "overflow-contract-revision.lock").write_text(
    valid_lock.replace(
        "expectedNewPeerSchemaSha256=9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245",
        f"expectedNewPeerSchemaSha256={new_hash}",
    ),
    encoding="utf-8",
)
PY
if python3 "$ROOT/spikes/build-dev-loop/stage-v5-peer-pin.py" \
    "$ROOT_A/inputs/overflow-contract-revision.lock" "$OLD_GENERATED" \
    "$ROOT_A/inputs/overflow-contract-revision.g.cs" "$PIN_A" \
    > "$ROOT_A/logs/overflow-contract-revision.log" 2>&1; then
    printf 'Expected generated uint32 contract revision rejection\n' >&2
    exit 1
fi
if ! grep -q 'contract revision components must fit uint32' \
    "$ROOT_A/logs/overflow-contract-revision.log" ||
    grep -q 'NATIVE-HOST-LINK:' "$ROOT_A/logs/overflow-contract-revision.log" ||
    [[ -e "$PIN_A/peer_provenance.h" || -e "$PIN_A/peer_provenance.manifest.json" ]]; then
    printf 'Out-of-range generated contract revision did not fail closed\n' >&2
    exit 1
fi
printf 'PASS generated contract revision above uint32 rejected before native linking\n'

python3 - "$ROOT_A/inputs/peer-provenance.lock" "$ROOT_A/inputs" <<'PY'
from pathlib import Path
import sys

valid = Path(sys.argv[1]).read_text(encoding="utf-8")
output = Path(sys.argv[2])
variants = {
    "malformed": valid.replace("minimumNewPeerContractRevision=1.1",
                               "minimumNewPeerContractRevision=not-a-revision"),
    "wrong-hash": valid.replace(
        "expectedNewPeerSchemaSha256=9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245",
        "expectedNewPeerSchemaSha256=" + "0" * 64),
    "stale-floor": valid.replace("minimumNewPeerContractRevision=1.1",
                                 "minimumNewPeerContractRevision=2.0"),
    "uint32-floor": valid.replace("minimumNewPeerContractRevision=1.1",
                                  "minimumNewPeerContractRevision=4294967296.0"),
}
for name, content in variants.items():
    (output / f"{name}.lock").write_text(content, encoding="utf-8")
PY

expect_rejected_before_link() {
    local name="$1"
    local expected_text="$2"
    local lock="$ROOT_A/inputs/$name.lock"
    local log="$ROOT_A/logs/$name.log"
    if run_host "$ROOT_A" "$lock" "$log"; then
        printf 'Expected %s lock rejection, but host runner succeeded\n' "$name" >&2
        exit 1
    fi
    if grep -q 'NATIVE-HOST-LINK:' "$log"; then
        printf '%s lock reached native compile/link\n' "$name" >&2
        exit 1
    fi
    if [[ -e "$ROOT_A/native-host/netlift-compatible-start-v5-host" ]]; then
        printf '%s lock left a stale native host executable\n' "$name" >&2
        exit 1
    fi
    if [[ -e "$PIN_A/peer_provenance.h" || -e "$PIN_A/peer_provenance.manifest.json" ]]; then
        printf '%s lock left stale staged pin output\n' "$name" >&2
        exit 1
    fi
    if ! grep -q "$expected_text" "$log"; then
        printf '%s lock failed for an unexpected reason; see %s\n' "$name" "$log" >&2
        exit 1
    fi
    printf 'PASS %s lock rejected before native compile/link; staged pin withdrawn\n' "$name"
}

expect_rejected_before_link malformed 'minimumNewPeerContractRevision must be a canonical major.minor revision'
expect_rejected_before_link wrong-hash 'does not match generated new peer CanonicalJson schemaHash'
expect_rejected_before_link stale-floor 'is above the generated new peer contract revision'
expect_rejected_before_link uint32-floor 'minimumNewPeerContractRevision components must fit uint32'

run_host "$ROOT_A" "$ROOT_A/inputs/peer-provenance.lock" "$ROOT_A/logs/valid-repair.log"
cmp "$PIN_A/peer_provenance.h" "$PIN_B/peer_provenance.h"
cmp "$PIN_A/peer_provenance.manifest.json" "$PIN_B/peer_provenance.manifest.json"
python3 - "$ROOT_A" <<'PY'
from pathlib import Path
import sys

root = Path(sys.argv[1])
header = root / "peer-pin" / "peer_provenance.h"
manifest = root / "peer-pin" / "peer_provenance.manifest.json"
(root / "before-repair-stamps.txt").write_text(
    f"{header.stat().st_mtime_ns}\n{manifest.stat().st_mtime_ns}\n",
    encoding="ascii",
)
PY
run_host "$ROOT_A" "$ROOT_A/inputs/peer-provenance.lock" "$ROOT_A/logs/valid-incremental-skip.log"
grep -q '^PIN-STAGE-SKIP ' "$ROOT_A/logs/valid-incremental-skip.log"
python3 - "$ROOT_A" <<'PY'
from pathlib import Path
import sys

root = Path(sys.argv[1])
header = root / "peer-pin" / "peer_provenance.h"
manifest = root / "peer-pin" / "peer_provenance.manifest.json"
before = [int(line) for line in (root / "before-repair-stamps.txt").read_text().splitlines()]
after = [header.stat().st_mtime_ns, manifest.stat().st_mtime_ns]
if before != after:
    raise SystemExit("incremental pin-stage skip rewrote staged files")
print("PASS repaired pin stage, then skipped unchanged inputs without rewriting outputs")
PY

cat > "$RUN_ROOT/summary.txt" <<EOF
v5 native-host app-owned peer-pin proof
rootA=$ROOT_A
rootB=$ROOT_B
old expected peer: 7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184 revision 1.0 floor 1.0
new expected peer: 9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245 revision 1.1 floor 1.1
header sha256=$(shasum -a 256 "$PIN_A/peer_provenance.h" | awk '{print $1}')
stage manifest sha256=$(shasum -a 256 "$PIN_A/peer_provenance.manifest.json" | awk '{print $1}')
EOF
printf 'Evidence summary: %s\n' "$RUN_ROOT/summary.txt"
