cmake_minimum_required(VERSION 3.22.1)
project(android_split_first_native_host C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

find_library(log-lib log)
find_library(dl-lib dl)

add_library(bootstrap_dependency SHARED bootstrap_dependency.c)
target_link_libraries(bootstrap_dependency PRIVATE ${log-lib})

add_library(first_host SHARED first_host.c)
target_link_libraries(first_host PRIVATE bootstrap_dependency ${log-lib} ${dl-lib})
