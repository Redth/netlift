import Foundation

private enum SchemaV5TestError: Error {
    case assertion(String)
}

func schemaV5Peer(_ path: String) throws -> NetLiftV5PeerSchema {
    let lines = try String(contentsOfFile: path, encoding: .utf8)
        .split(whereSeparator: \.isNewline)
    var hash: String?
    var fields = [NetLiftV5PeerField]()
    for line in lines {
        let parts = line.split(separator: "=", maxSplits: 1)
        guard parts.count == 2 else {
            throw SchemaV5TestError.assertion("Malformed peer descriptor line.")
        }
        if parts[0] == "schemaHash" {
            hash = String(parts[1])
        } else if parts[0] == "field" {
            let values = parts[1].split(separator: "|")
            guard values.count == 4,
                  let id = UInt16(values[0]),
                  values[2] == "0" || values[2] == "1",
                  values[3] == "0" || values[3] == "1" else {
                throw SchemaV5TestError.assertion("Malformed peer field descriptor.")
            }
            let kind: NetLiftV5FieldKind
            switch values[1] {
            case "utf8": kind = .utf8
            case "i32": kind = .i32
            case "bool": kind = .bool
            default: throw SchemaV5TestError.assertion("Unknown peer field kind.")
            }
            fields.append(NetLiftV5PeerField(
                testingOnly: (),
                id: id,
                kind: kind,
                nullable: values[2] == "1",
                optional: values[3] == "1"))
        } else {
            throw SchemaV5TestError.assertion("Unknown peer descriptor key.")
        }
    }
    guard let hash else {
        throw SchemaV5TestError.assertion("Peer descriptor does not contain its schema hash.")
    }
    return NetLiftV5PeerSchema.testingOnly(schemaHash: hash, fields: fields)
}

func schemaV5Bytes(_ name: String, in directory: String) throws -> [UInt8] {
    Array(try Data(contentsOf: URL(fileURLWithPath: directory).appendingPathComponent(name)))
}

func expectSchemaV5Failure(
    _ expected: NetLiftV5CodecError,
    _ action: () throws -> Void
) throws {
    do {
        try action()
    } catch let error as NetLiftV5CodecError {
        guard error == expected else {
            throw SchemaV5TestError.assertion("Expected \(expected), received \(error).")
        }
        return
    }
    throw SchemaV5TestError.assertion("Expected schema-v5 codec error \(expected).")
}

func schemaV5AppendField(
    _ source: [UInt8],
    id: UInt16,
    kind: UInt8,
    payload: [UInt8]
) -> [UInt8] {
    var result = source
    result.append(UInt8(truncatingIfNeeded: id))
    result.append(UInt8(truncatingIfNeeded: id >> 8))
    result.append(kind)
    schemaV5AppendU32(UInt32(payload.count), to: &result)
    result.append(contentsOf: payload)
    let fieldCount = UInt16(result[6]) | (UInt16(result[7]) << 8)
    schemaV5SetU16(fieldCount &+ 1, in: &result, at: 6)
    schemaV5SetU32(UInt32(result.count - 12), in: &result, at: 8)
    return result
}

func schemaV5ReplaceField(
    _ source: [UInt8],
    id: UInt16,
    kind: UInt8,
    payload: [UInt8]
) throws -> [UInt8] {
    guard source.count >= 12 else { throw NetLiftV5CodecError.malformed }
    var result = Array(source[0..<12])
    var offset = 12
    while offset < source.count {
        guard source.count - offset >= 7 else { throw NetLiftV5CodecError.malformed }
        let currentId = UInt16(source[offset]) | (UInt16(source[offset + 1]) << 8)
        let length = Int(schemaV5ReadU32(source, offset + 3))
        guard length <= source.count - offset - 7 else { throw NetLiftV5CodecError.malformed }
        if currentId == id {
            result.append(source[offset])
            result.append(source[offset + 1])
            result.append(kind)
            schemaV5AppendU32(UInt32(payload.count), to: &result)
            result.append(contentsOf: payload)
        } else {
            result.append(contentsOf: source[offset..<(offset + 7 + length)])
        }
        offset += 7 + length
    }
    schemaV5SetU32(UInt32(result.count - 12), in: &result, at: 8)
    return result
}

private func schemaV5AppendU32(_ value: UInt32, to bytes: inout [UInt8]) {
    for shift in 0..<4 {
        bytes.append(UInt8(truncatingIfNeeded: value >> UInt32(shift * 8)))
    }
}

private func schemaV5SetU16(_ value: UInt16, in bytes: inout [UInt8], at offset: Int) {
    bytes[offset] = UInt8(truncatingIfNeeded: value)
    bytes[offset + 1] = UInt8(truncatingIfNeeded: value >> 8)
}

private func schemaV5SetU32(_ value: UInt32, in bytes: inout [UInt8], at offset: Int) {
    for shift in 0..<4 {
        bytes[offset + shift] = UInt8(truncatingIfNeeded: value >> UInt32(shift * 8))
    }
}

private func schemaV5ReadU32(_ bytes: [UInt8], _ offset: Int) -> UInt32 {
    (0..<4).reduce(UInt32(0)) {
        $0 | (UInt32(bytes[offset + $1]) << UInt32($1 * 8))
    }
}
