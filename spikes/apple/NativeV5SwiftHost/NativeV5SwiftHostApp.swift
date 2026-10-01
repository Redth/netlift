import SwiftUI
import OSLog
import NetLiftV5Host
import NetLiftV5OldCodec
import NetLiftV5NewCodec

@main
struct NativeV5SwiftHostApp: App {
    @StateObject private var proof = NativeV5Proof()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            NativeV5ProofView(proof: proof)
                .onAppear {
                    proof.initialize()
                    proof.runBaselineChecks()
                }
                .onDisappear { proof.closePending(reason: "scene view disappeared") }
                .onChange(of: scenePhase) { phase in
                    if phase == .background {
                        proof.closePending(reason: "scene entered background")
                    }
                }
        }
    }
}

@MainActor
private final class NativeV5Proof: ObservableObject {
    private let logger = Logger(subsystem: "org.netlift.nativev5swifthost", category: "proof")
    @Published private(set) var lines = ["Waiting for CoreCLR host initialization."]
    @Published private(set) var isReady = false

    private var host: NetLiftV5Host?
    private var pendingEndpoint: NetLiftV5Endpoint?
    private var pendingContext: NetLiftV5PeerContext?
    private var pendingBaselineCounters: NetLiftV5Counters?
    private var hasRunBaseline = false

    func initialize() {
        guard host == nil else { return }
#if NETLIFT_V5_ASYNC_EXECUTION
        do {
            try Data().write(to: asyncExecutionProofURL(), options: .atomic)
        } catch {
            logger.error("Could not initialize simulator proof artifact: \(error.localizedDescription, privacy: .public)")
        }
#endif
        do {
            host = try NetLiftV5Host()
            isReady = true
            log("HOST-INIT PASS: Xcode-owned SwiftUI app loaded .NET 11 CoreCLR.")
        } catch {
            log("HOST-INIT FAIL: \(error.localizedDescription)")
        }
    }

#if !NETLIFT_V5_ASYNC_RESULTS && !NETLIFT_V5_ASYNC_EXECUTION
    func runBothDirections() {
        guard let host else {
            log("VALID-MATRIX BLOCKED: CoreCLR host is not initialized.")
            return
        }
        runValidFlow(host, flow: .oldLocalWithNewPeer)
        runValidFlow(host, flow: .newLocalWithOldPeer)
    }
#endif

    func runBaselineChecks() {
        guard isReady, !hasRunBaseline else { return }
        hasRunBaseline = true
#if NETLIFT_V5_ASYNC_EXECUTION
#if NETLIFT_V5_ASYNC_NEW
        runAsyncExecutionFlow(host, flow: .newLocalWithOldPeer)
#else
        runAsyncExecutionFlow(host, flow: .oldLocalWithNewPeer)
#endif
#elseif NETLIFT_V5_ASYNC_RESULTS
        runAsyncResultMatrix()
#else
        runBothDirections()
        verifyWrongManagedPin()
        verifyWrongCodecContext()
        armSceneClose()
#endif
    }

#if NETLIFT_V5_ASYNC_RESULTS
    private func runAsyncResultMatrix() {
        guard let host else {
            log("ASYNC-RESULT BLOCKED: CoreCLR host is not initialized.")
            return
        }
        for flow in [NetLiftV5Flow.oldLocalWithNewPeer, .newLocalWithOldPeer] {
            runAsyncResultFlow(host, flow: flow)
        }
    }

    private func runAsyncResultFlow(_ host: NetLiftV5Host, flow: NetLiftV5Flow) {
        do {
            let peer: NetLiftV5PeerDescriptor
            let localHash: String
            switch flow {
            case .oldLocalWithNewPeer:
                peer = try newPeerDescriptor()
                localHash = NetLiftV5BuildPins.oldManagedSchemaHash
            case .newLocalWithOldPeer:
                peer = try oldPeerDescriptor()
                localHash = NetLiftV5BuildPins.newManagedSchemaHash
            }
            let endpoint = try host.makeEndpoint(for: flow, peer: peer)
            defer { try? endpoint.close() }
            try endpoint.start(expectedManagedSchemaHash: localHash)
            let peerContext = try endpoint.peerContext()
            let resultContext = try endpoint.managedResultSchemaContext()
            guard resultContext.localSchemaHash == localHash,
                  resultContext.peerSchemaHash == peerContext.schemaHash,
                  resultContext.serviceID == peerContext.serviceID,
                  resultContext.methodID == peerContext.methodID,
                  resultContext.profileId == 1,
                  resultContext.resultDTOID == 22,
                  resultContext.resultDTOVersion == 1 else {
                throw ProofFailure.resultProvenanceMismatch(flow)
            }
            try endpoint.verifyWrongResultProfileAndRouteRejected()
            let payload: [UInt8]
            switch flow {
            case .oldLocalWithNewPeer:
                let vector = asyncResultPayload(code: 201, accepted: true)
                let decoded = try NetLiftV5NewCodec.NetLiftV5AsyncCodec.decodeResult(
                    vector,
                    context: resultContext)
                guard decoded.code == 201, decoded.accepted, !decoded.audited else {
                    throw ProofFailure.resultVectorMismatch(flow)
                }
                do {
                    _ = try NetLiftV5OldCodec.NetLiftV5AsyncCodec.decodeResult(
                        vector,
                        context: resultContext)
                    throw ProofFailure.wrongResultPeerAccepted(flow)
                } catch NetLiftV5OldCodec.NetLiftV5AsyncCodecError.invalidPeer {}
                payload = vector
            case .newLocalWithOldPeer:
                let vector = asyncResultPayload(code: 202, accepted: true, audited: true)
                let decoded = try NetLiftV5OldCodec.NetLiftV5AsyncCodec.decodeResult(
                    vector,
                    context: resultContext)
                guard decoded.code == 202, decoded.accepted else {
                    throw ProofFailure.resultVectorMismatch(flow)
                }
                do {
                    _ = try NetLiftV5NewCodec.NetLiftV5AsyncCodec.decodeResult(
                        vector,
                        context: resultContext)
                    throw ProofFailure.wrongResultPeerAccepted(flow)
                } catch NetLiftV5NewCodec.NetLiftV5AsyncCodecError.invalidPeer {}
                payload = vector
            }

            try endpoint.close()
            do {
                try decodeAsyncResult(payload, flow: flow, context: resultContext)
                throw ProofFailure.staleResultContextAccepted(flow)
            } catch NetLiftV5HostError.endpointClosed {
                log("ASYNC-RESULT PASS \(flow.label): live NLMR local/peer hashes and route matched; generated test-vector decode/defaults passed; wrong peer/profile/route and post-close context rejected.")
            }
        } catch {
            log("ASYNC-RESULT FAIL \(flow.label): \(error.localizedDescription)")
        }
    }

    private func decodeAsyncResult(
        _ payload: [UInt8],
        flow: NetLiftV5Flow,
        context: ManagedResultSchemaContext
    ) throws {
        switch flow {
        case .oldLocalWithNewPeer:
            _ = try NetLiftV5NewCodec.NetLiftV5AsyncCodec.decodeResult(payload, context: context)
        case .newLocalWithOldPeer:
            _ = try NetLiftV5OldCodec.NetLiftV5AsyncCodec.decodeResult(payload, context: context)
        }
    }

    private func asyncResultPayload(code: Int32, accepted: Bool, audited: Bool? = nil) -> [UInt8] {
        var body = [UInt8]()
        appendResultField(1, kind: 2, value: withUnsafeBytes(of: code.littleEndian, Array.init), to: &body)
        appendResultField(2, kind: 3, value: [accepted ? 1 : 0], to: &body)
        if let audited {
            appendResultField(9, kind: 3, value: [audited ? 1 : 0], to: &body)
        }
        var bytes = Array("NLF1".utf8)
        appendResultU16(1, to: &bytes)
        appendResultU16(audited == nil ? 2 : 3, to: &bytes)
        appendResultU32(UInt32(body.count), to: &bytes)
        bytes.append(contentsOf: body)
        return bytes
    }

    private func appendResultField(_ id: UInt16, kind: UInt8, value: [UInt8], to bytes: inout [UInt8]) {
        appendResultU16(id, to: &bytes)
        bytes.append(kind)
        appendResultU32(UInt32(value.count), to: &bytes)
        bytes.append(contentsOf: value)
    }

    private func appendResultU16(_ value: UInt16, to bytes: inout [UInt8]) {
        bytes.append(UInt8(truncatingIfNeeded: value))
        bytes.append(UInt8(truncatingIfNeeded: value >> 8))
    }

    private func appendResultU32(_ value: UInt32, to bytes: inout [UInt8]) {
        for shift in stride(from: 0, to: 32, by: 8) {
            bytes.append(UInt8(truncatingIfNeeded: value >> UInt32(shift)))
        }
    }
#endif

#if NETLIFT_V5_ASYNC_EXECUTION
    private func runAsyncExecutionFlow(_ host: NetLiftV5Host?, flow: NetLiftV5Flow) {
        guard let host else {
            log("ASYNC-EXEC BLOCKED \(flow.label): CoreCLR host is not initialized.")
            return
        }
        let peer: NetLiftV5PeerDescriptor?
        let localHash: String
        switch flow {
        case .oldLocalWithNewPeer:
            peer = try? newPeerDescriptor()
            localHash = NetLiftV5BuildPins.oldManagedSchemaHash
        case .newLocalWithOldPeer:
            peer = try? oldPeerDescriptor()
            localHash = NetLiftV5BuildPins.newManagedSchemaHash
        }
        guard let peer else {
            log("ASYNC-EXEC FAIL \(flow.label): generated peer descriptor is unavailable.")
            return
        }

        var endpoint: NetLiftV5Endpoint?
        do {
            let activeEndpoint = try host.makeEndpoint(for: flow, peer: peer)
            endpoint = activeEndpoint
            try activeEndpoint.start(expectedManagedSchemaHash: localHash)
            let peerContext = try activeEndpoint.peerContext()
            let resultContext = try activeEndpoint.managedResultSchemaContext()
            guard peerContext.localSchemaHash == localHash,
                  peerContext.schemaHash == peer.schemaHash,
                  peerContext.serviceID == 17,
                  peerContext.methodID == 1,
                  peerContext.dtoID == 21,
                  peerContext.requiredFeatureBits == 0x36,
                  resultContext.localSchemaHash == localHash,
                  resultContext.peerSchemaHash == peerContext.schemaHash,
                  resultContext.serviceID == peerContext.serviceID,
                  resultContext.methodID == peerContext.methodID,
                  resultContext.profileId == 1,
                  resultContext.resultDTOID == 22,
                  resultContext.resultDTOVersion == 1 else {
                throw ProofFailure.asyncRequestPreflightMismatch(flow)
            }
            try activeEndpoint.verifyWrongResultProfileAndRouteRejected()
            let route = try activeEndpoint.bindAsyncCallbackV2()
            guard route.serviceID == 17, route.methodID == 1 else {
                throw ProofFailure.asyncRequestPreflightMismatch(flow)
            }

            let accepted = try runAsyncRequest(
                activeEndpoint,
                flow: flow,
                context: peerContext,
                resultContext: resultContext,
                name: "approve",
                count: flow == .oldLocalWithNewPeer ? 61 : 71,
                mode: .typedReply,
                expectedAccepted: true)
            _ = try runAsyncRequest(
                activeEndpoint,
                flow: flow,
                context: peerContext,
                resultContext: resultContext,
                name: "decline",
                count: flow == .oldLocalWithNewPeer ? 62 : 72,
                mode: .typedReply,
                expectedAccepted: false)
            _ = try runAsyncRequest(
                activeEndpoint,
                flow: flow,
                context: peerContext,
                resultContext: resultContext,
                name: "approve",
                count: flow == .oldLocalWithNewPeer ? 63 : 73,
                mode: .cancelRequest,
                expectedAccepted: false)
            _ = try runAsyncRequest(
                activeEndpoint,
                flow: flow,
                context: peerContext,
                resultContext: resultContext,
                name: "approve",
                count: flow == .oldLocalWithNewPeer ? 64 : 74,
                mode: .cancelOperation,
                expectedAccepted: false)

            let drain = try activeEndpoint.drainAsyncCallbackV2()
            guard drain.releaseDelta == 1,
                  drain.outstandingLeases == 0,
                  drain.resultDescriptorLeases == 0 else {
                throw ProofFailure.asyncDrainMismatch(flow)
            }
            try activeEndpoint.close()
            do {
                try decodeAsyncResult(accepted.resultBytes, flow: flow, context: resultContext)
                throw ProofFailure.staleResultContextAccepted(flow)
            } catch NetLiftV5HostError.endpointClosed {
                log("ASYNC-EXEC PASS \(flow.label): real generated Task<SubmitResult> and managed-origin ConfirmAsync; one Confirmation request per SubmitAsync, typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.")
            }
        } catch {
            log("ASYNC-EXEC FAIL \(flow.label): \(error.localizedDescription)")
            if let endpoint {
                if endpoint.isAsyncRuntimeBound {
                    do {
                        let drain = try endpoint.drainAsyncCallbackV2()
                        log("ASYNC-EXEC CLEANUP \(flow.label): revoke/release completed; leases=\(drain.outstandingLeases), NLMR leases=\(drain.resultDescriptorLeases).")
                    } catch {
                        log("ASYNC-EXEC CLEANUP FAIL \(flow.label): \(error.localizedDescription)")
                    }
                }
                do {
                    try endpoint.close()
                } catch {
                    log("ASYNC-EXEC CLOSE FAIL \(flow.label): \(error.localizedDescription)")
                }
            }
        }
    }

    private func runAsyncRequest(
        _ endpoint: NetLiftV5Endpoint,
        flow: NetLiftV5Flow,
        context: NetLiftV5PeerContext,
        resultContext: ManagedResultSchemaContext,
        name: String,
        count: Int32,
        mode: NetLiftV5AsyncOperationMode,
        expectedAccepted: Bool
    ) throws -> NetLiftV5AsyncOperationResult {
        let payload = try encodeAsyncRequest(flow, context: context, name: name, count: count)
        let operation = try endpoint.runAsyncCallbackV2Operation(
            payload: payload,
            mode: mode,
            accepted: expectedAccepted)
        guard operation.requestCount == 1, operation.resultCount == 1 else {
            throw ProofFailure.asyncOperationMismatch(flow, "callback cardinality")
        }
        if mode == .typedReply {
            guard operation.terminalStatus == 0 else {
                throw ProofFailure.asyncOperationMismatch(flow, "typed reply terminal status")
            }
            switch flow {
            case .oldLocalWithNewPeer:
                let decoded = try NetLiftV5NewCodec.NetLiftV5AsyncCodec.decodeResult(
                    operation.resultBytes,
                    context: resultContext)
                guard decoded.code == count,
                      decoded.accepted == expectedAccepted,
                      decoded.audited == false else {
                    throw ProofFailure.asyncOperationMismatch(flow, "new generated result decode/default")
                }
            case .newLocalWithOldPeer:
                let decoded = try NetLiftV5OldCodec.NetLiftV5AsyncCodec.decodeResult(
                    operation.resultBytes,
                    context: resultContext)
                guard decoded.code == count, decoded.accepted == expectedAccepted else {
                    throw ProofFailure.asyncOperationMismatch(flow, "old generated result decode/field skip")
                }
            }
        } else if operation.terminalStatus != 12 || !operation.resultBytes.isEmpty {
            throw ProofFailure.asyncOperationMismatch(flow, "cancellation terminal status")
        }
        return operation
    }

    private func encodeAsyncRequest(
        _ flow: NetLiftV5Flow,
        context: NetLiftV5PeerContext,
        name: String,
        count: Int32
    ) throws -> [UInt8] {
        switch flow {
        case .oldLocalWithNewPeer:
            try NetLiftV5NewCodec.NetLiftV5AsyncCodec.encodeRequest(
                NetLiftV5NewCodec.SubmitInput(name: name, count: count, note: "ios-new-peer"),
                context: context)
        case .newLocalWithOldPeer:
            try NetLiftV5OldCodec.NetLiftV5AsyncCodec.encodeRequest(
                NetLiftV5OldCodec.SubmitInput(name: name, count: count),
                context: context)
        }
    }
#endif

#if !NETLIFT_V5_ASYNC_RESULTS && !NETLIFT_V5_ASYNC_EXECUTION
    func verifyWrongManagedPin() {
        guard let host else { return log("WRONG-PIN BLOCKED: CoreCLR host is not initialized.") }
        do {
            let peer = try newPeerDescriptor()
            let endpoint = try host.makeEndpoint(for: .oldLocalWithNewPeer, peer: peer)
            defer { closeAndLog(endpoint) }
            let baseline = try endpoint.counters()
            let wrongPin = altered(NetLiftV5BuildPins.oldManagedSchemaHash)
            do {
                try endpoint.start(expectedManagedSchemaHash: wrongPin)
                log("WRONG-PIN FAIL: start unexpectedly accepted an incorrect managed image pin.")
            } catch NetLiftV5HostError.localManifestMismatch(_, _) {
                let counters = try endpoint.counters()
                guard counters == baseline else {
                    throw ProofFailure.counterDeltaMismatch("wrong pin", baseline, counters)
                }
                log("WRONG-PIN PASS: GetManifest rejected the pin before compatible-start; delta factory=0 route=0.")
            }
        } catch {
            log("WRONG-PIN FAIL: \(error.localizedDescription)")
        }
    }

    func verifyWrongCodecContext() {
        guard let host else { return log("WRONG-CONTEXT BLOCKED: CoreCLR host is not initialized.") }
        do {
            let endpoint = try host.makeEndpoint(
                for: .oldLocalWithNewPeer,
                peer: newPeerDescriptor())
            try endpoint.start(expectedManagedSchemaHash: NetLiftV5BuildPins.oldManagedSchemaHash)
            let context = try endpoint.peerContext()
            let baseline = try endpoint.counters()
            do {
                _ = try NetLiftV5OldCodec.NetLiftV5DtoCodec.encodePayload(
                    NetLiftV5OldCodec.Payload(sku: "wrong-peer", quantity: 13),
                    context: context)
                throw ProofFailure.unexpectedAcceptance("old codec accepted the new peer context")
            } catch NetLiftV5OldCodec.NetLiftV5CodecError.invalidPeer {
                let counters = try endpoint.counters()
                guard counters == baseline else {
                    throw ProofFailure.counterDeltaMismatch("wrong codec context", baseline, counters)
                }
                log("WRONG-CONTEXT PASS: old codec rejected the negotiated new-peer descriptor before dispatch; delta factory=0 route=0.")
            }
            try endpoint.close()
        } catch {
            log("WRONG-CONTEXT FAIL: \(error.localizedDescription)")
        }
    }

    func armSceneClose() {
        guard let host else { return log("SCENE-CLOSE BLOCKED: CoreCLR host is not initialized.") }
        closePending(reason: "replaced by new scene-close probe")
        do {
            let endpoint = try host.makeEndpoint(
                for: .oldLocalWithNewPeer,
                peer: newPeerDescriptor())
            try endpoint.start(expectedManagedSchemaHash: NetLiftV5BuildPins.oldManagedSchemaHash)
            let context = try endpoint.peerContext()
            let baseline = try endpoint.counters()
            pendingEndpoint = endpoint
            pendingContext = context
            pendingBaselineCounters = baseline
            log("SCENE-CLOSE ARMED: compatible-start succeeded; no DTO dispatch or UI callback is pending.")
        } catch {
            log("SCENE-CLOSE FAIL: \(error.localizedDescription)")
        }
    }

    func closePending(reason: String) {
        guard let endpoint = pendingEndpoint else { return }
        pendingEndpoint = nil
        do {
            try endpoint.close()
            log("SCENE-CLOSE PASS: \(reason); endpoint closed before any route dispatch.")
            let counters = try endpoint.counters()
            guard counters == pendingBaselineCounters else {
                throw ProofFailure.counterDeltaMismatch("scene close", pendingBaselineCounters, counters)
            }
            if let context = pendingContext {
                do {
                    _ = try NetLiftV5NewCodec.NetLiftV5DtoCodec.encodePayload(
                        NetLiftV5NewCodec.Payload(
                            sku: "late-ui-callback",
                            quantity: 17,
                            note: "",
                            nullableNote: nil,
                            giftWrap: false),
                        context: context)
                    throw ProofFailure.unexpectedAcceptance("encoder accepted a context after scene close")
                } catch NetLiftV5HostError.endpointClosed {
                    log("STALE-CONTEXT PASS: post-close encoder rejected before dispatch; delta factory=0 route=0.")
                }
            }
        } catch {
            log("SCENE-CLOSE FAIL: \(error.localizedDescription)")
        }
        pendingContext = nil
        pendingBaselineCounters = nil
    }
#else
    func closePending(reason: String) {}
#endif

#if !NETLIFT_V5_ASYNC_RESULTS && !NETLIFT_V5_ASYNC_EXECUTION
    private func runValidFlow(_ host: NetLiftV5Host, flow: NetLiftV5Flow) {
        do {
            let peer: NetLiftV5PeerDescriptor
            let expectedLocalHash: String
            switch flow {
            case .oldLocalWithNewPeer:
                peer = try newPeerDescriptor()
                expectedLocalHash = NetLiftV5BuildPins.oldManagedSchemaHash
            case .newLocalWithOldPeer:
                peer = try oldPeerDescriptor()
                expectedLocalHash = NetLiftV5BuildPins.newManagedSchemaHash
            }

            let endpoint = try host.makeEndpoint(for: flow, peer: peer)
            try endpoint.start(expectedManagedSchemaHash: expectedLocalHash)
            let context = try endpoint.peerContext()
            guard context.localSchemaHash == expectedLocalHash,
                  context.schemaHash == peer.schemaHash else {
                throw ProofFailure.provenanceMismatch(
                    local: context.localSchemaHash,
                    peer: context.schemaHash)
            }

            let quantity: Int32 = flow == .oldLocalWithNewPeer ? 41 : 42
            let payload: [UInt8]
            switch flow {
            case .oldLocalWithNewPeer:
                payload = try NetLiftV5NewCodec.NetLiftV5DtoCodec.encodePayload(
                    NetLiftV5NewCodec.Payload(
                        sku: "native-new-managed-old",
                        quantity: quantity,
                        note: "",
                        nullableNote: nil,
                        giftWrap: false),
                    context: context)
            case .newLocalWithOldPeer:
                payload = try NetLiftV5OldCodec.NetLiftV5DtoCodec.encodePayload(
                    NetLiftV5OldCodec.Payload(sku: "native-old-managed-new", quantity: quantity),
                    context: context)
            }

            let baseline = try endpoint.counters()
            let routeValue = try endpoint.dispatch(payload, using: context)
            let counters = try endpoint.counters()
            let defaultsExpected: UInt32 = flow == .newLocalWithOldPeer ? 1 : 0
            guard routeValue == quantity,
                  counters.factoryCalls == baseline.factoryCalls + 1,
                  counters.routeCalls == baseline.routeCalls + 1,
                  counters.lastQuantity == quantity,
                  counters.optionalDefaultsValid == defaultsExpected else {
                throw ProofFailure.validRouteMismatch(flow, routeValue, counters)
            }
            log("VALID-FLOW PASS \(flow.label): managedPin=\(context.localSchemaHash) peerDescriptor=\(context.schemaHash) route=\(routeValue) deltaFactory=1 deltaRoute=1 defaults=\(counters.optionalDefaultsValid).")
            try endpoint.close()
        } catch {
            log("VALID-FLOW FAIL \(flow.label): \(error.localizedDescription)")
        }
    }
#endif

    private func newPeerDescriptor() throws -> NetLiftV5PeerDescriptor {
#if NETLIFT_V5_ASYNC_PROTOCOL
        try NetLiftV5PeerDescriptor(
            canonicalJson: NetLiftV5NewCodec.NetLiftV5AsyncCodec.canonicalJson,
            schemaHash: NetLiftV5NewCodec.NetLiftV5AsyncCodec.schemaHash)
#else
        try NetLiftV5PeerDescriptor(
            canonicalJson: NetLiftV5NewCodec.NetLiftV5DtoCodec.canonicalJson,
            schemaHash: NetLiftV5NewCodec.NetLiftV5DtoCodec.schemaHash)
#endif
    }

    private func oldPeerDescriptor() throws -> NetLiftV5PeerDescriptor {
#if NETLIFT_V5_ASYNC_PROTOCOL
        try NetLiftV5PeerDescriptor(
            canonicalJson: NetLiftV5OldCodec.NetLiftV5AsyncCodec.canonicalJson,
            schemaHash: NetLiftV5OldCodec.NetLiftV5AsyncCodec.schemaHash)
#else
        try NetLiftV5PeerDescriptor(
            canonicalJson: NetLiftV5OldCodec.NetLiftV5DtoCodec.canonicalJson,
            schemaHash: NetLiftV5OldCodec.NetLiftV5DtoCodec.schemaHash)
#endif
    }

#if !NETLIFT_V5_ASYNC_RESULTS && !NETLIFT_V5_ASYNC_EXECUTION
    private func closeAndLog(_ endpoint: NetLiftV5Endpoint) {
        do {
            try endpoint.close()
        } catch {
            log("ENDPOINT-CLOSE FAIL: \(error.localizedDescription)")
        }
    }

    private func altered(_ hash: String) -> String {
        String(hash.first == "0" ? "1" : "0") + hash.dropFirst()
    }
#endif

    private func log(_ message: String) {
        lines.append(message)
        logger.info("\(message, privacy: .public)")
#if NETLIFT_V5_ASYNC_EXECUTION
        do {
            let url = try asyncExecutionProofURL()
            var contents = FileManager.default.fileExists(atPath: url.path)
                ? try Data(contentsOf: url)
                : Data()
            contents.append(contentsOf: Data((message + "\n").utf8))
            try contents.write(to: url, options: .atomic)
        } catch {
            logger.error("Could not persist simulator proof line: \(error.localizedDescription, privacy: .public)")
        }
#endif
    }

#if NETLIFT_V5_ASYNC_EXECUTION
    private func asyncExecutionProofURL() throws -> URL {
        guard let documents = FileManager.default.urls(
            for: .documentDirectory,
            in: .userDomainMask).first else {
            throw CocoaError(.fileNoSuchFile)
        }
        return documents.appendingPathComponent("async-execution-proof.log")
    }
#endif
}

@MainActor
private struct NativeV5ProofView: View {
    @ObservedObject var proof: NativeV5Proof

    var body: some View {
        VStack(spacing: 12) {
            Text("NetLift V5 CoreCLR")
                .font(.title2.bold())
            Text(proof.isReady ? "CoreCLR ready" : "CoreCLR not ready")
                .accessibilityIdentifier("host-status")
#if NETLIFT_V5_ASYNC_EXECUTION
            Text("AsyncCallbackV1 V2 managed Task and ConfirmAsync execution.")
                .accessibilityIdentifier("async-execution-scope")
#elseif !NETLIFT_V5_ASYNC_RESULTS
            Button("Run old/new generated-codec matrix", action: proof.runBothDirections)
                .disabled(!proof.isReady)
            Button("Reject wrong managed image pin", action: proof.verifyWrongManagedPin)
                .disabled(!proof.isReady)
            Button("Reject wrong peer codec context", action: proof.verifyWrongCodecContext)
                .disabled(!proof.isReady)
            Button("Arm scene-background close", action: proof.armSceneClose)
                .disabled(!proof.isReady)
                .accessibilityIdentifier("arm-scene-close")
#else
            Text("AsyncCallbackV1 result decoding uses test vectors only.")
                .accessibilityIdentifier("async-result-scope")
#endif
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(Array(proof.lines.enumerated()), id: \.offset) { _, line in
                        Text(line)
                            .font(.system(.caption, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .accessibilityIdentifier("proof-log")
                    }
                }
                .padding(.vertical, 8)
            }
        }
        .padding()
    }
}

private enum ProofFailure: Error, LocalizedError {
    case unexpectedAcceptance(String)
    case counterDeltaMismatch(String, NetLiftV5Counters?, NetLiftV5Counters)
    case provenanceMismatch(local: String, peer: String)
    case validRouteMismatch(NetLiftV5Flow, Int32, NetLiftV5Counters)
    case resultProvenanceMismatch(NetLiftV5Flow)
    case resultVectorMismatch(NetLiftV5Flow)
    case asyncRequestPreflightMismatch(NetLiftV5Flow)
    case wrongResultPeerAccepted(NetLiftV5Flow)
    case staleResultContextAccepted(NetLiftV5Flow)
    case asyncOperationMismatch(NetLiftV5Flow, String)
    case asyncDrainMismatch(NetLiftV5Flow)

    var errorDescription: String? {
        switch self {
        case let .unexpectedAcceptance(message):
            return message
        case let .counterDeltaMismatch(operation, baseline, counters):
            return "\(operation) changed counters from \(String(describing: baseline)) to \(counters)."
        case let .provenanceMismatch(local, peer):
            return "Provenance mismatch: managed pin \(local), peer descriptor \(peer)."
        case let .validRouteMismatch(flow, route, counters):
            return "\(flow.label) route/counter mismatch: route \(route), counters \(counters)."
        case let .resultProvenanceMismatch(flow):
            return "\(flow.label) NLMR hashes or result route differ from the live negotiated endpoint."
        case let .resultVectorMismatch(flow):
            return "\(flow.label) generated result decoder returned unexpected test-vector values."
        case let .asyncRequestPreflightMismatch(flow):
            return "\(flow.label) live async request preflight did not match its negotiated codecs."
        case let .wrongResultPeerAccepted(flow):
            return "\(flow.label) accepted a result with the wrong generated peer codec."
        case let .staleResultContextAccepted(flow):
            return "\(flow.label) decoded a result after its endpoint closed."
        case let .asyncOperationMismatch(flow, detail):
            return "\(flow.label) AsyncCallbackV1 operation failed its \(detail) assertion."
        case let .asyncDrainMismatch(flow):
            return "\(flow.label) V2 release or lease counts did not drain to zero."
        }
    }
}

private extension NetLiftV5Flow {
    var label: String {
        switch self {
        case .oldLocalWithNewPeer: return "native-new/managed-old"
        case .newLocalWithOldPeer: return "native-old/managed-new"
        }
    }
}
