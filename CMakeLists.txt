cmake_minimum_required(VERSION 4.0)
project(WorkForNative VERSION 0.1.0 LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "WorkFor native prototypes require Windows x64 and MSVC.")
endif()

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(WORKFOR_JSON_INCLUDE "${CMAKE_SOURCE_DIR}/out/native-deps" CACHE PATH "Directory containing nlohmann/json.hpp")
if(NOT EXISTS "${WORKFOR_JSON_INCLUDE}/nlohmann/json.hpp")
  message(FATAL_ERROR "Run scripts/restore-native-deps.ps1 before configuring.")
endif()

add_library(WorkFor.Taskbar SHARED
  src/WorkFor.Taskbar/Bootstrap.cpp
  src/WorkFor.Taskbar/Adapter.cpp
  src/WorkFor.Taskbar/Compatibility.cpp)
add_library(WorkFor.Taskbar.Live SHARED
  src/WorkFor.Taskbar/Bootstrap.cpp
  src/WorkFor.Taskbar/Adapter.cpp
  src/WorkFor.Taskbar/Compatibility.cpp)
add_executable(WorkFor.NativeBridge
  src/WorkFor.NativeBridge/main.cpp
  src/WorkFor.NativeBridge/TargetIdentity.cpp
  src/WorkFor.NativeBridge/BootstrapClient.cpp
  src/WorkFor.NativeBridge/DiscoveryWatcher.cpp
  src/WorkFor.NativeBridge/ControlStdio.cpp
  src/WorkFor.NativeBridge/ProtocolSelfTest.cpp)

foreach(target WorkFor.Taskbar WorkFor.Taskbar.Live WorkFor.NativeBridge)
  target_include_directories(${target} PRIVATE "${WORKFOR_JSON_INCLUDE}")
  target_compile_definitions(${target} PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
  target_compile_options(${target} PRIVATE /W4 /utf-8 /permissive- /EHsc)
  target_link_libraries(${target} PRIVATE user32 advapi32 bcrypt runtimeobject windowsapp version)
  set_target_properties(${target} PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
endforeach()
add_dependencies(WorkFor.NativeBridge WorkFor.Taskbar WorkFor.Taskbar.Live)

enable_testing()
add_test(NAME native-bootstrap-owned-target COMMAND WorkFor.NativeBridge --self-test)
set_tests_properties(native-bootstrap-owned-target PROPERTIES TIMEOUT 30)

add_executable(WorkFor.Adapter.Tests
  experiments/taskbar/adapter-layout-test.cpp
  src/WorkFor.Taskbar/Adapter.cpp
  src/WorkFor.Taskbar/Compatibility.cpp)
target_compile_definitions(WorkFor.Adapter.Tests PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_include_directories(WorkFor.Adapter.Tests PRIVATE "${CMAKE_SOURCE_DIR}/src/WorkFor.Taskbar")
target_compile_options(WorkFor.Adapter.Tests PRIVATE /W4 /utf-8 /permissive- /EHsc)
target_link_libraries(WorkFor.Adapter.Tests PRIVATE user32 advapi32 bcrypt runtimeobject windowsapp version)
target_link_options(WorkFor.Adapter.Tests PRIVATE
  "/MANIFEST:EMBED"
  "/MANIFESTINPUT:${CMAKE_SOURCE_DIR}/experiments/taskbar/adapter-layout-test.manifest")
set_target_properties(WorkFor.Adapter.Tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
add_test(NAME native-xaml-layout COMMAND WorkFor.Adapter.Tests)
set_tests_properties(native-xaml-layout PROPERTIES TIMEOUT 30)
