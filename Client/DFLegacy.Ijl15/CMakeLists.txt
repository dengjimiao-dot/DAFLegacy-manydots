cmake_minimum_required(VERSION 3.24)

project(DFLegacy.Ijl15 LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "DFLegacy.Ijl15 only supports Windows.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "DFLegacy.Ijl15 must be configured with -A Win32.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(ijl15 SHARED
    include/ijl15.h
    src/d3d8_scaler.cpp
    src/d3d8_scaler.h
    src/dnf_patches.cpp
    src/dnf_patches.h
    src/external_string_table.cpp
    src/external_string_table.h
    src/ijl15_config.cpp
    src/ijl15_config.h
    src/ijl15.cpp
    src/ijl15.def
    src/message_audio.cpp
    src/message_audio.h
    src/memory_patch.cpp
    src/memory_patch.h
    src/mouse_wheel.cpp
    src/mouse_wheel.h
    src/output_paths.cpp
    src/output_paths.h
    src/performance_overlay.cpp
    src/performance_overlay.h
    src/window_scaling.cpp
    src/window_scaling.h)

target_compile_definitions(ijl15 PRIVATE
    IJL15_EXPORTS
    UNICODE
    _UNICODE)
target_compile_options(ijl15 PRIVATE
    /W4
    /WX
    /permissive-
    /EHsc)
target_include_directories(ijl15 PUBLIC include)
target_link_libraries(ijl15 PRIVATE
    ole32
    psapi
    shlwapi
    windowscodecs)

set_target_properties(ijl15 PROPERTIES
    OUTPUT_NAME "ijl15"
    DEBUG_POSTFIX "")

add_custom_command(TARGET ijl15 POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/Config.ini"
        "$<TARGET_FILE_DIR:ijl15>/Config.ini")

include(CTest)
if(BUILD_TESTING)
    add_executable(ijl15_tests
        src/external_string_table.cpp
        src/external_string_table.h
        src/memory_patch.cpp
        src/memory_patch.h
        src/output_paths.cpp
        src/output_paths.h
        tests/ijl15_tests.cpp)
    target_compile_options(ijl15_tests PRIVATE /W4 /WX /permissive- /EHsc)
    target_include_directories(ijl15_tests PRIVATE include src)
    add_dependencies(ijl15_tests ijl15)
    add_test(NAME ijl15_compatibility COMMAND ijl15_tests)
endif()
