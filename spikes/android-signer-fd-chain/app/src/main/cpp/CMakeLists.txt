cmake_minimum_required(VERSION 3.22.1)
project(android_signer_fd_chain C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

find_library(log-lib log)

add_library(trust_host SHARED trust_host.c)
target_link_libraries(trust_host PRIVATE android ${log-lib} dl)

add_library(fixture_a SHARED marker_a.c)
target_link_libraries(fixture_a PRIVATE ${log-lib})

add_library(fixture_b SHARED marker_b.c)
target_link_libraries(fixture_b PRIVATE ${log-lib})
