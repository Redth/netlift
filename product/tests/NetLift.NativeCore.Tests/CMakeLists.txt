cmake_minimum_required(VERSION 3.21)
project(NetLiftNativeCoreTests LANGUAGES C CXX)

enable_testing()
find_package(Threads REQUIRED)
add_subdirectory(../../src/NetLift.NativeCore native-core)

add_executable(netlift_native_tests native_tests.c)
target_link_libraries(netlift_native_tests PRIVATE netlift Threads::Threads)
target_compile_features(netlift_native_tests PRIVATE c_std_11)
add_test(NAME netlift_native_tests COMMAND netlift_native_tests)

add_executable(netlift_header_cpp_tests header_tests.cpp)
target_link_libraries(netlift_header_cpp_tests PRIVATE netlift)
target_compile_features(netlift_header_cpp_tests PRIVATE cxx_std_17)
add_test(NAME netlift_header_cpp_tests COMMAND netlift_header_cpp_tests)
