private struct LocalFixture: Fixture {
    func count() throws -> Int32 {
        Point(x: 7).x
    }
}

@main
struct ProjectionHostMain {
    static func main() throws {
        let point = Point(x: 7)
        precondition(point.x == 7)
        let count = try LocalFixture().count()
        precondition(count == 7)

        var config = lift_config()
        projection_fixture_make_config(&config)
        precondition(projection_fixture_validate_config(&config) == 0)

        let schema = String(cString: projection_fixture_schema_hash())
        print("XCODE_NATIVE_TARGET_PASS point=\(point.x) method=\(count) abi=\(LIFT_ABI_VERSION) schema=\(schema) native=\(nativeOnlyMarker())")
    }
}
