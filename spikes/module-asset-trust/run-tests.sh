#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
spike_dir="$repo_root/spikes/module-asset-trust"
if [ -z "${NETLIFT_TRUST_TMPDIR:-}" ]; then
    printf '%s\n' \
        'NETLIFT_TRUST_TMPDIR is required; set it to a per-session scratch directory outside shared /tmp.' >&2
    exit 2
fi
case "$NETLIFT_TRUST_TMPDIR" in
    /*) ;;
    *)
        printf '%s\n' 'NETLIFT_TRUST_TMPDIR must be an absolute path.' >&2
        exit 2
        ;;
esac
if ! mkdir -p "$NETLIFT_TRUST_TMPDIR"; then
    printf 'Could not create NETLIFT_TRUST_TMPDIR: %s\n' "$NETLIFT_TRUST_TMPDIR" >&2
    exit 2
fi
work_root=$(CDPATH= cd -- "$NETLIFT_TRUST_TMPDIR" && pwd -P) || {
    printf 'Could not resolve NETLIFT_TRUST_TMPDIR: %s\n' "$NETLIFT_TRUST_TMPDIR" >&2
    exit 2
}
case "$work_root" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf '%s\n' \
            'NETLIFT_TRUST_TMPDIR must not resolve inside a shared system temp directory.' >&2
        exit 2
        ;;
esac
work=$(mktemp -d "$work_root/netlift-module-asset-trust.XXXXXX")
work=$(cd "$work" && pwd -P)
cleanup() {
    chmod -R u+w "$work" 2>/dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT HUP INT TERM

clang -std=c11 -Wall -Wextra -Werror \
    "$spike_dir/fixture_module.c" -dynamiclib \
    -DFIXTURE_PROVIDER='"fixture.provider.v1"' -DFIXTURE_BUILD=1 \
    -o "$work/module-v1.dylib"
clang -std=c11 -Wall -Wextra -Werror \
    "$spike_dir/fixture_module.c" -dynamiclib \
    -DFIXTURE_PROVIDER='"fixture.provider.v1"' -DFIXTURE_BUILD=2 \
    -o "$work/module-v2.dylib"
hash_v1=$(shasum -a 256 "$work/module-v1.dylib" | awk '{ print $1 }')
hash_v2=$(shasum -a 256 "$work/module-v2.dylib" | awk '{ print $1 }')

clang -std=c11 -Wall -Wextra -Werror \
    "$spike_dir/module_asset_trust.c" \
    "$spike_dir/test_module_asset_trust.c" \
    -o "$work/module-asset-trust-tests"

"$work/module-asset-trust-tests" \
    "$work" "$work/module-v1.dylib" "$work/module-v2.dylib" \
    "$hash_v1" "$hash_v2"
