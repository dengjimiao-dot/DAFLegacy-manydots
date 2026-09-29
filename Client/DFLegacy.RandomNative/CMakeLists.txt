cmake_minimum_required(VERSION 3.24)

project(DFLegacy.RandomNative LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "DFLegacy.RandomNative only supports Windows.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "DFLegacy.RandomNative must be configured with -A x64.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(DFLegacy.RandomNative SHARED
    include/dflegacy_random.h
    src/dflegacy_random.cpp
    src/dflegacy_random.def)

target_compile_definitions(DFLegacy.RandomNative PRIVATE
    DFLEGACY_RANDOM_EXPORTS)
target_compile_options(DFLegacy.RandomNative PRIVATE
    /W4
    /WX
    /permissive-)
target_include_directories(DFLegacy.RandomNative PUBLIC include)

set_target_properties(DFLegacy.RandomNative PROPERTIES
    OUTPUT_NAME "DFLegacy.RandomNative"
    DEBUG_POSTFIX "")

include(CTest)
if(BUILD_TESTING)
    add_executable(DFLegacy.RandomNative.Tests
        tests/random_native_tests.cpp)
    target_compile_options(DFLegacy.RandomNative.Tests PRIVATE
        /W4
        /WX
        /permissive-)
    target_include_directories(DFLegacy.RandomNative.Tests PRIVATE include)
    target_link_libraries(DFLegacy.RandomNative.Tests PRIVATE DFLegacy.RandomNative)
    add_test(NAME rdseed_native COMMAND DFLegacy.RandomNative.Tests)
endif()
