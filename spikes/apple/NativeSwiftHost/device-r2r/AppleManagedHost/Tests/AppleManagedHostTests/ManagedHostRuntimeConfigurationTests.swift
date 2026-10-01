import XCTest

@_silgen_name("apple_managed_host_test_configuration_reuse")
private func testHostConfigurationReuse() -> Int32

final class ManagedHostRuntimeConfigurationTests: XCTestCase {
    func testConfigurationIsCopiedAndIncompatibleRepeatIsRejected() {
        XCTAssertEqual(testHostConfigurationReuse(), 0)
    }
}
