cmake_minimum_required(VERSION 3.21)
project(NetLiftNativeCore LANGUAGES C)

add_library(netlift SHARED netlift.c)
target_include_directories(netlift PUBLIC ../../include)
target_compile_features(netlift PRIVATE c_std_11)
set_target_properties(netlift PROPERTIES
    C_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES)
