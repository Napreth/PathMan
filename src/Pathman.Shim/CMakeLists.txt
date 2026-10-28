cmake_minimum_required(VERSION 3.24)
project(Pathman.Shim LANGUAGES CXX RC)

if(NOT WIN32)
    message(FATAL_ERROR "PathMan.Shim only supports Windows")
endif()


set(PATHMAN_VERSION_NUMBER "0.0.0" CACHE STRING "Numeric build identity")
set(PATHMAN_VERSION_NAME "0.0.0" CACHE STRING "Product version name")
string(REPLACE "." "," PATHMAN_VERSION_TUPLE "${PATHMAN_VERSION_NUMBER}.0")
set(PATHMAN_BINARY_NAME "PathMan.Shim")
set(PATHMAN_FILE_TYPE "VFT_APP")
configure_file(../../packaging/native/version.rc.in version.rc @ONLY)
add_executable(Pathman.Shim main.cpp "${CMAKE_CURRENT_BINARY_DIR}/version.rc")
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
