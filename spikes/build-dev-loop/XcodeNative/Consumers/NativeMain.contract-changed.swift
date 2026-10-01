private struct LocalFixture: Fixture {
    func count() throws -> Int32 {
        Point(x: 7).x + Vector(x: 12).x
    }
}

@main
struct ProjectionHostMain {
    static func main() throws {
        let point = Point(x: 7)
        let vector = Vector(x: 12)
        precondition(point.x == 7 && vector.x == 12)
        let count = try LocalFixture().count()
        precondition(count == 19)

        var config = lift_config()
        projection_fixture_make_config(&config)
        precondition(projection_fixture_validate_config(&config) == 0)

        let schema = String(cString: projection_fixture_schema_hash())
        print("XCODE_NATIVE_TARGET_PASS point=\(point.x) vector=\(vector.x) method=\(count) abi=\(LIFT_ABI_VERSION) schema=\(schema) native=\(nativeOnlyMarker())")
    }
}
