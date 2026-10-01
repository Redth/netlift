cmake_minimum_required(VERSION 3.22.1)
project(netlift_host)

add_library(netlift_host SHARED netlift_host.cpp android_lift_native_abi.cpp)
get_filename_component(NETLIFT_REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../../.." ABSOLUTE)
target_include_directories(netlift_host PRIVATE
    "${NETLIFT_REPO_ROOT}/tests/NetLift.NativeHost.Managed/obj/projected")
if("$ENV{NETLIFT_STRESS_DIAGNOSTICS}" STREQUAL "1")
    target_compile_definitions(netlift_host PRIVATE NETLIFT_STRESS_DIAGNOSTICS=1)
endif()
find_library(android-log log)
find_library(android-dl dl)
target_link_libraries(netlift_host ${android-log} ${android-dl})
