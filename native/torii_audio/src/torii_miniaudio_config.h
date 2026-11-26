#pragma once

/*
 * Shared miniaudio configuration header — MUST be included before miniaudio.h
 * in EVERY translation unit so struct layouts (ma_context, ma_device, ma_decoder)
 * are 100% ABI-identical across C and C++ files!
 */

#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_ENGINE
#define MA_NO_NODE_GRAPH
#define MA_NO_RESOURCE_MANAGER

#define MA_ENABLE_ONLY_SPECIFIC_BACKENDS
#if defined(_WIN32)
    #define MA_ENABLE_WASAPI
#elif defined(__APPLE__)
    #define MA_ENABLE_COREAUDIO
#elif defined(__ANDROID__)
    #define MA_ENABLE_AAUDIO
    #define MA_ENABLE_OPENSL
#else
    #define MA_ENABLE_PULSEAUDIO
    #define MA_ENABLE_ALSA
    #define MA_ENABLE_JACK
#endif
#define MA_ENABLE_NULL
