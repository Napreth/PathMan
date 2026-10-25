cmake_minimum_required(VERSION 3.24)
project(Pathman.Shim LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "PathMan.Shim only supports Windows")
endif()


add_executable(Pathman.Shim main.cpp)
target_link_libraries(Pathman.Shim PRIVATE advapi32 shell32 ole32)
set_target_properties(Pathman.Shim PROPERTIES
    OUTPUT_NAME "PathMan.Shim"
    CXX_STANDARD 20
    CXX_STANDARD_REQUIRED ON
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_compile_definitions(Pathman.Shim PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(Pathman.Shim PRIVATE /W4 /WX /utf-8 /permissive-)
endif()
