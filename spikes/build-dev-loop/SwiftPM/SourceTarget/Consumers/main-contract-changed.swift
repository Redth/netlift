import ProjectionCABI

let point = Point(x: 7)
let vector = Vector(x: 12)
precondition(point.x == 7 && vector.x == 12)

var config = lift_config()
projection_fixture_make_config(&config)
precondition(projection_fixture_validate_config(&config) == 0)

let schemaHash = String(cString: projection_fixture_schema_hash())
print("SWIFTPM_TYPED_CONSUMER_PASS point=\(point.x) vector=\(vector.x) abi=\(LIFT_ABI_VERSION) schema=\(schemaHash)")
