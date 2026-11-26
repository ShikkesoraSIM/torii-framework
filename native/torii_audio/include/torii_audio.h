#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(TORII_AUDIO_STATIC_BUILD)
    #define TA_API
#elif defined(_WIN32)
    #if defined(TORII_AUDIO_EXPORTS)
        #define TA_API __declspec(dllexport)
    #else
        #define TA_API __declspec(dllimport)
    #endif
#else
    #define TA_API __attribute__((visibility("default")))
#endif

typedef struct ta_engine ta_engine;

typedef enum ta_error {
    TA_OK = 0,
    TA_ERR_GENERIC = 1,
    TA_ERR_BUSY = 2,                    /* Exclusive device held by another app (AUDCLNT_E_DEVICE_IN_USE) */
    TA_ERR_EXCLUSIVE_NOT_ALLOWED = 3,   /* Windows "Allow applications to take exclusive control" disabled */
    TA_ERR_FORMAT_UNSUPPORTED = 4,      /* No float/int format accepted by device */
    TA_ERR_DEVICE_LOST = 5,
    TA_ERR_NOT_INITIALIZED = 6,
    TA_ERR_INVALID_HANDLE = 7,
    TA_ERR_DECODE_FAILED = 8,
    TA_ERR_OUT_OF_MEMORY = 9
} ta_error;

typedef enum ta_device_mode {
    TA_MODE_NO_SOUND = 0,               /* Software-clocked silent pump for headless/CI/no-device */
    TA_MODE_LEGACY_SHARED = 1,          /* Safe shared mode (~10-20ms buffer, weak CPU friendly) */
    TA_MODE_LOW_LATENCY_SHARED = 2,     /* Event-driven low-latency shared mode (WASAPI/AAudio/CoreAudio/PipeWire) */
    TA_MODE_EXCLUSIVE = 3               /* WASAPI Exclusive / AAudio MMAP Exclusive (2-4ms) */
} ta_device_mode;

typedef enum ta_audio_format {
    TA_FORMAT_UNKNOWN = 0,
    TA_FORMAT_MP3 = 1,
    TA_FORMAT_OGG = 2,
    TA_FORMAT_WAVE = 3,
    TA_FORMAT_FLAC = 4
} ta_audio_format;

typedef enum ta_fx_type {
    TA_FX_BIQUAD_LOWPASS = 0,
    TA_FX_BIQUAD_HIGHPASS = 1,
    TA_FX_BIQUAD_BANDPASS = 2,
    TA_FX_BIQUAD_NOTCH = 3,
    TA_FX_BIQUAD_PEAKING_EQ = 4,
    TA_FX_BIQUAD_LOW_SHELF = 5,
    TA_FX_BIQUAD_HIGH_SHELF = 6,
    TA_FX_BIQUAD_ALLPASS = 7,
    TA_FX_CHORUS = 8,
    TA_FX_PHASER = 9,
    TA_FX_AUTOWAH = 10
} ta_fx_type;

typedef enum ta_channel_state {
    TA_STATE_STOPPED = 0,
    TA_STATE_PLAYING = 1,
    TA_STATE_PAUSED = 2
} ta_channel_state;

typedef struct ta_device_info {
    int32_t index;                      /* Physical playback device index (0..N-1) */
    int32_t is_default;                 /* 1 if OS default playback device */
    int32_t is_enabled;                 /* Always 1 for enumerated miniaudio playback devices */
    char name[256];                     /* UTF-8 device name */
} ta_device_info;

typedef struct ta_device_caps {
    int32_t sample_rate;
    int32_t channels;
    int32_t actual_mode;                /* ta_device_mode actually opened */
    int32_t is_float_output;            /* 1 if native float32 output, 0 if integer PCM */
    int32_t bit_depth;                  /* 32, 24, or 16 */
    double active_period_ms;            /* Real accepted hardware period in ms */
    double output_latency_ms;           /* Initial measured hardware output latency in ms */
    char backend_name[64];              /* e.g. "WASAPI", "CoreAudio", "AAudio", "PipeWire", "NoSound" */
} ta_device_caps;

typedef struct ta_fx_params {
    int32_t type;                       /* ta_fx_type */
    float center_hz;                    /* Cutoff / center frequency in Hz */
    float q;                            /* Q factor (default ~0.707) */
    float bandwidth;                    /* Bandwidth in octaves (0 = use q) */
    float gain_db;                      /* Gain in dB for Peaking/Shelf */
    float dry_mix;                      /* Dry mix for Chorus/Phaser/AutoWah */
    float wet_mix;                      /* Wet mix for Chorus/Phaser/AutoWah */
    float feedback;                     /* Feedback for modulation FX */
    float rate_hz;                      /* LFO rate in Hz */
    float depth;                        /* Modulation depth */
    float delay_ms;                     /* Base delay in ms (Chorus) */
    float range_octaves;                /* Sweep range (Phaser/AutoWah) */
} ta_fx_params;

typedef struct ta_track_info {
    int32_t format;                     /* ta_audio_format */
    int32_t sample_rate;
    int32_t channels;
    int32_t bitrate_kbps;
    int64_t total_frames;
    double length_ms;
    double last_seekable_ms;
} ta_track_info;

typedef struct ta_track_clock {
    double current_time_ms;
    int32_t is_playing;                 /* 1 if actively running (including zero-freq virtual play) */
    int32_t has_completed;              /* 1 if reached natural end (non-looping) */
    uint32_t loop_count;                /* Increments each time track wraps on loop */
    uint32_t end_event_count;           /* Increments each time track hits natural end */
} ta_track_clock;

typedef struct ta_sample_info {
    int32_t format;                     /* ta_audio_format */
    int32_t sample_rate;
    int32_t channels;
    int64_t total_frames;
    int64_t pcm_bytes;                  /* Native decoded PCM byte count for memory tracking */
    double length_ms;
} ta_sample_info;

typedef struct ta_audio_info {
    int32_t format;                     /* ta_audio_format */
    int32_t sample_rate;
    int32_t channels;
    int32_t bitrate_kbps;
    int64_t total_frames;
    double duration_ms;
} ta_audio_info;

/* Binary-identical to C# osu.Framework.Audio.Track.Waveform.Point (20 bytes) */
typedef struct ta_waveform_point {
    float amplitude_left;
    float amplitude_right;
    float low_intensity;
    float mid_intensity;
    float high_intensity;
} ta_waveform_point;

typedef struct ta_waveform_result {
    ta_waveform_point* points;
    int32_t point_count;
    int32_t channels;
} ta_waveform_result;

typedef int32_t (*ta_io_read_cb)(void* user_data, void* dst, int32_t bytes);
typedef int32_t (*ta_io_seek_cb)(void* user_data, int64_t offset);
typedef int64_t (*ta_io_length_cb)(void* user_data);

typedef struct ta_io_callbacks {
    ta_io_read_cb read;
    ta_io_seek_cb seek;
    ta_io_length_cb length;
} ta_io_callbacks;

/* ---------------- Engine & Device ---------------- */

TA_API ta_engine* ta_engine_create(void);
TA_API void ta_engine_destroy(ta_engine* engine);

TA_API int32_t ta_enumerate_devices(ta_engine* engine, ta_device_info* out_devices, int32_t max_count);

/*
 * device_index:
 *   0  -> NoSound software device (TA_MODE_NO_SOUND)
 *   1  -> OS Default playback device
 *  >=2 -> Specific physical device index = device_index - 2
 */
TA_API int32_t ta_device_init(
    ta_engine* engine,
    int32_t device_index,
    int32_t mode,
    double exclusive_period_ms,
    int32_t preferred_sample_rate,
    ta_device_caps* out_caps,
    char* out_error,
    int32_t out_error_cap);

TA_API void ta_device_free(ta_engine* engine);
TA_API int32_t ta_device_pause(ta_engine* engine);
TA_API int32_t ta_device_start(ta_engine* engine);
TA_API int32_t ta_device_is_started(ta_engine* engine);
TA_API int32_t ta_device_is_lost(ta_engine* engine);
TA_API double ta_get_output_latency_ms(ta_engine* engine);
TA_API float ta_get_cpu_usage(ta_engine* engine);
TA_API int32_t ta_flush_output(ta_engine* engine);
TA_API int32_t ta_get_last_error(ta_engine* engine);
TA_API int32_t ta_get_device_sample_rate(ta_engine* engine);

/* ---------------- Mixers & DSP FX ---------------- */

TA_API int32_t ta_mixer_create(ta_engine* engine, int32_t is_global_root);
TA_API void ta_mixer_destroy(ta_engine* engine, int32_t mixer_handle);
TA_API int32_t ta_mixer_add_channel(ta_engine* engine, int32_t mixer_handle, int32_t channel_handle, int32_t start_paused);
TA_API int32_t ta_mixer_remove_channel(ta_engine* engine, int32_t mixer_handle, int32_t channel_handle);
TA_API void ta_mixer_flush(ta_engine* engine, int32_t mixer_handle);
TA_API int32_t ta_mixer_get_channels(ta_engine* engine, int32_t mixer_handle, int32_t* out_handles, int32_t max_count);
TA_API int32_t ta_mixer_get_levels(ta_engine* engine, int32_t mixer_handle, float window_sec, float* out_left, float* out_right);

TA_API int32_t ta_mixer_fx_add(ta_engine* engine, int32_t mixer_handle, const ta_fx_params* params, int32_t priority);
TA_API int32_t ta_mixer_fx_update(ta_engine* engine, int32_t mixer_handle, int32_t fx_handle, const ta_fx_params* params);
TA_API int32_t ta_mixer_fx_remove(ta_engine* engine, int32_t mixer_handle, int32_t fx_handle);

/* ---------------- Tracks ---------------- */

TA_API int32_t ta_track_create(ta_engine* engine, const uint8_t* data, size_t len, int32_t quick_preview, ta_track_info* out_info);
TA_API void ta_track_destroy(ta_engine* engine, int32_t track_handle);
TA_API int32_t ta_track_play(ta_engine* engine, int32_t track_handle);
TA_API int32_t ta_track_pause(ta_engine* engine, int32_t track_handle, int32_t flush_mixer);
TA_API int32_t ta_track_seek_ms(ta_engine* engine, int32_t track_handle, double target_ms, double* out_actual_ms);
TA_API void ta_track_set_params(ta_engine* engine, int32_t track_handle, float volume, float balance, double frequency, double tempo, int32_t looping, double restart_point_ms);
TA_API int32_t ta_track_get_clock(ta_engine* engine, int32_t track_handle, ta_track_clock* out_clock);

/* ---------------- Samples & Polyphonic Voices ---------------- */

TA_API int32_t ta_sample_create(ta_engine* engine, const uint8_t* data, size_t len, int32_t max_concurrency, ta_sample_info* out_info);
TA_API void ta_sample_set_concurrency(ta_engine* engine, int32_t sample_handle, int32_t max_concurrency);
TA_API void ta_sample_destroy(ta_engine* engine, int32_t sample_handle);

TA_API int32_t ta_sample_channel_create(ta_engine* engine, int32_t sample_handle, int32_t mixer_handle);
TA_API int32_t ta_sample_channel_play(ta_engine* engine, int32_t channel_handle);
TA_API int32_t ta_sample_channel_stop(ta_engine* engine, int32_t channel_handle);
TA_API void ta_sample_channel_set_params(ta_engine* engine, int32_t channel_handle, float volume, float balance, double frequency, int32_t looping);
TA_API int32_t ta_sample_channel_get_state(ta_engine* engine, int32_t channel_handle);
TA_API void ta_sample_channel_destroy(ta_engine* engine, int32_t channel_handle);

/* ---------------- Channel Analysis (Tracks & SampleChannels) ---------------- */

TA_API int32_t ta_channel_get_amplitudes(ta_engine* engine, int32_t channel_handle, float* out_left, float* out_right, float* out_fft256);
TA_API int32_t ta_channel_get_levels(ta_engine* engine, int32_t channel_handle, float window_sec, float* out_left, float* out_right);
TA_API int32_t ta_channel_get_state(ta_engine* engine, int32_t channel_handle);

/* ---------------- Offline Deviceless Probing & Waveform ---------------- */

TA_API int32_t ta_probe_memory(const uint8_t* data, size_t len, int32_t prescan, ta_audio_info* out_info);
TA_API int32_t ta_probe_callbacks(const ta_io_callbacks* cbs, void* user_data, int32_t prescan, ta_audio_info* out_info);
TA_API int32_t ta_waveform_generate(const uint8_t* data, size_t len, ta_waveform_result* out_result);
TA_API void ta_waveform_free(ta_waveform_result* result);
TA_API int32_t ta_decode_memory_f32(const uint8_t* data, size_t len, float** out_pcm, int64_t* out_frames, int32_t* out_sample_rate, int32_t* out_channels);
TA_API void ta_free_buffer(void* ptr);

#ifdef __cplusplus
}
#endif
