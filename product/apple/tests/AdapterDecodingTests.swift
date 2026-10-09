import Foundation

@main
enum AdapterDecodingTests {
    static func main() throws {
        expectFailure("UUID", UuidRoundTripResult.self, #"{"Value":"not-a-uuid"}"#)
        expectFailure(
            "timestamp",
            AdapterRoundTripResult.self,
            #"{"Payload":"AAE=","Timestamp":"not-a-timestamp","Resource":"https://example.test"}"#
        )
        expectFailure(
            "base64",
            AdapterRoundTripResult.self,
            #"{"Payload":"%%%","Timestamp":"2026-10-09T16:34:56.789Z","Resource":"https://example.test"}"#
        )
        expectFailure(
            "URL",
            AdapterRoundTripResult.self,
            #"{"Payload":"AAE=","Timestamp":"2026-10-09T16:34:56.789Z","Resource":"http://["}"#
        )

        let valid = try JSONDecoder().decode(
            AdapterRoundTripResult.self,
            from: Data(
                #"{"Payload":"AAF/gP8=","Timestamp":"2026-10-09T16:34:56.789Z","Resource":"https://example.test/orders/42"}"#.utf8
            )
        )
        precondition(valid.payload == Data([0, 1, 127, 128, 255]))
        precondition(valid.resource.absoluteString == "https://example.test/orders/42")
        print("Swift adapter decoding tests passed.")
    }

    private static func expectFailure<T: Decodable>(
        _ label: String,
        _ type: T.Type,
        _ json: String
    ) {
        do {
            _ = try JSONDecoder().decode(type, from: Data(json.utf8))
            fatalError("\(label) decoder accepted a malformed adapter value.")
        } catch {
        }
    }
}
