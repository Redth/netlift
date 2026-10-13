cmake_minimum_required(VERSION 3.22.1)
project(NetLiftAndroidRuntime LANGUAGES C)

add_subdirectory(
    ../../../../../src/NetLift.NativeCore
    ${CMAKE_CURRENT_BINARY_DIR}/native-core
)

add_library(netlift_android SHARED netlift_android.c)
target_compile_features(netlift_android PRIVATE c_std_11)
target_compile_options(netlift_android PRIVATE -Wall -Wextra -Werror)
target_link_libraries(netlift_android PRIVATE netlift log)
