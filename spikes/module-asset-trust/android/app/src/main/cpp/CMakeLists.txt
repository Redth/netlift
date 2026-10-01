cmake_minimum_required(VERSION 3.22.1)
project(module_asset_trust_android C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(trust_host SHARED trust_host.c)
target_link_libraries(trust_host PRIVATE android log dl)

add_library(module_dependency SHARED module_dependency.c)
set_target_properties(module_dependency PROPERTIES OUTPUT_NAME module_dependency)

add_library(fixture_module_v1 SHARED fixture_module.c)
target_compile_definitions(fixture_module_v1 PRIVATE FIXTURE_BUILD=1)
target_link_libraries(fixture_module_v1 PRIVATE module_dependency)
target_link_options(fixture_module_v1 PRIVATE "-Wl,-soname,libnetlift_fd_probe.so")
set_target_properties(fixture_module_v1 PROPERTIES OUTPUT_NAME fixture_v1)

add_library(fixture_module_v2 SHARED fixture_module.c)
target_compile_definitions(fixture_module_v2 PRIVATE FIXTURE_BUILD=2)
target_link_libraries(fixture_module_v2 PRIVATE module_dependency)
target_link_options(fixture_module_v2 PRIVATE "-Wl,-soname,libnetlift_fd_probe.so")
set_target_properties(fixture_module_v2 PROPERTIES OUTPUT_NAME fixture_v2)
