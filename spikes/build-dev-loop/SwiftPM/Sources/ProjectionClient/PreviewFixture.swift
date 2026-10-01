import Foundation
import ProjectionCABI

public enum PreviewFixture {
    public static func makePoint() -> Point {
        Point(x: 7)
    }

    public static func schemaHash() -> String {
        String(cString: projection_fixture_schema_hash())
    }
}
