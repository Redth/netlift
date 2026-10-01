#!/usr/bin/env python3
import struct
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_u16(data, offset):
    return struct.unpack_from("<H", data, offset)[0]


def read_u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def rva_to_offset(rva, sections):
    for virtual_address, virtual_size, raw_offset, raw_size in sections:
        span = max(virtual_size, raw_size)
        if virtual_address <= rva < virtual_address + span:
            delta = rva - virtual_address
            require(delta < raw_size, "RVA has no corresponding raw file bytes")
            return raw_offset + delta
    raise ValueError(f"RVA 0x{rva:x} is outside PE sections")


def has_ready_to_run(path):
    data = open(path, "rb").read()
    require(data[:2] == b"MZ", "not a PE image")
    pe_offset = read_u32(data, 0x3C)
    require(data[pe_offset:pe_offset + 4] == b"PE\0\0", "invalid PE signature")

    coff_offset = pe_offset + 4
    section_count = read_u16(data, coff_offset + 2)
    optional_size = read_u16(data, coff_offset + 16)
    optional_offset = coff_offset + 20
    magic = read_u16(data, optional_offset)
    if magic == 0x20B:
        directories_offset = optional_offset + 112
    elif magic == 0x10B:
        directories_offset = optional_offset + 96
    else:
        raise ValueError(f"unsupported PE optional-header magic 0x{magic:x}")

    cli_directory_offset = directories_offset + 14 * 8
    cli_rva = read_u32(data, cli_directory_offset)
    cli_size = read_u32(data, cli_directory_offset + 4)
    require(cli_rva != 0 and cli_size >= 72, "CLR header is missing")

    section_offset = optional_offset + optional_size
    sections = []
    for index in range(section_count):
        offset = section_offset + index * 40
        sections.append((
            read_u32(data, offset + 12),
            read_u32(data, offset + 8),
            read_u32(data, offset + 20),
            read_u32(data, offset + 16),
        ))

    cli_offset = rva_to_offset(cli_rva, sections)
    native_rva = read_u32(data, cli_offset + 64)
    native_size = read_u32(data, cli_offset + 68)
    if native_rva == 0 or native_size < 12:
        return False

    native_offset = rva_to_offset(native_rva, sections)
    return read_u32(data, native_offset) == 0x00525452


def main():
    if len(sys.argv) != 3 or sys.argv[2] not in {"r2r", "il"}:
        print(f"usage: {sys.argv[0]} ASSEMBLY.dll r2r|il", file=sys.stderr)
        return 2
    path, expectation = sys.argv[1:]
    try:
        found = has_ready_to_run(path)
    except (OSError, ValueError, struct.error) as error:
        print(f"FAIL ReadyToRun inspection: {error}", file=sys.stderr)
        return 1

    if expectation == "r2r" and found:
        print(f"PASS ReadyToRun header signature verified: {path}")
        return 0
    if expectation == "il" and not found:
        print(f"PASS IL-only control has no ReadyToRun header: {path}")
        return 0
    if found:
        print(f"FAIL unexpected ReadyToRun header: {path}", file=sys.stderr)
    else:
        print(f"FAIL expected ReadyToRun header is absent: {path}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
