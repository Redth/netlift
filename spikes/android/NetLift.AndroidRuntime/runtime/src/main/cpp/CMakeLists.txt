cmake_minimum_required(VERSION 3.22.1)
project(netlift_androidruntime LANGUAGES C CXX)

set(NETLIFT_REPOSITORY_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../../../")
set(NETLIFT_ASYNC_RUNTIME_INCLUDE_DIR
    "${NETLIFT_REPOSITORY_ROOT}/tests/NetLift.Negotiation.NativeHost.AsyncRuntime")
set(NETLIFT_NATIVE_HOST_V5_DIR
    "${NETLIFT_REPOSITORY_ROOT}/src/NetLift.NativeHost.V5")
set(NETLIFT_TYPED_HOST_DIR
    "${NETLIFT_REPOSITORY_ROOT}/spikes/apple/NativeSwiftHost/typed")

option(
    NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE
    "Use the source-owned test-only V2 lifecycle and accessor"
    OFF
)

set(NETLIFT_ANDROID_V2_LIFECYCLE_SOURCES)
if(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
    list(APPEND NETLIFT_ANDROID_V2_LIFECYCLE_SOURCES
        "${NETLIFT_NATIVE_HOST_V5_DIR}/async_v2_lifecycle.c"
        "${NETLIFT_NATIVE_HOST_V5_DIR}/async_v2_accessor.c"
    )
else()
    list(APPEND NETLIFT_ANDROID_V2_LIFECYCLE_SOURCES
        "${NETLIFT_TYPED_HOST_DIR}/CallbackLifecycleV2State.c"
        "${NETLIFT_TYPED_HOST_DIR}/CallbackLifecycleV2Accessor.c"
    )
endif()

# Both lifecycle selections are experimental dependencies of this spike, not
# a product NativeHost implementation.
add_library(
    netlift_androidruntime
    SHARED
    netlift_androidruntime.cpp
    "${CMAKE_CURRENT_SOURCE_DIR}/netlift_android_v2_provider.c"
    "${NETLIFT_NATIVE_HOST_V5_DIR}/async_v2_core.c"
    ${NETLIFT_ANDROID_V2_LIFECYCLE_SOURCES}
)
target_compile_features(netlift_androidruntime PRIVATE cxx_std_17)
target_compile_definitions(
    netlift_androidruntime
    PRIVATE
    NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1
    NETLIFT_ANDROID_ASYNC_V2_TEST_ONLY=1
)
if(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
    target_compile_definitions(
        netlift_androidruntime
        PRIVATE
        NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE=1
    )
endif()
target_include_directories(
    netlift_androidruntime
    PRIVATE
    "${NETLIFT_REPOSITORY_ROOT}/tests/NetLift.Negotiation.NativeHost"
    "${NETLIFT_ASYNC_RUNTIME_INCLUDE_DIR}"
    "${NETLIFT_NATIVE_HOST_V5_DIR}"
    "${NETLIFT_TYPED_HOST_DIR}"
    "${NETLIFT_TYPED_HOST_DIR}/GeneratedTyped"
)
target_link_libraries(netlift_androidruntime PRIVATE android log dl)
