import Foundation

private enum SmokeFailure: Error {
    case assertion(String)
}

@main
private struct SwiftFlatCodecSmoke {
    static func main() throws {
        guard CommandLine.arguments.count == 2 else {
            throw SmokeFailure.assertion("Expected the generated fixture directory.")
        }
        let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)

        let command = DispatchCommand(packageCount: 3, destination: 2)
        let commandBytes = try NetLiftDtoCodec.encodeDispatchCommand(command)
        let expectedCommand = try Data(contentsOf: directory.appendingPathComponent("DispatchCommand.csharp.bin"))
        precondition(Data(commandBytes) == expectedCommand, "Swift DispatchCommand bytes differ from generated C#.")
        let decodedCommand = try NetLiftDtoCodec.decodeDispatchCommand(Array(expectedCommand))
        precondition(decodedCommand.packageCount == 3 && decodedCommand.destination == 2)

        let receipt = DispatchReceipt(
            receiptId: 1,
            packageCount: 3,
            destination: 2,
            trackingCode: "DSP-CENTRAL-000001")
        let receiptBytes = try NetLiftDtoCodec.encodeDispatchReceipt(receipt)
        let expectedReceipt = try Data(contentsOf: directory.appendingPathComponent("DispatchReceipt.csharp.bin"))
        precondition(expectedReceipt.count == 58, "C# DispatchReceipt fixture should be 58 bytes.")
        precondition(Data(receiptBytes) == expectedReceipt, "Swift DispatchReceipt bytes differ from generated C#.")
        let decodedReceipt = try NetLiftDtoCodec.decodeDispatchReceipt(Array(expectedReceipt))
        precondition(
            decodedReceipt.receiptId == 1 &&
                decodedReceipt.packageCount == 3 &&
                decodedReceipt.destination == 2 &&
                decodedReceipt.trackingCode == "DSP-CENTRAL-000001")

        let note = DispatchNote(message: "Dispatch 🚚")
        let noteBytes = try NetLiftDtoCodec.encodeDispatchNote(note)
        let expectedNote = try Data(contentsOf: directory.appendingPathComponent("DispatchNote.csharp.bin"))
        precondition(Data(noteBytes) == expectedNote, "Swift Unicode string bytes differ from generated C#.")
        let decodedNote = try NetLiftDtoCodec.decodeDispatchNote(Array(expectedNote))
        precondition(decodedNote.message == "Dispatch 🚚")

        let values = FixedWidthValues(
            flag: true,
            unsignedByte: 250,
            signedByte: -12,
            signedShort: -1234,
            unsignedShort: 54321,
            signedInt: -1234567,
            unsignedInt: 3456789012,
            signedLong: -9000000000000,
            unsignedLong: 18000000000000,
            float32: 1.5,
            float64: -2.25)
        let expectedValues = try Data(contentsOf: directory.appendingPathComponent("FixedWidthValues.csharp.bin"))
        let encodedValues = try NetLiftDtoCodec.encodeFixedWidthValues(values)
        precondition(Data(encodedValues) == expectedValues, "Swift fixed-width bytes differ from generated C#.")
        let decodedValues = try NetLiftDtoCodec.decodeFixedWidthValues(Array(expectedValues))
        precondition(
            decodedValues.flag &&
                decodedValues.unsignedByte == 250 &&
                decodedValues.signedByte == -12 &&
                decodedValues.signedShort == -1234 &&
                decodedValues.unsignedShort == 54321 &&
                decodedValues.signedInt == -1234567 &&
                decodedValues.unsignedInt == 3456789012 &&
                decodedValues.signedLong == -9000000000000 &&
                decodedValues.unsignedLong == 18000000000000 &&
                decodedValues.float32 == 1.5 &&
                decodedValues.float64 == -2.25)

        try expect(.malformed) {
            _ = try NetLiftDtoCodec.decodeDispatchCommand([1, 0])
        }
        try expect(.malformed) {
            _ = try NetLiftDtoCodec.decodeDispatchCommand(field(2, littleEndian(Int32(2))) +
                field(1, littleEndian(Int32(3))))
        }
        try expect(.malformed) {
            _ = try NetLiftDtoCodec.decodeDispatchCommand(field(1, [3, 0]) +
                field(2, littleEndian(Int32(2))))
        }
        try expect(.malformed) {
            _ = try NetLiftDtoCodec.decodeDispatchCommand(field(1, littleEndian(Int32(3))))
        }
        try expect(.malformed) {
            _ = try NetLiftDtoCodec.decodeDispatchCommand(commandBytes + [0])
        }
        try expect(.malformed) {
            _ = try NetLiftDtoCodec.decodeDispatchNote(field(1, [0xff]))
        }
        try expect(.malformed) {
            _ = try NetLiftDtoCodec.decodeDispatchNote([1, 0, 1, 0x10, 0, 0])
        }

        var invalidBoolean = Array(expectedValues)
        invalidBoolean[6] = 2
        try expect(.malformed) {
            _ = try NetLiftDtoCodec.decodeFixedWidthValues(invalidBoolean)
        }
        try expect(.bufferTooLarge) {
            _ = try NetLiftDtoCodec.decodeDispatchCommand([UInt8](repeating: 0, count: 4097))
        }
        try expect(.bufferTooLarge) {
            _ = try NetLiftDtoCodec.encodeDispatchNote(DispatchNote(message: String(repeating: "x", count: 4096)))
        }

        print("PASS Swift public DTO codecs match generated C# bytes, round-trip flat DTOs, and reject malformed/oversized wire.")
    }

    private static func expect(
        _ expected: NetLiftCodecError,
        _ action: () throws -> Void
    ) throws {
        do {
            try action()
        } catch let error as NetLiftCodecError {
            precondition(error == expected, "Expected \(expected), got \(error).")
            return
        }
        throw SmokeFailure.assertion("Expected codec error \(expected).")
    }

    private static func field(_ id: UInt16, _ payload: [UInt8]) -> [UInt8] {
        var bytes = [UInt8]()
        bytes.append(UInt8(truncatingIfNeeded: id))
        bytes.append(UInt8(truncatingIfNeeded: id >> 8))
        let length = UInt32(payload.count)
        for shift in 0..<4 {
            bytes.append(UInt8(truncatingIfNeeded: length >> UInt32(shift * 8)))
        }
        bytes.append(contentsOf: payload)
        return bytes
    }

    private static func littleEndian<T: FixedWidthInteger>(_ value: T) -> [UInt8] {
        var value = value.littleEndian
        return withUnsafeBytes(of: &value) { Array($0) }
    }
}
