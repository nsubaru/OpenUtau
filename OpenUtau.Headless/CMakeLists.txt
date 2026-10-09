cmake_minimum_required(VERSION 3.24)
project(OpenUtauHeadlessWorld LANGUAGES CXX)
include(FetchContent)
find_package(Git REQUIRED)

FetchContent_Declare(world
    SOURCE_SUBDIR headless-no-project
    URL https://github.com/mmorise/World/archive/f8dd5fb289db6a7f7f704497752bf32b258f9151.zip
    URL_HASH SHA256=2054f12a8961ed391b089c401c390f983f7ef7e31f63eabcb15a135563001c95
    PATCH_COMMAND ${CMAKE_COMMAND} -DSOURCE_DIR=<SOURCE_DIR> -DGIT_EXECUTABLE=${GIT_EXECUTABLE} -DPATCH_FILE=${CMAKE_CURRENT_SOURCE_DIR}/../cpp/third_party/world.patch -P ${CMAKE_CURRENT_SOURCE_DIR}/ApplyPatch.cmake)
FetchContent_Declare(spline
    SOURCE_SUBDIR headless-no-project
    URL https://github.com/ttk592/spline/archive/5894beaf91e9adbfdbe5c6c9a1c60770e380e8e8.zip
    URL_HASH SHA256=0d42b77d0f398c9ad2b7290587b625c49c39a2337f6cfa88b6a7930207d91a33
    PATCH_COMMAND ${CMAKE_COMMAND} -DSOURCE_DIR=<SOURCE_DIR> -DGIT_EXECUTABLE=${GIT_EXECUTABLE} -DPATCH_FILE=${CMAKE_CURRENT_SOURCE_DIR}/../cpp/third_party/spline.patch -P ${CMAKE_CURRENT_SOURCE_DIR}/ApplyPatch.cmake)
FetchContent_MakeAvailable(world spline)
file(GLOB WORLD_SOURCES CONFIGURE_DEPENDS "${world_SOURCE_DIR}/src/*.cpp")
# Real-time synthesis gets caller-owned noise state without patching the pinned
# WORLD dependency or changing the analysis/batch synthesis entry points.
set_source_files_properties("${world_SOURCE_DIR}/src/synthesisrealtime.cpp"
    PROPERTIES COMPILE_DEFINITIONS "randn=WorldStreamRandom")
add_library(openutau-worldcpu SHARED
    ${WORLD_SOURCES}
    world_stream.cpp
    ../cpp/worldline/worldline.cpp
    ../cpp/worldline/common/vec_utils.cpp
    ../cpp/worldline/f0/dio_estimator.cpp
    ../cpp/worldline/f0/dio_ss_estimator.cpp
    ../cpp/worldline/f0/harvest_estimator.cpp
    ../cpp/worldline/model/effects.cpp
    ../cpp/worldline/model/continuous_noise.cpp)
target_include_directories(openutau-worldcpu PRIVATE ../cpp ${world_SOURCE_DIR}/src ${spline_SOURCE_DIR}/src)
target_compile_features(openutau-worldcpu PRIVATE cxx_std_17)
target_compile_definitions(openutau-worldcpu PRIVATE OPENUTAU_HEADLESS_WORLD_ONLY)
if(MSVC)
    target_compile_options(openutau-worldcpu PRIVATE /MP /O2)
endif()
install(TARGETS openutau-worldcpu RUNTIME DESTINATION . LIBRARY DESTINATION .)
