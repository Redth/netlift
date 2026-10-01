#!/usr/bin/env python3
import shutil
import struct
import sys
import zipfile

source, destination, member = sys.argv[1:4]
shutil.copyfile(source, destination)

with zipfile.ZipFile(destination, "r") as apk:
    if apk.testzip() is not None:
        raise SystemExit("Source APK failed ZIP CRC validation.")
    offset = apk.start_dir
    with open(destination, "r+b") as output:
        while True:
            output.seek(offset)
            signature = output.read(4)
            if signature != b"PK\x01\x02":
                raise SystemExit(f"Central directory entry not found: {member}")
            header = output.read(42)
            if len(header) != 42:
                raise SystemExit("Truncated central directory.")
            fields = struct.unpack("<6H3I5H2I", header)
            filename_length, extra_length, comment_length = fields[9:12]
            filename = output.read(filename_length).decode("utf-8")
            if filename == member:
                external_attributes = fields[14]
                output.seek(offset + 38)
                output.write(struct.pack("<I", external_attributes ^ 1))
                break
            offset += 46 + filename_length + extra_length + comment_length

with zipfile.ZipFile(destination, "r") as apk:
    if apk.testzip() is not None:
        raise SystemExit("Tampered APK no longer passes ZIP CRC validation.")

print(f"TAMPERED_MEMBER={member}")
print("TAMPER=central-directory-external-attributes-bit")
