#!/usr/bin/env python3
import json
import re
import sys
import zipfile


def fail(message):
    raise ValueError(message)


def unique_fields(pairs):
    fields = {}
    for key, value in pairs:
        if key in fields:
            fail(f"duplicate contract manifest field: {key}")
        fields[key] = value
    return fields


def main():
    archive, manifest_path, asset_directory, *expected_dlls = sys.argv[1:]
    with zipfile.ZipFile(archive) as package:
        if package.getinfo(manifest_path).file_size > 65536:
            fail("contract manifest exceeds the 64 KiB limit")
        manifest = json.loads(
            package.read(manifest_path).decode("utf-8"),
            object_pairs_hook=unique_fields,
            parse_constant=lambda value: fail(f"invalid JSON constant: {value}"),
        )
        if not isinstance(manifest, dict):
            fail("contract manifest must be a JSON object")
        assembly = manifest.get("assemblyName")
        if not isinstance(assembly, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*", assembly):
            fail("contract manifest assemblyName must be a safe assembly name")
        if f"{assembly}.dll" not in expected_dlls:
            fail(f"contract manifest assemblyName does not match expected app assemblies: {assembly}")
        if f"{manifest_path.rsplit('/', 1)[0]}/dotnet/{assembly}.dll" not in package.namelist():
            fail(f"contract manifest assembly missing from app payload: {assembly}.dll")
        if manifest.get("managedAssetDirectory") != asset_directory:
            fail("contract manifest managedAssetDirectory does not match the app asset root")
        if type(manifest.get("abiVersion")) is not int or manifest["abiVersion"] != 1:
            fail("contract manifest abiVersion must be 1")
        bits = manifest.get("requiredFeatureBits")
        if type(bits) is not int or not (0 < bits <= 0xFFFFFFFF):
            fail("contract manifest requiredFeatureBits must be a nonzero uint32")
        schema_hash = manifest.get("schemaHash")
        if not isinstance(schema_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", schema_hash):
            fail("contract manifest schemaHash must be 64 lowercase hex characters")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, UnicodeError, KeyError, RecursionError, zipfile.BadZipFile) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
