import CryptoKit
import Foundation

struct ProjectionManifest: Encodable {
    let schemaHash: String
    let swiftSha256: String
    let headerSha256: String
}

enum DriverFailure: Error, CustomStringConvertible {
    case usage
    case unexpectedTarget(String)
    case buildFailed(Int32)
    case missingExpectedHash
    case missingSchemaHash
    case schemaHashMismatch(expected: String, actual: String)
    case projectionFailed(Int32)

    var description: String {
        switch self {
        case .usage:
            return "Usage: NetLiftProjectionDriver <target> <projection.csproj> <repository-root> <contract.g.cs> <artifacts-directory> <output-directory>"
        case let .unexpectedTarget(target):
            return "Unsupported SwiftPM target: \(target)"
        case let .buildFailed(status):
            return "Building NetLift.Projection exited with status \(status)."
        case .missingExpectedHash:
            return "Fixture does not declare a 64-character SchemaHash."
        case .missingSchemaHash:
            return "Generated netlift.h does not declare LIFT_SCHEMA_HASH_HEX."
        case let .schemaHashMismatch(expected, actual):
            return "Generated header schema hash \(actual) did not match fixture hash \(expected)."
        case let .projectionFailed(status):
            return "NetLift.Projection exited with status \(status)."
        }
    }
}

func sha256(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

func fixtureSchemaHash(_ source: String) throws -> String {
    let pattern = #"SchemaHash\s*=\s*"([0-9a-f]{64})""#
    guard let expression = try? NSRegularExpression(pattern: pattern),
          let match = expression.firstMatch(
            in: source,
            range: NSRange(source.startIndex..., in: source)),
          let range = Range(match.range(at: 1), in: source) else {
        throw DriverFailure.missingExpectedHash
    }
    return String(source[range])
}

func headerSchemaHash(_ header: String) throws -> String {
    let pattern = #"#define LIFT_SCHEMA_HASH_HEX "([0-9a-f]{64})""#
    guard let expression = try? NSRegularExpression(pattern: pattern),
          let match = expression.firstMatch(
            in: header,
            range: NSRange(header.startIndex..., in: header)),
          let range = Range(match.range(at: 1), in: header) else {
        throw DriverFailure.missingSchemaHash
    }
    return String(header[range])
}

func run() throws {
    guard CommandLine.arguments.count == 7 else {
        throw DriverFailure.usage
    }

    let target = CommandLine.arguments[1]
    let projectionProject = CommandLine.arguments[2]
    let repositoryRoot = CommandLine.arguments[3]
    let fixturePath = CommandLine.arguments[4]
    let artifactsDirectory = CommandLine.arguments[5]
    let outputDirectory = CommandLine.arguments[6]
    guard target == "ProjectionClient" || target == "ProjectionCABI" else {
        throw DriverFailure.unexpectedTarget(target)
    }
    let fixtureSource = try String(contentsOfFile: fixturePath, encoding: .utf8)
    let expectedHash = try fixtureSchemaHash(fixtureSource)

    try FileManager.default.createDirectory(
        atPath: artifactsDirectory,
        withIntermediateDirectories: true)
    try FileManager.default.createDirectory(
        atPath: outputDirectory,
        withIntermediateDirectories: true)

    let hostEnvironment = ProcessInfo.processInfo.environment
    var environment = [
        "PATH": hostEnvironment["PATH"] ?? "/usr/bin:/bin",
        "HOME": "\(artifactsDirectory)/.home",
        "TMPDIR": hostEnvironment["TMPDIR"] ?? NSTemporaryDirectory(),
        "DOTNET_CLI_HOME": "\(artifactsDirectory)/.dotnet-home",
        "NUGET_PACKAGES": "\(artifactsDirectory)/.nuget"
    ]
    if let dotnetRoot = hostEnvironment["DOTNET_ROOT"] {
        environment["DOTNET_ROOT"] = dotnetRoot
    }

    let build = Process()
    build.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    build.arguments = [
        "dotnet", "build", projectionProject,
        "-c", "Release",
        "--artifacts-path", artifactsDirectory,
        "--nologo"
    ]
    build.currentDirectoryURL = URL(fileURLWithPath: repositoryRoot)
    build.environment = environment
    build.standardOutput = FileHandle.standardOutput
    build.standardError = FileHandle.standardError
    print("PROJECTION-CLI-BUILD-RUN: \(projectionProject)")
    try build.run()
    build.waitUntilExit()
    guard build.terminationStatus == 0 else {
        throw DriverFailure.buildFailed(build.terminationStatus)
    }

    let projectionDll = "\(artifactsDirectory)/bin/NetLift.Projection/release/NetLift.Projection.dll"
    let project = Process()
    project.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    project.arguments = ["dotnet", projectionDll, fixturePath, outputDirectory]
    project.environment = environment
    project.standardOutput = FileHandle.standardOutput
    project.standardError = FileHandle.standardError
    print("PROJECTION-COMMAND-RUN: \(projectionDll)")
    try project.run()
    project.waitUntilExit()
    guard project.terminationStatus == 0 else {
        throw DriverFailure.projectionFailed(project.terminationStatus)
    }

    let swiftUrl = URL(fileURLWithPath: outputDirectory).appendingPathComponent("NetLift.swift")
    let headerUrl = URL(fileURLWithPath: outputDirectory).appendingPathComponent("netlift.h")
    let swiftBytes = try Data(contentsOf: swiftUrl)
    let headerBytes = try Data(contentsOf: headerUrl)
    let actualHash = try headerSchemaHash(String(decoding: headerBytes, as: UTF8.self))
    guard actualHash == expectedHash else {
        throw DriverFailure.schemaHashMismatch(expected: expectedHash, actual: actualHash)
    }

    let manifest = ProjectionManifest(
        schemaHash: expectedHash,
        swiftSha256: sha256(swiftBytes),
        headerSha256: sha256(headerBytes))
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    let manifestBytes = try encoder.encode(manifest)
    try manifestBytes.write(
        to: URL(fileURLWithPath: outputDirectory).appendingPathComponent("projection-manifest.json"),
        options: .atomic)
    if target == "ProjectionCABI" {
        let hashBytes = stride(from: 0, to: actualHash.count, by: 2).map { offset in
            let start = actualHash.index(actualHash.startIndex, offsetBy: offset)
            let end = actualHash.index(start, offsetBy: 2)
            return "0x\(actualHash[start..<end])"
        }
        let cSource = """
        #include <string.h>
        #include "NetLiftProjectionSupport.h"
        #include "netlift.h"

        static const uint8_t expected_schema_hash[32] = {
            \(hashBytes.joined(separator: ", "))
        };

        const char *projection_fixture_schema_hash(void)
        {
            return LIFT_SCHEMA_HASH_HEX;
        }

        void projection_fixture_make_config(lift_config *config)
        {
            if (config == NULL)
                return;

            memset(config, 0, sizeof(*config));
            config->struct_size = (uint32_t)sizeof(*config);
            config->abi_version = LIFT_ABI_VERSION;
            memcpy(config->schema_hash, expected_schema_hash, sizeof(expected_schema_hash));
        }

        uint32_t projection_fixture_validate_config(const lift_config *config)
        {
            if (config == NULL)
                return LIFT_STATUS_INVALID_ARGUMENT;
            if (config->struct_size != sizeof(*config) || config->abi_version != LIFT_ABI_VERSION)
                return LIFT_STATUS_VERSION_MISMATCH;
            if (memcmp(config->schema_hash, expected_schema_hash, sizeof(expected_schema_hash)) != 0)
                return LIFT_STATUS_SCHEMA_MISMATCH;
            return LIFT_STATUS_OK;
        }
        """
        try cSource.write(
            to: URL(fileURLWithPath: outputDirectory).appendingPathComponent("abi.c"),
            atomically: true,
            encoding: .utf8)
    }
    print("PROJECTION-MANIFEST: schema sha256=\(expectedHash)")
}

do {
    try run()
} catch {
    fputs("NetLift projection plugin driver failed: \(error)\n", stderr)
    exit(1)
}
