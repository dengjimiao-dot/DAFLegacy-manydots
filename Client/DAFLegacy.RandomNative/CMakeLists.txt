cmake_minimum_required(VERSION 3.24)

project(DAFLegacy.RandomNative LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "DAFLegacy.RandomNative only supports Windows.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "DAFLegacy.RandomNative must be configured with -A x64.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(DAFLegacy.RandomNative SHARED
    include/daflegacy_random.h
    src/daflegacy_random.cpp
    src/daflegacy_random.def)

target_compile_definitions(DAFLegacy.RandomNative PRIVATE
    DAFLEGACY_RANDOM_EXPORTS)
target_compile_options(DAFLegacy.RandomNative PRIVATE
    /W4
    /WX
    /permissive-)
target_include_directories(DAFLegacy.RandomNative PUBLIC include)

set_target_properties(DAFLegacy.RandomNative PROPERTIES
    OUTPUT_NAME "DAFLegacy.RandomNative"
    DEBUG_POSTFIX "")

include(CTest)
if(BUILD_TESTING)
    add_executable(DAFLegacy.RandomNative.Tests
        tests/random_native_tests.cpp)
    target_compile_options(DAFLegacy.RandomNative.Tests PRIVATE
        /W4
        /WX
        /permissive-)
    target_include_directories(DAFLegacy.RandomNative.Tests PRIVATE include)
    target_link_libraries(DAFLegacy.RandomNative.Tests PRIVATE DAFLegacy.RandomNative)
    add_test(NAME rdseed_native COMMAND DAFLegacy.RandomNative.Tests)
endif()
