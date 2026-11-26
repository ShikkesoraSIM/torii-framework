cmake_minimum_required(VERSION 3.16)
project(torii_audio LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

option(TORII_AUDIO_STATIC "Build torii_audio as a static library (e.g. for iOS)" OFF)
option(TORII_AUDIO_BUILD_TESTS "Build standalone C++ test & latency harness" ON)

if(MSVC)
    # Statically link CRT (/MT) so torii_audio.dll has zero external VC++ runtime DLL dependencies.
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

set(TORII_AUDIO_SOURCES
    src/torii_audio_vendor.c
    src/torii_audio_probe.cpp
    src/torii_audio_engine.cpp
)

if(TORII_AUDIO_STATIC)
    add_library(torii_audio STATIC ${TORII_AUDIO_SOURCES})
    target_compile_definitions(torii_audio PUBLIC TORII_AUDIO_STATIC_BUILD=1)
else()
    add_library(torii_audio SHARED ${TORII_AUDIO_SOURCES})
    target_compile_definitions(torii_audio PRIVATE TORII_AUDIO_EXPORTS=1)
endif()

target_include_directories(torii_audio
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${CMAKE_CURRENT_SOURCE_DIR}/third_party
        ${CMAKE_CURRENT_SOURCE_DIR}/third_party/signalsmith-stretch
        ${CMAKE_CURRENT_SOURCE_DIR}/third_party/signalsmith-linear
)

if(MSVC)
    target_compile_options(torii_audio PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:/O2 /fp:fast /EHsc /W3 /wd4244 /wd4267 /wd4996 /wd4305>
        $<$<COMPILE_LANGUAGE:C>:/O2 /fp:fast /W1 /wd4244 /wd4267 /wd4996 /wd4305 /wd4018>
    )
    target_compile_definitions(torii_audio PRIVATE
        _CRT_SECURE_NO_WARNINGS
        NOMINMAX
        WIN32_LEAN_AND_MEAN
    )
    target_link_libraries(torii_audio PRIVATE ole32 avrt mmdevapi winmm)
elseif(APPLE)
    target_compile_options(torii_audio PRIVATE -O3 -ffast-math -fvisibility=hidden)
    if(IOS)
        target_link_libraries(torii_audio PRIVATE "-framework AudioToolbox" "-framework CoreAudio" "-framework AVFoundation")
    else()
        target_link_libraries(torii_audio PRIVATE "-framework CoreFoundation" "-framework CoreAudio" "-framework AudioToolbox")
        set_target_properties(torii_audio PROPERTIES
            INSTALL_NAME_DIR "@loader_path"
            BUILD_WITH_INSTALL_RPATH ON
        )
    endif()
elseif(ANDROID)
    target_compile_options(torii_audio PRIVATE -O3 -ffast-math -fvisibility=hidden)
    target_link_libraries(torii_audio PRIVATE log aaudio OpenSLES m)
else()
    # Linux: miniaudio dynamically dlopens PipeWire/PulseAudio/ALSA at runtime (no hard DT_NEEDED).
    target_compile_options(torii_audio PRIVATE -O3 -ffast-math -fvisibility=hidden)
    target_link_libraries(torii_audio PRIVATE pthread dl m)
endif()

if(TORII_AUDIO_BUILD_TESTS AND NOT IOS AND NOT ANDROID)
    add_executable(torii_audio_test tests/torii_audio_test.cpp)
    target_link_libraries(torii_audio_test PRIVATE torii_audio)
    target_include_directories(torii_audio_test PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
    if(MSVC)
        target_compile_options(torii_audio_test PRIVATE /O2 /EHsc /W3)
    endif()
endif()
