import Darwin
import Foundation

typealias ProbeEntry = @convention(c) () -> Int32

func fail(_ message: String, status: Int32) -> Never {
    FileHandle.standardError.write(Data("FAIL \(message)\n".utf8))
    exit(status)
}

guard let resourceURL = Bundle.main.resourceURL else {
    fail("bundle has no resource directory", status: 2)
}

let libraryURL = Bundle.main.bundleURL
    .appendingPathComponent("Contents/Frameworks/libProbe.dylib")
guard let handle = dlopen(libraryURL.path, RTLD_NOW | RTLD_LOCAL) else {
    let message = dlerror().map { String(cString: $0) } ?? "unknown dyld error"
    fail("dlopen \(libraryURL.path): \(message)", status: 3)
}

guard let symbol = dlsym(handle, "netlift_nativeaot_probe") else {
    let message = dlerror().map { String(cString: $0) } ?? "unknown dlsym error"
    fail("dlsym netlift_nativeaot_probe: \(message)", status: 4)
}

let entry = unsafeBitCast(symbol, to: ProbeEntry.self)
let result = entry()
print(String(format: "MANAGED_ENTRY_RESULT=0x%04x", UInt32(bitPattern: result)))
guard result == 0x4e4c else {
    fail("unexpected native entry result: \(result)", status: 5)
}

var loadedImages: [String] = []
for index in 0..<_dyld_image_count() {
    guard let image = _dyld_get_image_name(index) else {
        continue
    }
    let path = String(cString: image)
    loadedImages.append(path)
    print("IMAGE=\(path)")
}

let forbiddenImageNames = ["libcoreclr", "libclrjit", "clrjit"]
if let forbidden = loadedImages.first(where: { image in
    let lowercased = image.lowercased()
    return forbiddenImageNames.contains { lowercased.contains($0) }
}) {
    fail("unexpected CoreCLR/JIT image loaded: \(forbidden)", status: 6)
}

print("PASS deterministic NativeAOT entry and no CoreCLR/JIT image")
