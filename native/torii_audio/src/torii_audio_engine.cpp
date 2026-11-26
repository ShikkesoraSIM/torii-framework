#include "torii_audio.h"
#include "torii_audio_dsp.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

#include "torii_miniaudio_config.h"
#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"
#include "miniaudio.h"

#include "signalsmith-stretch.h"

#if defined(_WIN32)
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#endif

namespace torii_audio {

int32_t sniffFormatFromBytes(const uint8_t* data, size_t len) noexcept;

static constexpr int32_t kMaxMixers = 64;
static constexpr int32_t kMaxTracks = 64;
static constexpr int32_t kMaxSamples = 4096;
static constexpr int32_t kMaxSampleChannels = 512;
static constexpr int32_t kMaxFxPerMixer = 16;
static constexpr uint32_t kMaxRenderFrames = 4096;

// Handle encoding: high bits = type tag, low 16 bits = slot index + 1
// Type tags: 1 = Mixer, 2 = Track, 3 = Sample, 4 = SampleChannel, 5 = FX
enum HandleTag : int32_t {
    kTagMixer = 1 << 20,
    kTagTrack = 2 << 20,
    kTagSample = 3 << 20,
    kTagSampleChannel = 4 << 20,
    kTagFx = 5 << 20,
    kTagMask = 0x7FF00000,
    kIndexMask = 0x000FFFFF
};

static inline int32_t makeHandle(HandleTag tag, int32_t idx) noexcept { return tag | (idx + 1); }
static inline int32_t handleTag(int32_t h) noexcept { return h & kTagMask; }
static inline int32_t handleIndex(int32_t h) noexcept { return (h & kIndexMask) - 1; }

enum SlotState : uint32_t {
    kSlotFree = 0,
    kSlotInitializing = 1,
    kSlotActive = 2,
    kSlotRetiring = 3
};

struct FxSlot {
    std::atomic<uint32_t> state{kSlotFree};
    std::atomic<uint32_t> version{0};
    uint32_t applied_version = 0;
    int32_t priority = 0;
    uint64_t seq = 0;
    ta_fx_params pending_params{};
    DspEffectNode dsp;
};

struct MixerSlot {
    std::atomic<uint32_t> state{kSlotFree};
    std::atomic<bool> is_global_root{false};
    std::atomic<uint32_t> flush_counter{0};
    uint32_t applied_flush = 0;
    FxSlot fx[kMaxFxPerMixer];
    AnalysisRing analysis;
    float mix_buf[kMaxRenderFrames * 2]{};
    std::atomic<uint32_t> fx_order_version{0};
    int32_t sorted_fx_indices[kMaxFxPerMixer]{};
    int32_t sorted_fx_count = 0;
    uint32_t applied_fx_order_version = 0;
};

struct SampleAsset {
    std::atomic<uint32_t> state{kSlotFree};
    std::atomic<int32_t> ref_count{0};
    std::atomic<int32_t> max_concurrency{2};
    int32_t sample_rate = 44100;
    int32_t channels = 2;
    int64_t total_frames = 0;
    double length_ms = 0.0;
    float* pcm_stereo = nullptr; // Pre-decoded interleaved stereo float32
    size_t pcm_bytes = 0;
};

struct SampleChannelSlot {
    std::atomic<uint32_t> state{kSlotFree};
    std::atomic<int32_t> sample_idx{-1};
    std::atomic<int32_t> mixer_handle{0};

    // Control params written by AudioThread, read lock-free by RT callback
    std::atomic<float> target_volume{1.0f};
    std::atomic<float> target_balance{0.0f};
    std::atomic<double> target_frequency{1.0};
    std::atomic<int32_t> looping{0};

    // Commands
    std::atomic<uint32_t> play_seq{0};
    std::atomic<uint32_t> stop_seq{0};
    uint32_t applied_play_seq = 0;
    uint32_t applied_stop_seq = 0;

    // RT voice state
    std::atomic<int32_t> rt_state{TA_STATE_STOPPED};
    std::atomic<int32_t> logical_state{TA_STATE_STOPPED};
    std::atomic<int32_t> concurrency_claimed{0};
    std::atomic<uint64_t> start_order{0};
    double phase = 0.0;
    float cur_gain_l = 0.0f;
    float cur_gain_r = 0.0f;
    int32_t fade_out_samples = 0; // 64-sample click-free fade on stop/steal
    bool fading_out = false;
    bool paused_by_zero_freq = false;

    // Optional per-channel analysis tap (for IntroWelcome visualiser)
    AnalysisRing analysis;
    float voice_buf[kMaxRenderFrames * 2]{};
};

struct TrackSlot {
    std::atomic<uint32_t> state{kSlotFree};
    std::atomic<int32_t> mixer_handle{0};

    // Compressed media owned in memory (zero managed callbacks on RT thread!)
    uint8_t* raw_data = nullptr;
    size_t raw_size = 0;
    ma_decoder decoder{};
    bool decoder_valid = false;
    int32_t format = TA_FORMAT_UNKNOWN;
    int32_t src_sample_rate = 44100;
    int32_t src_channels = 2;
    int32_t bitrate_kbps = 0;
    int64_t total_frames = 0;
    double length_ms = 0.0;
    double last_seekable_ms = 0.0;

    // Control parameters (atomic, lock-free)
    std::atomic<float> target_volume{1.0f};
    std::atomic<float> target_balance{0.0f};
    std::atomic<double> target_frequency{1.0};
    std::atomic<double> target_tempo{1.0};
    std::atomic<int32_t> looping{0};
    std::atomic<double> restart_point_ms{0.0};

    // Play / Pause / Seek commands
    std::atomic<int32_t> desired_playing{0};
    std::atomic<uint32_t> seek_seq{0};
    std::atomic<double> seek_target_ms{0.0};
    uint32_t applied_seek_seq = 0;

    // Published clock snapshot (atomic, zero-jitter)
    std::atomic<double> published_time_ms{0.0};
    std::atomic<int32_t> published_playing{0};
    std::atomic<int32_t> published_completed{0};
    std::atomic<uint32_t> loop_count{0};
    std::atomic<uint32_t> end_event_count{0};

    // RT-only playback & DSP state (never touched outside RT callback while active)
    double src_cursor_frame = 0.0;     // Exact source PCM frame position
    int64_t decoder_frame_pos = 0;     // Where ma_decoder currently sits
    bool has_completed = false;
    float cur_gain_l = 1.0f;
    float cur_gain_r = 1.0f;

    // Pre-allocated decode/resample/stretch scratch buffers (zero RT allocs!)
    static constexpr size_t kDecBufFrames = 8192;
    float dec_buf[kDecBufFrames * 2]{};
    int64_t dec_buf_start_frame = -1;
    int32_t dec_buf_valid_frames = 0;

    // Signalsmith Stretch (pre-allocated at track creation, bypassed when tempo == 1.0)
    signalsmith::stretch::SignalsmithStretch<float> stretcher;
    int32_t stretcher_sr = 0;
    bool stretcher_active = false;
    bool stretcher_primed = false;
    double stretcher_frac_accum = 0.0;
    double stretcher_read_frame = 0.0; // Source frame cursor fed into stretcher (leads audible src_cursor_frame by surplusInput)
    double stretcher_last_dir = 1.0;
    float stretch_in_l[kMaxRenderFrames * 4]{};
    float stretch_in_r[kMaxRenderFrames * 4]{};
    float stretch_out_l[kMaxRenderFrames]{};
    float stretch_out_r[kMaxRenderFrames]{};

    float track_buf[kMaxRenderFrames * 2]{};
    AnalysisRing analysis;

    void resetDecoderCache() noexcept {
        dec_buf_start_frame = -1;
        dec_buf_valid_frames = 0;
    }

    bool ensureFramesDecoded(int64_t start_frame, int32_t count) noexcept {
        if (!decoder_valid || total_frames <= 0 || count <= 0)
            return false;
        if (start_frame < 0)
            start_frame = 0;
        if (start_frame >= total_frames)
            return false;

        if (dec_buf_start_frame >= 0 &&
            start_frame >= dec_buf_start_frame &&
            start_frame + count <= dec_buf_start_frame + dec_buf_valid_frames) {
            return true;
        }

        // Keep 4 frames of lookbehind for 4-point Hermite interpolation
        int64_t fetch_start = std::max<int64_t>(0, start_frame - 4);
        int32_t to_read = static_cast<int32_t>(std::min<int64_t>(kDecBufFrames, total_frames - fetch_start));
        if (to_read <= 0)
            return false;

        if (decoder_frame_pos != fetch_start) {
            if (ma_decoder_seek_to_pcm_frame(&decoder, static_cast<ma_uint64>(fetch_start)) != MA_SUCCESS) {
                return false;
            }
            decoder_frame_pos = fetch_start;
        }

        ma_uint64 actually_read = 0;
        ma_result r = ma_decoder_read_pcm_frames(&decoder, dec_buf, static_cast<ma_uint64>(to_read), &actually_read);
        if (r != MA_SUCCESS && actually_read == 0) {
            return false;
        }
        decoder_frame_pos += static_cast<int64_t>(actually_read);
        dec_buf_start_frame = fetch_start;
        dec_buf_valid_frames = static_cast<int32_t>(actually_read);
        return dec_buf_valid_frames > 0;
    }

    inline void sampleSourceHermite(double frame_pos, float& outL, float& outR) noexcept {
        if (total_frames <= 0) {
            outL = outR = 0.0f;
            return;
        }
        double clamped = std::clamp(frame_pos, 0.0, static_cast<double>(total_frames - 1));
        int64_t i1 = static_cast<int64_t>(std::floor(clamped));
        float frac = static_cast<float>(clamped - static_cast<double>(i1));

        int64_t i0 = std::max<int64_t>(0, i1 - 1);
        int64_t i2 = std::min<int64_t>(total_frames - 1, i1 + 1);
        int64_t i3 = std::min<int64_t>(total_frames - 1, i1 + 2);

        if (!ensureFramesDecoded(i0, static_cast<int32_t>(i3 - i0 + 1))) {
            outL = outR = 0.0f;
            return;
        }

        auto at = [&](int64_t idx, int ch) -> float {
            int64_t rel = idx - dec_buf_start_frame;
            if (rel < 0) rel = 0;
            if (rel >= dec_buf_valid_frames) rel = dec_buf_valid_frames - 1;
            return dec_buf[rel * 2 + ch];
        };

        outL = hermite4(at(i0, 0), at(i1, 0), at(i2, 0), at(i3, 0), frac);
        outR = hermite4(at(i0, 1), at(i1, 1), at(i2, 1), at(i3, 1), frac);
    }

    void primeStretcher(int32_t dev_sr, double rate, double pitch) noexcept {
        if (stretcher_sr != dev_sr) {
            stretcher.presetDefault(2, static_cast<float>(dev_sr), true);
            stretcher_sr = dev_sr;
        }
        stretcher.setTransposeFactor(static_cast<float>(std::clamp(pitch, 0.1, 4.0)));

        const double sr_ratio = static_cast<double>(src_sample_rate) / static_cast<double>(dev_sr);
        const double dir = rate < 0.0 ? -1.0 : 1.0;
        const double abs_r = std::max(0.05, std::fabs(rate));

        const int input_lat = stretcher.inputLatency();
        const int seek_len = std::min<int>(stretcher.outputSeekLength(static_cast<float>(abs_r)), static_cast<int>(kMaxRenderFrames * 4));
        const int surplus = std::max(0, seek_len - input_lat);

        if (seek_len > 0 && total_frames > 0) {
            // inputs[0..input_lat-1] = pre-roll ending at src_cursor_frame
            // inputs[input_lat..seek_len-1] = surplus audio starting at src_cursor_frame
            double start_frame = src_cursor_frame - dir * static_cast<double>(input_lat) * sr_ratio;
            for (int i = 0; i < seek_len; ++i) {
                double p = start_frame + dir * static_cast<double>(i) * sr_ratio;
                if (p < 0.0 || p >= static_cast<double>(total_frames)) {
                    stretch_in_l[i] = 0.0f;
                    stretch_in_r[i] = 0.0f;
                } else {
                    sampleSourceHermite(p, stretch_in_l[i], stretch_in_r[i]);
                }
            }
            float* in_ptrs[2] = { stretch_in_l, stretch_in_r };
            stretcher.outputSeek(in_ptrs, seek_len);
        } else {
            stretcher.reset();
        }

        // Next process() output sample 0 is aligned to EXACTLY src_cursor_frame (0.00ms offset)!
        // Next process() input sample 0 starts right after the surplus frames already consumed.
        stretcher_read_frame = src_cursor_frame + dir * static_cast<double>(surplus) * sr_ratio;
        stretcher_last_dir = dir;
        stretcher_primed = true;
        stretcher_frac_accum = 0.0;
    }
};

} // namespace torii_audio

struct ta_engine {
    ma_context context{};
    bool context_valid = false;

    ma_device device{};
    bool device_valid = false;
    bool nosound_active = false;
    std::atomic<bool> nosound_stop{false};
    std::thread nosound_thread;

    std::atomic<int32_t> active_sample_rate{44100};
    std::atomic<int32_t> active_channels{2};
    std::atomic<int32_t> active_mode{TA_MODE_NO_SOUND};
    std::atomic<int32_t> device_started{0};
    std::atomic<int32_t> device_lost{0};
    std::atomic<int32_t> last_error{TA_OK};
    std::atomic<double> active_period_ms{5.0};
    std::atomic<double> measured_latency_ms{0.0};
    std::atomic<float> cpu_usage_pct{0.0f};
    std::atomic<uint32_t> rt_callback_active{0};

    // Cached device list for enumeration
    std::mutex ctrl_mutex;
    std::vector<ma_device_info> cached_devices;
    int32_t current_device_index = 0;
    double current_exclusive_period_ms = 0.0;
    int32_t current_preferred_sr = 0;

    // Fixed-size lock-free audio graph pools
    torii_audio::MixerSlot mixers[torii_audio::kMaxMixers];
    torii_audio::TrackSlot tracks[torii_audio::kMaxTracks];
    torii_audio::SampleAsset samples[torii_audio::kMaxSamples];
    torii_audio::SampleChannelSlot sample_channels[torii_audio::kMaxSampleChannels];

    std::atomic<uint64_t> voice_order_counter{1};
    std::atomic<uint64_t> fx_seq_counter{1};
    int32_t default_root_mixer_handle = 0;

    float master_buf[torii_audio::kMaxRenderFrames * 2]{};
};

namespace torii_audio {

static void waitRtQuiescent(ta_engine* eng) noexcept {
    // Wait briefly if RT callback is currently in the middle of a render quantum (~0.1-2ms)
    for (int i = 0; i < 200; ++i) {
        if (eng->rt_callback_active.load(std::memory_order_acquire) == 0)
            return;
        std::this_thread::sleep_for(std::chrono::microseconds(250));
    }
}

static void renderTrack(TrackSlot& trk, float* out_stereo, uint32_t frames, int32_t dev_sr) noexcept {
    // Process pending seek immediately
    uint32_t sseq = trk.seek_seq.load(std::memory_order_acquire);
    if (sseq != trk.applied_seek_seq) {
        double target_ms = trk.seek_target_ms.load(std::memory_order_relaxed);
        double target_frame = std::clamp(target_ms * 0.001 * trk.src_sample_rate, 0.0, static_cast<double>(std::max<int64_t>(0, trk.total_frames - 1)));
        trk.src_cursor_frame = target_frame;
        if (target_ms < trk.length_ms)
            trk.has_completed = false;
        trk.stretcher_primed = false;
        trk.analysis.clear();
        trk.applied_seek_seq = sseq;
        trk.published_time_ms.store(target_ms, std::memory_order_release);
        trk.published_completed.store(trk.has_completed ? 1 : 0, std::memory_order_release);
    }

    const bool want_play = trk.desired_playing.load(std::memory_order_acquire) != 0;
    const double freq = trk.target_frequency.load(std::memory_order_relaxed);
    const double tempo = std::max(0.05, trk.target_tempo.load(std::memory_order_relaxed));
    const bool looping = trk.looping.load(std::memory_order_relaxed) != 0;
    const double restart_ms = trk.restart_point_ms.load(std::memory_order_relaxed);
    const float vol = std::max(0.0f, trk.target_volume.load(std::memory_order_relaxed));
    const float bal = trk.target_balance.load(std::memory_order_relaxed);

    if (!want_play || trk.has_completed || !trk.decoder_valid || trk.total_frames <= 0) {
        trk.published_playing.store(0, std::memory_order_release);
        return;
    }

    // Zero-frequency virtual pause: keep logical playing = 1, emit silence, hold cursor
    if (std::fabs(freq) < 1e-6) {
        trk.published_playing.store(1, std::memory_order_release);
        return;
    }

    const double signed_rate = freq * tempo;
    const double abs_rate = std::fabs(signed_rate);
    const double dir = signed_rate < 0.0 ? -1.0 : 1.0;
    const double sr_ratio = static_cast<double>(trk.src_sample_rate) / static_cast<double>(dev_sr);

    // Activate Signalsmith Stretch ONLY when tempo != 1.0 (pitch-preserving time-stretch).
    // For normal 1.0x and classic Nightcore/Daycore/AdjustPitch (tempo == 1.0), pure Hermite
    // varispeed gives ZERO algorithmic latency and crystal-clear transients!
    const bool need_stretch = std::fabs(tempo - 1.0) >= 1e-5;
    if (need_stretch)
        trk.stretcher_active = true;
    else if (!trk.stretcher_primed)
        trk.stretcher_active = false;

    std::memset(trk.track_buf, 0, frames * 2 * sizeof(float));

    auto handleBoundary = [&]() -> bool {
        if (dir > 0.0 && trk.src_cursor_frame >= static_cast<double>(trk.total_frames)) {
            if (looping) {
                double rp_frame = std::clamp(restart_ms * 0.001 * trk.src_sample_rate, 0.0, static_cast<double>(std::max<int64_t>(0, trk.total_frames - 1)));
                trk.src_cursor_frame = rp_frame;
                trk.stretcher_primed = false;
                trk.loop_count.fetch_add(1, std::memory_order_release);
                return true;
            } else {
                trk.src_cursor_frame = static_cast<double>(trk.total_frames);
                trk.has_completed = true;
                trk.desired_playing.store(0, std::memory_order_release);
                trk.published_completed.store(1, std::memory_order_release);
                trk.published_playing.store(0, std::memory_order_release);
                trk.end_event_count.fetch_add(1, std::memory_order_release);
                return false;
            }
        } else if (dir < 0.0 && trk.src_cursor_frame < 0.0) {
            if (looping) {
                trk.src_cursor_frame = static_cast<double>(std::max<int64_t>(0, trk.total_frames - 1));
                trk.stretcher_primed = false;
                trk.loop_count.fetch_add(1, std::memory_order_release);
                return true;
            } else {
                trk.src_cursor_frame = 0.0;
                trk.has_completed = true;
                trk.desired_playing.store(0, std::memory_order_release);
                trk.published_completed.store(1, std::memory_order_release);
                trk.published_playing.store(0, std::memory_order_release);
                trk.end_event_count.fetch_add(1, std::memory_order_release);
                return false;
            }
        }
        return true;
    };

    if (!trk.stretcher_active) {
        // ZERO-LATENCY PATH: Direct 1:1 copy or 4-point Hermite varispeed
        const double step = dir * abs_rate * sr_ratio;
        for (uint32_t i = 0; i < frames; ++i) {
            if (!handleBoundary())
                break;
            float sL = 0.0f, sR = 0.0f;
            trk.sampleSourceHermite(trk.src_cursor_frame, sL, sR);
            trk.track_buf[i * 2 + 0] = sL;
            trk.track_buf[i * 2 + 1] = sR;
            trk.src_cursor_frame += step;
        }
    } else {
        // SIGNALSMITH STRETCH PATH (pitch-preserving DT / HT / Rate / PitchAdjust / Editor slow-mo)
        if (!trk.stretcher_primed || trk.stretcher_sr != dev_sr || trk.stretcher_last_dir != dir) {
            trk.primeStretcher(dev_sr, signed_rate, std::fabs(freq));
        } else {
            trk.stretcher.setTransposeFactor(static_cast<float>(std::clamp(std::fabs(freq), 0.1, 4.0)));
        }

        // Exact fractional input frame accounting so tempo clock has 0.0ms drift over 10-minute maps!
        double exact_in = static_cast<double>(frames) * abs_rate + trk.stretcher_frac_accum;
        int32_t in_frames = static_cast<int32_t>(std::floor(exact_in));
        trk.stretcher_frac_accum = exact_in - static_cast<double>(in_frames);
        in_frames = std::clamp(in_frames, 1, static_cast<int32_t>(kMaxRenderFrames * 4));

        for (int32_t i = 0; i < in_frames; ++i) {
            if (!handleBoundary()) {
                trk.stretch_in_l[i] = 0.0f;
                trk.stretch_in_r[i] = 0.0f;
                continue;
            }
            double rp = trk.stretcher_read_frame;
            if (rp < 0.0 || rp >= static_cast<double>(trk.total_frames)) {
                trk.stretch_in_l[i] = 0.0f;
                trk.stretch_in_r[i] = 0.0f;
            } else {
                trk.sampleSourceHermite(rp, trk.stretch_in_l[i], trk.stretch_in_r[i]);
            }
            trk.stretcher_read_frame += dir * sr_ratio;
            trk.src_cursor_frame += dir * sr_ratio;
        }

        float* in_ptrs[2] = { trk.stretch_in_l, trk.stretch_in_r };
        float* out_ptrs[2] = { trk.stretch_out_l, trk.stretch_out_r };
        trk.stretcher.process(in_ptrs, in_frames, out_ptrs, static_cast<int>(frames));

        for (uint32_t i = 0; i < frames; ++i) {
            trk.track_buf[i * 2 + 0] = trk.stretch_out_l[i];
            trk.track_buf[i * 2 + 1] = trk.stretch_out_r[i];
        }
    }

    // Tap pre-volume post-DSP audio into lock-free analysis ring (matches BASSmix MixerChanBuffer)
    trk.analysis.pushInterleaved(trk.track_buf, frames, dev_sr);

    // Apply smoothed volume + linear stereo pan and sum into target mixer bus
    float panL = 1.0f, panR = 1.0f;
    computePanGains(bal, panL, panR);
    const float targetL = vol * panL;
    const float targetR = vol * panR;
    const float smooth = 1.0f - std::exp(-1.0f / std::max(1.0f, 0.002f * dev_sr));

    for (uint32_t i = 0; i < frames; ++i) {
        trk.cur_gain_l += (targetL - trk.cur_gain_l) * smooth;
        trk.cur_gain_r += (targetR - trk.cur_gain_r) * smooth;
        out_stereo[i * 2 + 0] += trk.track_buf[i * 2 + 0] * trk.cur_gain_l;
        out_stereo[i * 2 + 1] += trk.track_buf[i * 2 + 1] * trk.cur_gain_r;
    }

    // Publish exact audible CurrentTime (ms):
    // Because primeStretcher() uses outputSeek() to pre-align output sample 0 to src_cursor_frame,
    // src_cursor_frame IS the exact audible playback position with 0.00ms offset on BOTH 1.0x and DT/HT/Rate!
    // Use exact same (frame * 1000.0) / sr expression as length_ms so EOF is bit-for-bit identical (0 ULP).
    double reported_ms = trk.has_completed
        ? trk.length_ms
        : std::clamp((trk.src_cursor_frame * 1000.0) / static_cast<double>(trk.src_sample_rate), 0.0, trk.length_ms);
    trk.published_time_ms.store(reported_ms, std::memory_order_release);
    trk.published_playing.store(trk.has_completed ? 0 : 1, std::memory_order_release);
}

static void renderSampleChannel(ta_engine* eng, SampleChannelSlot& ch, float* out_stereo, uint32_t frames, int32_t dev_sr) noexcept {
    // Apply pending play / stop commands
    uint32_t pseq = ch.play_seq.load(std::memory_order_acquire);
    if (pseq != ch.applied_play_seq) {
        ch.applied_play_seq = pseq;
        ch.phase = 0.0;
        ch.fading_out = false;
        ch.fade_out_samples = 0;
        ch.paused_by_zero_freq = false;
        ch.rt_state.store(TA_STATE_PLAYING, std::memory_order_release);
    }

    uint32_t sseq = ch.stop_seq.load(std::memory_order_acquire);
    if (sseq != ch.applied_stop_seq) {
        ch.applied_stop_seq = sseq;
        ch.concurrency_claimed.store(0, std::memory_order_release);
        if (ch.rt_state.load(std::memory_order_relaxed) == TA_STATE_PLAYING) {
            ch.fading_out = true;
            ch.fade_out_samples = 64; // ~1.3ms click-free fade-out
        } else {
            ch.rt_state.store(TA_STATE_STOPPED, std::memory_order_release);
        }
    }

    if (ch.rt_state.load(std::memory_order_relaxed) != TA_STATE_PLAYING)
        return;

    int32_t sidx = ch.sample_idx.load(std::memory_order_acquire);
    if (sidx < 0 || sidx >= kMaxSamples) {
        ch.rt_state.store(TA_STATE_STOPPED, std::memory_order_release);
        return;
    }

    SampleAsset& asset = eng->samples[sidx];
    if (asset.state.load(std::memory_order_acquire) != kSlotActive || !asset.pcm_stereo || asset.total_frames <= 0) {
        ch.rt_state.store(TA_STATE_STOPPED, std::memory_order_release);
        return;
    }

    const double freq = ch.target_frequency.load(std::memory_order_relaxed);
    if (std::fabs(freq) < 1e-6) {
        ch.paused_by_zero_freq = true;
        return;
    }
    ch.paused_by_zero_freq = false;

    const bool looping = ch.looping.load(std::memory_order_relaxed) != 0;
    const float vol = std::max(0.0f, ch.target_volume.load(std::memory_order_relaxed));
    const float bal = ch.target_balance.load(std::memory_order_relaxed);
    float panL = 1.0f, panR = 1.0f;
    computePanGains(bal, panL, panR);
    const float gL = vol * panL;
    const float gR = vol * panR;

    const double step = std::fabs(freq) * (static_cast<double>(asset.sample_rate) / static_cast<double>(dev_sr));
    const int64_t total = asset.total_frames;
    const float* pcm = asset.pcm_stereo;

    uint32_t rendered = 0;
    for (uint32_t i = 0; i < frames; ++i) {
        if (ch.phase >= static_cast<double>(total)) {
            if (looping && !ch.fading_out) {
                ch.phase = std::fmod(ch.phase, static_cast<double>(total));
            } else {
                ch.concurrency_claimed.store(0, std::memory_order_release);
                ch.logical_state.store(TA_STATE_STOPPED, std::memory_order_release);
                ch.rt_state.store(TA_STATE_STOPPED, std::memory_order_release);
                break;
            }
        }

        int64_t idx0 = static_cast<int64_t>(ch.phase);
        int64_t idx1 = idx0 + 1;
        if (idx1 >= total)
            idx1 = looping ? 0 : (total - 1);
        float frac = static_cast<float>(ch.phase - static_cast<double>(idx0));

        float sL = pcm[idx0 * 2 + 0] + (pcm[idx1 * 2 + 0] - pcm[idx0 * 2 + 0]) * frac;
        float sR = pcm[idx0 * 2 + 1] + (pcm[idx1 * 2 + 1] - pcm[idx0 * 2 + 1]) * frac;

        float fade = 1.0f;
        if (ch.fading_out) {
            fade = static_cast<float>(ch.fade_out_samples) / 64.0f;
            if (--ch.fade_out_samples <= 0) {
                ch.fading_out = false;
                ch.rt_state.store(TA_STATE_STOPPED, std::memory_order_release);
                ch.voice_buf[i * 2 + 0] = sL * fade;
                ch.voice_buf[i * 2 + 1] = sR * fade;
                out_stereo[i * 2 + 0] += sL * gL * fade;
                out_stereo[i * 2 + 1] += sR * gR * fade;
                rendered = i + 1;
                break;
            }
        }

        ch.voice_buf[i * 2 + 0] = sL * fade;
        ch.voice_buf[i * 2 + 1] = sR * fade;
        out_stereo[i * 2 + 0] += sL * gL * fade;
        out_stereo[i * 2 + 1] += sR * gR * fade;
        ch.phase += step;
        rendered = i + 1;
    }

    if (rendered > 0)
        ch.analysis.pushInterleaved(ch.voice_buf, rendered, dev_sr);
}

static void renderEngineBlock(ta_engine* eng, float* out_interleaved, uint32_t total_frames, int32_t out_channels) noexcept {
    auto t0 = std::chrono::steady_clock::now();
    eng->rt_callback_active.fetch_add(1, std::memory_order_acq_rel);

    const int32_t dev_sr = std::max(8000, eng->active_sample_rate.load(std::memory_order_relaxed));
    uint32_t offset = 0;

    while (offset < total_frames) {
        uint32_t frames = std::min<uint32_t>(total_frames - offset, kMaxRenderFrames);
        std::memset(eng->master_buf, 0, frames * 2 * sizeof(float));

        // 1. Clear active sub-mixer buffers
        for (int32_t m = 0; m < kMaxMixers; ++m) {
            if (eng->mixers[m].state.load(std::memory_order_acquire) == kSlotActive) {
                std::memset(eng->mixers[m].mix_buf, 0, frames * 2 * sizeof(float));
                uint32_t fcnt = eng->mixers[m].flush_counter.load(std::memory_order_acquire);
                if (fcnt != eng->mixers[m].applied_flush) {
                    eng->mixers[m].applied_flush = fcnt;
                    eng->mixers[m].analysis.clear();
                }
            }
        }

        // 2. Render active tracks into their assigned sub-mixer (or master directly)
        for (int32_t t = 0; t < kMaxTracks; ++t) {
            TrackSlot& trk = eng->tracks[t];
            if (trk.state.load(std::memory_order_acquire) != kSlotActive)
                continue;

            int32_t mh = trk.mixer_handle.load(std::memory_order_acquire);
            float* dst = eng->master_buf;
            if (mh != 0 && handleTag(mh) == kTagMixer) {
                int32_t midx = handleIndex(mh);
                if (midx >= 0 && midx < kMaxMixers && eng->mixers[midx].state.load(std::memory_order_acquire) == kSlotActive)
                    dst = eng->mixers[midx].mix_buf;
            }
            renderTrack(trk, dst, frames, dev_sr);
        }

        // 3. Render active sample channels (hitsounds!) into their assigned sub-mixer
        for (int32_t c = 0; c < kMaxSampleChannels; ++c) {
            SampleChannelSlot& ch = eng->sample_channels[c];
            if (ch.state.load(std::memory_order_acquire) != kSlotActive)
                continue;

            int32_t mh = ch.mixer_handle.load(std::memory_order_acquire);
            float* dst = eng->master_buf;
            if (mh != 0 && handleTag(mh) == kTagMixer) {
                int32_t midx = handleIndex(mh);
                if (midx >= 0 && midx < kMaxMixers && eng->mixers[midx].state.load(std::memory_order_acquire) == kSlotActive)
                    dst = eng->mixers[midx].mix_buf;
            }
            renderSampleChannel(eng, ch, dst, frames, dev_sr);
        }

        // 4. Process DSP effects on each non-root sub-mixer, tap levels, and sum into master
        for (int32_t m = 0; m < kMaxMixers; ++m) {
            MixerSlot& mx = eng->mixers[m];
            if (mx.state.load(std::memory_order_acquire) != kSlotActive)
                continue;
            if (mx.is_global_root.load(std::memory_order_relaxed))
                continue;

            // Rebuild sorted FX order if changed (bounded O(16 log 16), zero allocs)
            uint32_t ov = mx.fx_order_version.load(std::memory_order_acquire);
            if (ov != mx.applied_fx_order_version) {
                int32_t count = 0;
                for (int32_t f = 0; f < kMaxFxPerMixer; ++f) {
                    if (mx.fx[f].state.load(std::memory_order_acquire) == kSlotActive)
                        mx.sorted_fx_indices[count++] = f;
                }
                for (int32_t i = 1; i < count; ++i) {
                    int32_t key = mx.sorted_fx_indices[i];
                    int32_t j = i - 1;
                    while (j >= 0) {
                        int32_t prev = mx.sorted_fx_indices[j];
                        bool greater = (mx.fx[prev].priority > mx.fx[key].priority)
                            || (mx.fx[prev].priority == mx.fx[key].priority && mx.fx[prev].seq > mx.fx[key].seq);
                        if (!greater) break;
                        mx.sorted_fx_indices[j + 1] = prev;
                        --j;
                    }
                    mx.sorted_fx_indices[j + 1] = key;
                }
                mx.sorted_fx_count = count;
                mx.applied_fx_order_version = ov;
            }

            for (int32_t i = 0; i < mx.sorted_fx_count; ++i) {
                FxSlot& fxs = mx.fx[mx.sorted_fx_indices[i]];
                if (fxs.state.load(std::memory_order_acquire) != kSlotActive)
                    continue;
                uint32_t v = fxs.version.load(std::memory_order_acquire);
                if (v != fxs.applied_version) {
                    fxs.dsp.params = fxs.pending_params;
                    fxs.dsp.updateTargets(dev_sr);
                    fxs.applied_version = v;
                }
                fxs.dsp.processStereo(mx.mix_buf, frames, dev_sr);
            }

            mx.analysis.pushInterleaved(mx.mix_buf, frames, dev_sr);

            for (uint32_t i = 0; i < frames * 2; ++i)
                eng->master_buf[i] += mx.mix_buf[i];
        }

        // 5. Tap root mixer analysis if present & soft-limit master output
        for (int32_t m = 0; m < kMaxMixers; ++m) {
            MixerSlot& mx = eng->mixers[m];
            if (mx.state.load(std::memory_order_acquire) == kSlotActive && mx.is_global_root.load(std::memory_order_relaxed)) {
                mx.analysis.pushInterleaved(eng->master_buf, frames, dev_sr);
            }
        }

        if (out_interleaved) {
            float* dst = out_interleaved + offset * out_channels;
            for (uint32_t i = 0; i < frames; ++i) {
                float sL = softLimitSample(eng->master_buf[i * 2 + 0]);
                float sR = softLimitSample(eng->master_buf[i * 2 + 1]);
                if (out_channels == 2) {
                    dst[i * 2 + 0] = sL;
                    dst[i * 2 + 1] = sR;
                } else if (out_channels == 1) {
                    dst[i] = 0.5f * (sL + sR);
                } else {
                    for (int32_t c = 0; c < out_channels; ++c)
                        dst[i * out_channels + c] = (c == 0) ? sL : (c == 1 ? sR : 0.0f);
                }
            }
        }

        offset += frames;
    }

    eng->rt_callback_active.fetch_sub(1, std::memory_order_acq_rel);

    // Update DSP CPU usage rolling EMA
    auto t1 = std::chrono::steady_clock::now();
    double elapsed_us = std::chrono::duration<double, std::micro>(t1 - t0).count();
    double budget_us = (static_cast<double>(total_frames) / static_cast<double>(dev_sr)) * 1e6;
    if (budget_us > 0.0) {
        float inst_pct = static_cast<float>(std::clamp((elapsed_us / budget_us) * 100.0, 0.0, 100.0));
        float prev = eng->cpu_usage_pct.load(std::memory_order_relaxed);
        eng->cpu_usage_pct.store(prev * 0.9f + inst_pct * 0.1f, std::memory_order_relaxed);
    }
}

static void maDataCallback(ma_device* pDevice, void* pOutput, const void* /*pInput*/, ma_uint32 frameCount) {
    ta_engine* eng = static_cast<ta_engine*>(pDevice->pUserData);
    if (!eng || !pOutput)
        return;

    // miniaudio converts from playback.format (f32) to device's native internalFormat (f32/s32/s24/s16 + TPDF dither)!
    renderEngineBlock(eng, static_cast<float*>(pOutput), frameCount, static_cast<int32_t>(pDevice->playback.channels));
}

static void maNotificationCallback(const ma_device_notification* pNotification) {
    if (!pNotification || !pNotification->pDevice)
        return;
    ta_engine* eng = static_cast<ta_engine*>(pNotification->pDevice->pUserData);
    if (!eng)
        return;

    switch (pNotification->type) {
        case ma_device_notification_type_stopped:
        case ma_device_notification_type_rerouted:
        case ma_device_notification_type_interruption_began:
            eng->device_lost.store(1, std::memory_order_release);
            break;
        case ma_device_notification_type_started:
        case ma_device_notification_type_interruption_ended:
            eng->device_lost.store(0, std::memory_order_release);
            break;
        default:
            break;
    }
}

static void stopNoSoundPump(ta_engine* eng) {
    if (eng->nosound_active) {
        eng->nosound_stop.store(true, std::memory_order_release);
        if (eng->nosound_thread.joinable())
            eng->nosound_thread.join();
        eng->nosound_active = false;
    }
}

static void startNoSoundPump(ta_engine* eng) {
    stopNoSoundPump(eng);
    eng->nosound_stop.store(false, std::memory_order_release);
    eng->active_sample_rate.store(44100, std::memory_order_release);
    eng->active_channels.store(2, std::memory_order_release);
    eng->active_mode.store(TA_MODE_NO_SOUND, std::memory_order_release);
    eng->active_period_ms.store(5.8, std::memory_order_release);
    eng->measured_latency_ms.store(0.0, std::memory_order_release);
    eng->device_lost.store(0, std::memory_order_release);
    eng->device_started.store(1, std::memory_order_release);
    eng->nosound_active = true;

    eng->nosound_thread = std::thread([eng]() {
        constexpr uint32_t kQuantum = 256; // ~5.805 ms @ 44100 Hz
        const auto period = std::chrono::microseconds(5805);
        auto next_tick = std::chrono::steady_clock::now();
        while (!eng->nosound_stop.load(std::memory_order_acquire)) {
            if (eng->device_started.load(std::memory_order_acquire)) {
                renderEngineBlock(eng, nullptr, kQuantum, 2);
            }
            next_tick += period;
            auto now = std::chrono::steady_clock::now();
            if (next_tick < now - std::chrono::milliseconds(50))
                next_tick = now;
            std::this_thread::sleep_until(next_tick);
        }
    });
}

#if defined(_WIN32)
// Query Windows WASAPI device minimum & default update period (in ms) and mix rate directly via IMMDeviceEnumerator
static void queryWasapiPeriods(const ma_device_id* dev_id, double& out_min_ms, double& out_def_ms, int32_t& out_mix_rate) {
    out_min_ms = 3.0;
    out_def_ms = 10.0;
    out_mix_rate = 48000;

    HRESULT hrInit = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool shouldUninit = SUCCEEDED(hrInit);

    IMMDeviceEnumerator* enumerator = nullptr;
    if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator))) && enumerator) {
        IMMDevice* device = nullptr;
        HRESULT hrDev = E_FAIL;
        if (dev_id && dev_id->wasapi[0] != 0) {
            hrDev = enumerator->GetDevice(dev_id->wasapi, &device);
        } else {
            hrDev = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
        }

        if (SUCCEEDED(hrDev) && device) {
            IAudioClient* client = nullptr;
            if (SUCCEEDED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client))) && client) {
                REFERENCE_TIME defPeriod = 0, minPeriod = 0;
                if (SUCCEEDED(client->GetDevicePeriod(&defPeriod, &minPeriod))) {
                    if (minPeriod > 0) out_min_ms = static_cast<double>(minPeriod) / 10000.0;
                    if (defPeriod > 0) out_def_ms = static_cast<double>(defPeriod) / 10000.0;
                }
                WAVEFORMATEX* mixFmt = nullptr;
                if (SUCCEEDED(client->GetMixFormat(&mixFmt)) && mixFmt) {
                    if (mixFmt->nSamplesPerSec > 0)
                        out_mix_rate = static_cast<int32_t>(mixFmt->nSamplesPerSec);
                    CoTaskMemFree(mixFmt);
                }
                client->Release();
            }
            device->Release();
        }
        enumerator->Release();
    }

    if (shouldUninit)
        CoUninitialize();
}
#endif

static void writeError(char* dst, int32_t cap, const char* msg) noexcept {
    if (!dst || cap <= 0)
        return;
    if (!msg) {
        dst[0] = '\0';
        return;
    }
    std::snprintf(dst, static_cast<size_t>(cap), "%s", msg);
}

static ma_encoding_format formatToMa(int32_t fmt) noexcept {
    switch (fmt) {
        case TA_FORMAT_MP3: return ma_encoding_format_mp3;
        case TA_FORMAT_OGG: return ma_encoding_format_vorbis;
        case TA_FORMAT_WAVE: return ma_encoding_format_wav;
        case TA_FORMAT_FLAC: return ma_encoding_format_flac;
        default: return ma_encoding_format_unknown;
    }
}

} // namespace torii_audio

extern "C" {

TA_API ta_engine* ta_engine_create(void) {
    ta_engine* eng = new (std::nothrow) ta_engine();
    if (!eng)
        return nullptr;

    ma_context_config ctx_cfg = ma_context_config_init();
#if defined(__APPLE__) && TARGET_OS_IPHONE
    ctx_cfg.coreaudio.sessionCategory = ma_ios_session_category_none;
#endif

    if (ma_context_init(nullptr, 0, &ctx_cfg, &eng->context) == MA_SUCCESS) {
        eng->context_valid = true;
    }
    return eng;
}

TA_API void ta_engine_destroy(ta_engine* eng) {
    if (!eng)
        return;
    ta_device_free(eng);

    for (int32_t t = 0; t < torii_audio::kMaxTracks; ++t) {
        auto& trk = eng->tracks[t];
        if (trk.decoder_valid) {
            ma_decoder_uninit(&trk.decoder);
            trk.decoder_valid = false;
        }
        std::free(trk.raw_data);
        trk.raw_data = nullptr;
    }

    for (int32_t s = 0; s < torii_audio::kMaxSamples; ++s) {
        std::free(eng->samples[s].pcm_stereo);
        eng->samples[s].pcm_stereo = nullptr;
    }

    if (eng->context_valid) {
        ma_context_uninit(&eng->context);
        eng->context_valid = false;
    }
    delete eng;
}

TA_API int32_t ta_enumerate_devices(ta_engine* eng, ta_device_info* out_devices, int32_t max_count) {
    if (!eng || !eng->context_valid)
        return 0;

    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    ma_device_info* pPlayback = nullptr;
    ma_uint32 playbackCount = 0;
    if (ma_context_get_devices(&eng->context, &pPlayback, &playbackCount, nullptr, nullptr) != MA_SUCCESS)
        return 0;

    eng->cached_devices.assign(pPlayback, pPlayback + playbackCount);

    int32_t count = static_cast<int32_t>(playbackCount);
    if (out_devices && max_count > 0) {
        int32_t n = std::min(count, max_count);
        for (int32_t i = 0; i < n; ++i) {
            out_devices[i].index = i;
            out_devices[i].is_default = pPlayback[i].isDefault ? 1 : 0;
            out_devices[i].is_enabled = 1;
            std::snprintf(out_devices[i].name, sizeof(out_devices[i].name), "%s", pPlayback[i].name);
        }
    }
    return count;
}

TA_API int32_t ta_device_init(
    ta_engine* eng,
    int32_t device_index,
    int32_t mode,
    double exclusive_period_ms,
    int32_t preferred_sample_rate,
    ta_device_caps* out_caps,
    char* out_error,
    int32_t out_error_cap) {
    if (!eng)
        return TA_ERR_NOT_INITIALIZED;

    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);

    // Tear down previous device cleanly
    torii_audio::stopNoSoundPump(eng);
    if (eng->device_valid) {
        ma_device_uninit(&eng->device);
        eng->device_valid = false;
    }
    eng->device_started.store(0, std::memory_order_release);
    eng->device_lost.store(0, std::memory_order_release);

    eng->current_device_index = device_index;
    eng->current_exclusive_period_ms = exclusive_period_ms;
    eng->current_preferred_sr = preferred_sample_rate;

    if (out_caps)
        std::memset(out_caps, 0, sizeof(*out_caps));
    torii_audio::writeError(out_error, out_error_cap, "");

    // Device 0 or TA_MODE_NO_SOUND -> software clock silent pump
    if (device_index == 0 || mode == TA_MODE_NO_SOUND || !eng->context_valid) {
        torii_audio::startNoSoundPump(eng);
        if (out_caps) {
            out_caps->sample_rate = 44100;
            out_caps->channels = 2;
            out_caps->actual_mode = TA_MODE_NO_SOUND;
            out_caps->is_float_output = 1;
            out_caps->bit_depth = 32;
            out_caps->active_period_ms = 5.8;
            out_caps->output_latency_ms = 0.0;
            std::snprintf(out_caps->backend_name, sizeof(out_caps->backend_name), "NoSound");
        }
        eng->last_error.store(TA_OK, std::memory_order_release);
        return TA_OK;
    }

    // Refresh device cache if empty
    if (eng->cached_devices.empty()) {
        ma_device_info* pPlayback = nullptr;
        ma_uint32 playbackCount = 0;
        if (ma_context_get_devices(&eng->context, &pPlayback, &playbackCount, nullptr, nullptr) == MA_SUCCESS)
            eng->cached_devices.assign(pPlayback, pPlayback + playbackCount);
    }

    const ma_device_id* target_id = nullptr;
    if (device_index >= 2) {
        int32_t phys = device_index - 2;
        if (phys >= 0 && phys < static_cast<int32_t>(eng->cached_devices.size()))
            target_id = &eng->cached_devices[phys].id;
    }

    auto makeBaseConfig = [&](ma_share_mode share, ma_performance_profile profile) -> ma_device_config {
        ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
        cfg.playback.pDeviceID = target_id;
        cfg.playback.format = ma_format_f32;
        cfg.playback.channels = 2;
        cfg.playback.shareMode = share;
        cfg.performanceProfile = profile;
        cfg.dataCallback = torii_audio::maDataCallback;
        cfg.notificationCallback = torii_audio::maNotificationCallback;
        cfg.pUserData = eng;
        cfg.periods = 2;
#if defined(__APPLE__) && TARGET_OS_IPHONE
        cfg.coreaudio.noAudioSessionActivate = MA_TRUE;
        cfg.coreaudio.noAudioSessionDeactivate = MA_TRUE;
#endif
#if defined(__ANDROID__)
        cfg.aaudio.usage = ma_aaudio_usage_game;
        cfg.aaudio.contentType = ma_aaudio_content_type_music;
#endif
        return cfg;
    };

    ma_result last_ma_res = MA_ERROR;
    int32_t actual_mode = mode;

    if (mode == TA_MODE_EXCLUSIVE) {
#if defined(_WIN32)
        double min_ms = 3.0, def_ms = 10.0;
        int32_t mix_rate = 48000;
        torii_audio::queryWasapiPeriods(target_id, min_ms, def_ms, mix_rate);

        std::vector<double> periods;
        if (exclusive_period_ms > 0.0)
            periods.push_back(exclusive_period_ms);
        if (min_ms > 0.0)
            periods.push_back(min_ms);
        if (def_ms > 0.0 && std::fabs(def_ms - min_ms) > 0.25)
            periods.push_back(def_ms);
        periods.push_back(0.0);

        std::vector<int32_t> rates;
        rates.push_back(mix_rate);
        for (int32_t r : {48000, 44100, 96000, 192000}) {
            if (std::find(rates.begin(), rates.end(), r) == rates.end())
                rates.push_back(r);
        }

        // Try exclusive with miniaudio's automatic float32->device format conversion + TPDF dither
        bool opened = false;
        for (double p_ms : periods) {
            for (int32_t rate : rates) {
                ma_device_config cfg = makeBaseConfig(ma_share_mode_exclusive, ma_performance_profile_low_latency);
                cfg.sampleRate = static_cast<ma_uint32>(rate);
                cfg.periodSizeInMilliseconds = static_cast<ma_uint32>(std::round(p_ms));
                cfg.periods = 2;
                cfg.wasapi.noAutoConvertSRC = MA_TRUE;
                cfg.wasapi.noDefaultQualitySRC = MA_TRUE;

                last_ma_res = ma_device_init(&eng->context, &cfg, &eng->device);
                if (last_ma_res == MA_SUCCESS) {
                    opened = true;
                    break;
                }
                // Stop immediately if device is busy or exclusive is disallowed in Windows settings!
                if (last_ma_res == MA_BUSY || last_ma_res == MA_DEVICE_ALREADY_INITIALIZED || last_ma_res == MA_ACCESS_DENIED)
                    break;
            }
            if (opened || last_ma_res == MA_BUSY || last_ma_res == MA_DEVICE_ALREADY_INITIALIZED || last_ma_res == MA_ACCESS_DENIED)
                break;
        }

        if (!opened) {
            int32_t err = TA_ERR_FORMAT_UNSUPPORTED;
            if (last_ma_res == MA_BUSY || last_ma_res == MA_DEVICE_ALREADY_INITIALIZED) {
                err = TA_ERR_BUSY;
                torii_audio::writeError(out_error, out_error_cap, "El dispositivo esta ocupado en modo exclusivo por otra aplicacion.");
            } else if (last_ma_res == MA_ACCESS_DENIED) {
                err = TA_ERR_EXCLUSIVE_NOT_ALLOWED;
                torii_audio::writeError(out_error, out_error_cap, "Windows no permite modo exclusivo en este dispositivo (revisar Propiedades de sonido).");
            } else {
                torii_audio::writeError(out_error, out_error_cap, "El dispositivo no acepto ningun formato/periodo en modo exclusivo.");
            }
            eng->last_error.store(err, std::memory_order_release);
            return err;
        }
#else
        // Non-Windows exclusive attempt (e.g., Android AAudio MMAP exclusive -> auto-fallback to shared)
        ma_device_config cfg = makeBaseConfig(ma_share_mode_exclusive, ma_performance_profile_low_latency);
        if (preferred_sample_rate > 0)
            cfg.sampleRate = static_cast<ma_uint32>(preferred_sample_rate);
        last_ma_res = ma_device_init(&eng->context, &cfg, &eng->device);
        if (last_ma_res != MA_SUCCESS) {
            cfg.playback.shareMode = ma_share_mode_shared;
            actual_mode = TA_MODE_LOW_LATENCY_SHARED;
            last_ma_res = ma_device_init(&eng->context, &cfg, &eng->device);
            if (last_ma_res != MA_SUCCESS) {
                eng->last_error.store(TA_ERR_GENERIC, std::memory_order_release);
                torii_audio::writeError(out_error, out_error_cap, "Failed to open audio device.");
                return TA_ERR_GENERIC;
            }
        }
#endif
    } else {
        // Shared mode (Low-Latency Event-Driven or Legacy Shared)
        const bool legacy = (mode == TA_MODE_LEGACY_SHARED);
        ma_device_config cfg = makeBaseConfig(
            ma_share_mode_shared,
            legacy ? ma_performance_profile_conservative : ma_performance_profile_low_latency);
        if (preferred_sample_rate > 0)
            cfg.sampleRate = static_cast<ma_uint32>(preferred_sample_rate);
        if (legacy) {
            cfg.periodSizeInMilliseconds = 10;
            cfg.periods = 2;
        } else {
#if defined(_WIN32)
            // Request device minimum period (~2-3ms on Win10/11) so miniaudio uses IAudioClient3 low-latency shared!
            double min_ms = 3.0, def_ms = 10.0;
            int32_t mix_rate = 48000;
            torii_audio::queryWasapiPeriods(target_id, min_ms, def_ms, mix_rate);
            cfg.periodSizeInMilliseconds = static_cast<ma_uint32>(std::max(2.0, std::round(min_ms)));
            cfg.periods = 2;
#else
            cfg.periodSizeInMilliseconds = 0; // Device minimum low-latency shared period!
            cfg.periods = 2;
#endif
        }

        last_ma_res = ma_device_init(&eng->context, &cfg, &eng->device);
        if (last_ma_res != MA_SUCCESS) {
            // Safe fallback with default OS parameters
            ma_device_config fb = makeBaseConfig(ma_share_mode_shared, ma_performance_profile_conservative);
            last_ma_res = ma_device_init(&eng->context, &fb, &eng->device);
            if (last_ma_res != MA_SUCCESS) {
                eng->last_error.store(TA_ERR_GENERIC, std::memory_order_release);
                torii_audio::writeError(out_error, out_error_cap, "Failed to initialize shared audio device.");
                return TA_ERR_GENERIC;
            }
        }
    }

    eng->device_valid = true;
    const int32_t sr = static_cast<int32_t>(eng->device.sampleRate > 0 ? eng->device.sampleRate : 48000);
    const int32_t ch = static_cast<int32_t>(eng->device.playback.channels > 0 ? eng->device.playback.channels : 2);
    const uint32_t internal_sr = eng->device.playback.internalSampleRate > 0 ? eng->device.playback.internalSampleRate : static_cast<uint32_t>(sr);
    const uint32_t period_frames = eng->device.playback.internalPeriodSizeInFrames;
    const uint32_t periods_cnt = std::max<uint32_t>(1, eng->device.playback.internalPeriods);

    const double period_ms = period_frames > 0 ? (static_cast<double>(period_frames) * 1000.0 / static_cast<double>(internal_sr)) : 10.0;
    // In event-driven double-buffered WASAPI Exclusive, effective queued hardware latency is ~1-2 periods
    const double lat_ms = (actual_mode == TA_MODE_EXCLUSIVE) ? period_ms : (period_ms * static_cast<double>(periods_cnt));

    eng->active_sample_rate.store(sr, std::memory_order_release);
    eng->active_channels.store(ch, std::memory_order_release);
    eng->active_mode.store(actual_mode, std::memory_order_release);
    eng->active_period_ms.store(period_ms, std::memory_order_release);
    eng->measured_latency_ms.store(lat_ms, std::memory_order_release);

    if (ma_device_start(&eng->device) != MA_SUCCESS) {
        ma_device_uninit(&eng->device);
        eng->device_valid = false;
        eng->last_error.store(TA_ERR_GENERIC, std::memory_order_release);
        torii_audio::writeError(out_error, out_error_cap, "Failed to start audio device.");
        return TA_ERR_GENERIC;
    }

    eng->device_started.store(1, std::memory_order_release);
    eng->last_error.store(TA_OK, std::memory_order_release);

    if (out_caps) {
        out_caps->sample_rate = sr;
        out_caps->channels = ch;
        out_caps->actual_mode = actual_mode;
        out_caps->is_float_output = (eng->device.playback.internalFormat == ma_format_f32) ? 1 : 0;
        switch (eng->device.playback.internalFormat) {
            case ma_format_f32:
            case ma_format_s32: out_caps->bit_depth = 32; break;
            case ma_format_s24: out_caps->bit_depth = 24; break;
            case ma_format_s16: out_caps->bit_depth = 16; break;
            default: out_caps->bit_depth = 32; break;
        }
        out_caps->active_period_ms = period_ms;
        out_caps->output_latency_ms = lat_ms;
        std::snprintf(out_caps->backend_name, sizeof(out_caps->backend_name), "%s", ma_get_backend_name(eng->context.backend));
    }

    return TA_OK;
}

TA_API void ta_device_free(ta_engine* eng) {
    if (!eng)
        return;
    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    torii_audio::stopNoSoundPump(eng);
    if (eng->device_valid) {
        ma_device_uninit(&eng->device);
        eng->device_valid = false;
    }
    eng->device_started.store(0, std::memory_order_release);
}

TA_API int32_t ta_device_pause(ta_engine* eng) {
    if (!eng) return 0;
    eng->device_started.store(0, std::memory_order_release);
    if (eng->device_valid)
        return ma_device_stop(&eng->device) == MA_SUCCESS ? 1 : 0;
    return eng->nosound_active ? 1 : 0;
}

TA_API int32_t ta_device_start(ta_engine* eng) {
    if (!eng) return 0;
    if (eng->device_valid) {
        if (ma_device_start(&eng->device) == MA_SUCCESS) {
            eng->device_started.store(1, std::memory_order_release);
            return 1;
        }
        return 0;
    }
    if (eng->nosound_active) {
        eng->device_started.store(1, std::memory_order_release);
        return 1;
    }
    return 0;
}

TA_API int32_t ta_device_is_started(ta_engine* eng) {
    if (!eng) return 0;
    if (eng->device_valid)
        return ma_device_is_started(&eng->device) ? 1 : 0;
    return eng->nosound_active ? eng->device_started.load(std::memory_order_acquire) : 0;
}

TA_API int32_t ta_device_is_lost(ta_engine* eng) {
    return eng ? eng->device_lost.load(std::memory_order_acquire) : 0;
}

TA_API double ta_get_output_latency_ms(ta_engine* eng) {
    if (!eng) return 0.0;
#if defined(_WIN32)
    // Query live WASAPI hardware padding when running on WASAPI for real-time queue visibility!
    if (eng->device_valid && eng->context.backend == ma_backend_wasapi && eng->device.wasapi.pAudioClientPlayback) {
        IAudioClient* client = reinterpret_cast<IAudioClient*>(eng->device.wasapi.pAudioClientPlayback);
        UINT32 padding = 0;
        if (SUCCEEDED(client->GetCurrentPadding(&padding))) {
            uint32_t sr = eng->device.playback.internalSampleRate > 0 ? eng->device.playback.internalSampleRate : 48000;
            double pad_ms = (static_cast<double>(padding) * 1000.0) / static_cast<double>(sr);
            // In Exclusive event-driven mode, padding oscillates between 0 and periodSizeInFrames;
            // report max(pad_ms, active_period_ms) so latency reflects true presentation delay cleanly.
            double p_ms = eng->active_period_ms.load(std::memory_order_relaxed);
            double reported = (eng->active_mode.load(std::memory_order_relaxed) == TA_MODE_EXCLUSIVE)
                ? std::max(pad_ms, p_ms)
                : (pad_ms > 0.0 ? pad_ms : eng->measured_latency_ms.load(std::memory_order_relaxed));
            eng->measured_latency_ms.store(reported, std::memory_order_relaxed);
            return reported;
        }
    }
#endif
    return eng->measured_latency_ms.load(std::memory_order_relaxed);
}

TA_API float ta_get_cpu_usage(ta_engine* eng) {
    return eng ? eng->cpu_usage_pct.load(std::memory_order_relaxed) : 0.0f;
}

TA_API int32_t ta_flush_output(ta_engine* eng) {
    if (!eng) return 0;
    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    if (!eng->device_valid)
        return eng->nosound_active ? 1 : 0;

    // Stop + start resets the WASAPI/hardware endpoint buffer instantaneously without tearing down the mixer graph!
    ma_device_stop(&eng->device);
    if (ma_device_start(&eng->device) == MA_SUCCESS) {
        eng->device_started.store(1, std::memory_order_release);
        return 1;
    }
    return 0;
}

TA_API int32_t ta_get_last_error(ta_engine* eng) {
    return eng ? eng->last_error.load(std::memory_order_acquire) : TA_ERR_NOT_INITIALIZED;
}

TA_API int32_t ta_get_device_sample_rate(ta_engine* eng) {
    return eng ? eng->active_sample_rate.load(std::memory_order_acquire) : 44100;
}

/* ---------------- Mixers & DSP FX ---------------- */

TA_API int32_t ta_mixer_create(ta_engine* eng, int32_t is_global_root) {
    if (!eng) return 0;
    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    for (int32_t i = 0; i < torii_audio::kMaxMixers; ++i) {
        uint32_t expected = torii_audio::kSlotFree;
        if (eng->mixers[i].state.compare_exchange_strong(expected, torii_audio::kSlotInitializing, std::memory_order_acq_rel)) {
            auto& mx = eng->mixers[i];
            mx.is_global_root.store(is_global_root != 0, std::memory_order_relaxed);
            mx.flush_counter.store(0, std::memory_order_relaxed);
            mx.applied_flush = 0;
            mx.fx_order_version.store(0, std::memory_order_relaxed);
            mx.applied_fx_order_version = 0;
            mx.sorted_fx_count = 0;
            mx.analysis.clear();
            for (int32_t f = 0; f < torii_audio::kMaxFxPerMixer; ++f)
                mx.fx[f].state.store(torii_audio::kSlotFree, std::memory_order_relaxed);
            mx.state.store(torii_audio::kSlotActive, std::memory_order_release);
            int32_t h = torii_audio::makeHandle(torii_audio::kTagMixer, i);
            if (is_global_root)
                eng->default_root_mixer_handle = h;
            return h;
        }
    }
    return 0;
}

TA_API void ta_mixer_destroy(ta_engine* eng, int32_t mixer_handle) {
    if (!eng || torii_audio::handleTag(mixer_handle) != torii_audio::kTagMixer) return;
    int32_t idx = torii_audio::handleIndex(mixer_handle);
    if (idx < 0 || idx >= torii_audio::kMaxMixers) return;

    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    eng->mixers[idx].state.store(torii_audio::kSlotRetiring, std::memory_order_release);
    torii_audio::waitRtQuiescent(eng);
    for (int32_t f = 0; f < torii_audio::kMaxFxPerMixer; ++f)
        eng->mixers[idx].fx[f].state.store(torii_audio::kSlotFree, std::memory_order_release);
    eng->mixers[idx].state.store(torii_audio::kSlotFree, std::memory_order_release);
}

TA_API int32_t ta_mixer_add_channel(ta_engine* eng, int32_t mixer_handle, int32_t channel_handle, int32_t start_paused) {
    if (!eng || torii_audio::handleTag(mixer_handle) != torii_audio::kTagMixer) return 0;
    int32_t tag = torii_audio::handleTag(channel_handle);
    int32_t idx = torii_audio::handleIndex(channel_handle);

    if (tag == torii_audio::kTagTrack && idx >= 0 && idx < torii_audio::kMaxTracks) {
        eng->tracks[idx].mixer_handle.store(mixer_handle, std::memory_order_release);
        if (start_paused)
            eng->tracks[idx].desired_playing.store(0, std::memory_order_release);
        return 1;
    }
    if (tag == torii_audio::kTagSampleChannel && idx >= 0 && idx < torii_audio::kMaxSampleChannels) {
        eng->sample_channels[idx].mixer_handle.store(mixer_handle, std::memory_order_release);
        return 1;
    }
    return 0;
}

TA_API int32_t ta_mixer_remove_channel(ta_engine* eng, int32_t mixer_handle, int32_t channel_handle) {
    if (!eng) return 0;
    int32_t tag = torii_audio::handleTag(channel_handle);
    int32_t idx = torii_audio::handleIndex(channel_handle);

    if (tag == torii_audio::kTagTrack && idx >= 0 && idx < torii_audio::kMaxTracks) {
        int32_t expected = mixer_handle;
        eng->tracks[idx].mixer_handle.compare_exchange_strong(expected, 0, std::memory_order_acq_rel);
        return 1;
    }
    if (tag == torii_audio::kTagSampleChannel && idx >= 0 && idx < torii_audio::kMaxSampleChannels) {
        int32_t expected = mixer_handle;
        eng->sample_channels[idx].mixer_handle.compare_exchange_strong(expected, 0, std::memory_order_acq_rel);
        return 1;
    }
    return 0;
}

TA_API void ta_mixer_flush(ta_engine* eng, int32_t mixer_handle) {
    if (!eng || torii_audio::handleTag(mixer_handle) != torii_audio::kTagMixer) return;
    int32_t idx = torii_audio::handleIndex(mixer_handle);
    if (idx >= 0 && idx < torii_audio::kMaxMixers)
        eng->mixers[idx].flush_counter.fetch_add(1, std::memory_order_release);
}

TA_API int32_t ta_mixer_get_channels(ta_engine* eng, int32_t mixer_handle, int32_t* out_handles, int32_t max_count) {
    if (!eng || torii_audio::handleTag(mixer_handle) != torii_audio::kTagMixer) return 0;
    int32_t count = 0;
    for (int32_t t = 0; t < torii_audio::kMaxTracks; ++t) {
        if (eng->tracks[t].state.load(std::memory_order_acquire) == torii_audio::kSlotActive &&
            eng->tracks[t].mixer_handle.load(std::memory_order_acquire) == mixer_handle) {
            if (out_handles && count < max_count)
                out_handles[count] = torii_audio::makeHandle(torii_audio::kTagTrack, t);
            ++count;
        }
    }
    for (int32_t c = 0; c < torii_audio::kMaxSampleChannels; ++c) {
        if (eng->sample_channels[c].state.load(std::memory_order_acquire) == torii_audio::kSlotActive &&
            eng->sample_channels[c].mixer_handle.load(std::memory_order_acquire) == mixer_handle) {
            if (out_handles && count < max_count)
                out_handles[count] = torii_audio::makeHandle(torii_audio::kTagSampleChannel, c);
            ++count;
        }
    }
    return count;
}

TA_API int32_t ta_mixer_get_levels(ta_engine* eng, int32_t mixer_handle, float window_sec, float* out_left, float* out_right) {
    if (!eng || torii_audio::handleTag(mixer_handle) != torii_audio::kTagMixer) return 0;
    int32_t idx = torii_audio::handleIndex(mixer_handle);
    if (idx < 0 || idx >= torii_audio::kMaxMixers || eng->mixers[idx].state.load(std::memory_order_acquire) != torii_audio::kSlotActive)
        return 0;
    float l = 0.0f, r = 0.0f;
    eng->mixers[idx].analysis.computeLevels(window_sec, l, r);
    if (out_left) *out_left = l;
    if (out_right) *out_right = r;
    return 1;
}

TA_API int32_t ta_mixer_fx_add(ta_engine* eng, int32_t mixer_handle, const ta_fx_params* params, int32_t priority) {
    if (!eng || !params || torii_audio::handleTag(mixer_handle) != torii_audio::kTagMixer) return 0;
    int32_t midx = torii_audio::handleIndex(mixer_handle);
    if (midx < 0 || midx >= torii_audio::kMaxMixers) return 0;

    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    auto& mx = eng->mixers[midx];
    if (mx.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;

    int32_t sr = eng->active_sample_rate.load(std::memory_order_acquire);
    for (int32_t f = 0; f < torii_audio::kMaxFxPerMixer; ++f) {
        uint32_t expected = torii_audio::kSlotFree;
        if (mx.fx[f].state.compare_exchange_strong(expected, torii_audio::kSlotInitializing, std::memory_order_acq_rel)) {
            mx.fx[f].priority = priority;
            mx.fx[f].seq = eng->fx_seq_counter.fetch_add(1, std::memory_order_relaxed);
            mx.fx[f].pending_params = *params;
            mx.fx[f].dsp = torii_audio::DspEffectNode{};
            mx.fx[f].dsp.params = *params;
            mx.fx[f].dsp.updateTargets(sr);
            mx.fx[f].version.store(1, std::memory_order_relaxed);
            mx.fx[f].applied_version = 1;
            mx.fx[f].state.store(torii_audio::kSlotActive, std::memory_order_release);
            mx.fx_order_version.fetch_add(1, std::memory_order_release);
            return torii_audio::makeHandle(torii_audio::kTagFx, (midx << 6) | f);
        }
    }
    return 0;
}

TA_API int32_t ta_mixer_fx_update(ta_engine* eng, int32_t mixer_handle, int32_t fx_handle, const ta_fx_params* params) {
    if (!eng || !params || torii_audio::handleTag(fx_handle) != torii_audio::kTagFx) return 0;
    int32_t packed = torii_audio::handleIndex(fx_handle);
    int32_t midx = packed >> 6;
    int32_t fidx = packed & 0x3F;
    if (midx < 0 || midx >= torii_audio::kMaxMixers || fidx < 0 || fidx >= torii_audio::kMaxFxPerMixer) return 0;
    if (mixer_handle != 0 && torii_audio::handleIndex(mixer_handle) != midx) return 0;

    auto& fxs = eng->mixers[midx].fx[fidx];
    if (fxs.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;
    fxs.pending_params = *params;
    fxs.version.fetch_add(1, std::memory_order_release);
    return 1;
}

TA_API int32_t ta_mixer_fx_remove(ta_engine* eng, int32_t mixer_handle, int32_t fx_handle) {
    if (!eng || torii_audio::handleTag(fx_handle) != torii_audio::kTagFx) return 0;
    int32_t packed = torii_audio::handleIndex(fx_handle);
    int32_t midx = packed >> 6;
    int32_t fidx = packed & 0x3F;
    if (midx < 0 || midx >= torii_audio::kMaxMixers || fidx < 0 || fidx >= torii_audio::kMaxFxPerMixer) return 0;
    if (mixer_handle != 0 && torii_audio::handleIndex(mixer_handle) != midx) return 0;

    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    auto& mx = eng->mixers[midx];
    mx.fx[fidx].state.store(torii_audio::kSlotRetiring, std::memory_order_release);
    mx.fx_order_version.fetch_add(1, std::memory_order_release);
    torii_audio::waitRtQuiescent(eng);
    mx.fx[fidx].state.store(torii_audio::kSlotFree, std::memory_order_release);
    return 1;
}

/* ---------------- Tracks ---------------- */

TA_API int32_t ta_track_create(ta_engine* eng, const uint8_t* data, size_t len, int32_t quick_preview, ta_track_info* out_info) {
    if (!eng || !data || len < 4) return 0;
    if (out_info) std::memset(out_info, 0, sizeof(*out_info));

    int32_t fmt = torii_audio::sniffFormatFromBytes(data, len);
    if (fmt == TA_FORMAT_UNKNOWN) return 0;

    int32_t slot_idx = -1;
    {
        std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
        for (int32_t i = 0; i < torii_audio::kMaxTracks; ++i) {
            uint32_t expected = torii_audio::kSlotFree;
            if (eng->tracks[i].state.compare_exchange_strong(expected, torii_audio::kSlotInitializing, std::memory_order_acq_rel)) {
                slot_idx = i;
                break;
            }
        }
    }
    if (slot_idx < 0) return 0;

    auto& trk = eng->tracks[slot_idx];
    trk.raw_data = static_cast<uint8_t*>(std::malloc(len));
    if (!trk.raw_data) {
        trk.state.store(torii_audio::kSlotFree, std::memory_order_release);
        return 0;
    }
    std::memcpy(trk.raw_data, data, len);
    trk.raw_size = len;

    // Always decode to 2-channel float32 at source sample rate (avoids double resampling!)
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 2, 0);
    cfg.encodingFormat = torii_audio::formatToMa(fmt);
    if (ma_decoder_init_memory(trk.raw_data, trk.raw_size, &cfg, &trk.decoder) != MA_SUCCESS) {
        std::free(trk.raw_data);
        trk.raw_data = nullptr;
        trk.state.store(torii_audio::kSlotFree, std::memory_order_release);
        return 0;
    }
    trk.decoder_valid = true;

    const int32_t sr = static_cast<int32_t>(trk.decoder.outputSampleRate > 0 ? trk.decoder.outputSampleRate : 44100);
    ma_uint64 total_frames = 0;
    if (!quick_preview && fmt == TA_FORMAT_MP3) {
        // Prescan exact MP3 frames (matches BassFlags.Prescan for exact Length & Seek on VBR/CBR MP3s!)
        std::vector<float> tmp(4096 * 2);
        ma_uint64 read = 0;
        while (ma_decoder_read_pcm_frames(&trk.decoder, tmp.data(), 4096, &read) == MA_SUCCESS && read > 0)
            total_frames += read;
        ma_decoder_seek_to_pcm_frame(&trk.decoder, 0);
    } else {
        if (ma_decoder_get_length_in_pcm_frames(&trk.decoder, &total_frames) != MA_SUCCESS)
            total_frames = 0;
    }

    trk.format = fmt;
    trk.src_sample_rate = sr;
    trk.src_channels = 2;
    trk.total_frames = static_cast<int64_t>(total_frames);
    trk.length_ms = total_frames > 0 ? (static_cast<double>(total_frames) * 1000.0 / sr) : 0.0;
    // Match TrackBass lastSeekablePosition = 1 frame margin before EOF
    trk.last_seekable_ms = total_frames > 1 ? (static_cast<double>(total_frames - 1) * 1000.0 / sr) : 0.0;

    // Bitrate calculation (excluding ID3v2 header for MP3)
    trk.bitrate_kbps = 0;
    if (trk.length_ms > 0.0) {
        size_t payload = len;
        if (fmt == TA_FORMAT_MP3 && len >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3') {
            size_t sz = (static_cast<size_t>(data[6] & 0x7F) << 21)
                      | (static_cast<size_t>(data[7] & 0x7F) << 14)
                      | (static_cast<size_t>(data[8] & 0x7F) << 7)
                      |  static_cast<size_t>(data[9] & 0x7F);
            size_t skip = 10 + sz + ((data[5] & 0x10) ? 10 : 0);
            if (skip < len) payload = len - skip;
        }
        trk.bitrate_kbps = static_cast<int32_t>(std::llround((payload * 8.0) / trk.length_ms));
    }

    // Reset runtime fields
    trk.mixer_handle.store(0, std::memory_order_relaxed);
    trk.target_volume.store(1.0f, std::memory_order_relaxed);
    trk.target_balance.store(0.0f, std::memory_order_relaxed);
    trk.target_frequency.store(1.0, std::memory_order_relaxed);
    trk.target_tempo.store(1.0, std::memory_order_relaxed);
    trk.looping.store(0, std::memory_order_relaxed);
    trk.restart_point_ms.store(0.0, std::memory_order_relaxed);
    trk.desired_playing.store(0, std::memory_order_relaxed);
    trk.seek_seq.store(0, std::memory_order_relaxed);
    trk.applied_seek_seq = 0;
    trk.published_time_ms.store(0.0, std::memory_order_relaxed);
    trk.published_playing.store(0, std::memory_order_relaxed);
    trk.published_completed.store(0, std::memory_order_relaxed);
    trk.loop_count.store(0, std::memory_order_relaxed);
    trk.end_event_count.store(0, std::memory_order_relaxed);
    trk.src_cursor_frame = 0.0;
    trk.decoder_frame_pos = 0;
    trk.has_completed = false;
    trk.cur_gain_l = 1.0f;
    trk.cur_gain_r = 1.0f;
    trk.resetDecoderCache();
    trk.analysis.clear();

    // Pre-configure Signalsmith Stretch off the RT thread (zero RT allocation!)
    int32_t dev_sr = std::max(8000, eng->active_sample_rate.load(std::memory_order_acquire));
    trk.stretcher.presetDefault(2, static_cast<float>(dev_sr), true);
    trk.stretcher_sr = dev_sr;
    trk.stretcher_active = false;
    trk.stretcher_primed = false;
    trk.stretcher_frac_accum = 0.0;

    trk.state.store(torii_audio::kSlotActive, std::memory_order_release);

    if (out_info) {
        out_info->format = trk.format;
        out_info->sample_rate = trk.src_sample_rate;
        out_info->channels = trk.src_channels;
        out_info->bitrate_kbps = trk.bitrate_kbps;
        out_info->total_frames = trk.total_frames;
        out_info->length_ms = trk.length_ms;
        out_info->last_seekable_ms = trk.last_seekable_ms;
    }

    return torii_audio::makeHandle(torii_audio::kTagTrack, slot_idx);
}

TA_API void ta_track_destroy(ta_engine* eng, int32_t track_handle) {
    if (!eng || torii_audio::handleTag(track_handle) != torii_audio::kTagTrack) return;
    int32_t idx = torii_audio::handleIndex(track_handle);
    if (idx < 0 || idx >= torii_audio::kMaxTracks) return;

    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    auto& trk = eng->tracks[idx];
    if (trk.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return;

    trk.state.store(torii_audio::kSlotRetiring, std::memory_order_release);
    torii_audio::waitRtQuiescent(eng);
    if (trk.decoder_valid) {
        ma_decoder_uninit(&trk.decoder);
        trk.decoder_valid = false;
    }
    std::free(trk.raw_data);
    trk.raw_data = nullptr;
    trk.raw_size = 0;
    trk.state.store(torii_audio::kSlotFree, std::memory_order_release);
}

TA_API int32_t ta_track_play(ta_engine* eng, int32_t track_handle) {
    if (!eng || torii_audio::handleTag(track_handle) != torii_audio::kTagTrack) return 0;
    int32_t idx = torii_audio::handleIndex(track_handle);
    if (idx < 0 || idx >= torii_audio::kMaxTracks) return 0;
    auto& trk = eng->tracks[idx];
    if (trk.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;
    if (trk.published_completed.load(std::memory_order_acquire) != 0) return 0;

    trk.desired_playing.store(1, std::memory_order_release);
    trk.published_playing.store(1, std::memory_order_release);
    return 1;
}

TA_API int32_t ta_track_pause(ta_engine* eng, int32_t track_handle, int32_t flush_mixer) {
    if (!eng || torii_audio::handleTag(track_handle) != torii_audio::kTagTrack) return 0;
    int32_t idx = torii_audio::handleIndex(track_handle);
    if (idx < 0 || idx >= torii_audio::kMaxTracks) return 0;
    auto& trk = eng->tracks[idx];
    if (trk.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;

    trk.desired_playing.store(0, std::memory_order_release);
    trk.published_playing.store(0, std::memory_order_release);
    if (flush_mixer) {
        int32_t mh = trk.mixer_handle.load(std::memory_order_acquire);
        if (mh != 0)
            ta_mixer_flush(eng, mh);
    }
    return 1;
}

TA_API int32_t ta_track_seek_ms(ta_engine* eng, int32_t track_handle, double target_ms, double* out_actual_ms) {
    if (!eng || torii_audio::handleTag(track_handle) != torii_audio::kTagTrack) return 0;
    int32_t idx = torii_audio::handleIndex(track_handle);
    if (idx < 0 || idx >= torii_audio::kMaxTracks) return 0;
    auto& trk = eng->tracks[idx];
    if (trk.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;

    // Match TrackBass exact seek clamping & return value contract:
    // - clamp to [0, last_seekable_ms]
    // - return 1 ONLY if target_ms was strictly within [0, last_seekable_ms] (so Seek(Length) returns 0!)
    double clamped = std::clamp(target_ms, 0.0, trk.last_seekable_ms);
    bool exact_in_range = (target_ms >= 0.0 && target_ms <= trk.last_seekable_ms && trk.total_frames > 0);

    // Publish immediately so CurrentTime reflects the sought position BEFORE the next audio tick!
    trk.seek_target_ms.store(clamped, std::memory_order_relaxed);
    trk.published_time_ms.store(clamped, std::memory_order_release);
    if (clamped < trk.length_ms)
        trk.published_completed.store(0, std::memory_order_release);
    trk.seek_seq.fetch_add(1, std::memory_order_release);

    if (out_actual_ms)
        *out_actual_ms = clamped;
    return exact_in_range ? 1 : 0;
}

TA_API void ta_track_set_params(ta_engine* eng, int32_t track_handle, float volume, float balance, double frequency, double tempo, int32_t looping, double restart_point_ms) {
    if (!eng || torii_audio::handleTag(track_handle) != torii_audio::kTagTrack) return;
    int32_t idx = torii_audio::handleIndex(track_handle);
    if (idx < 0 || idx >= torii_audio::kMaxTracks) return;
    auto& trk = eng->tracks[idx];
    if (trk.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return;

    trk.target_volume.store(volume, std::memory_order_relaxed);
    trk.target_balance.store(balance, std::memory_order_relaxed);
    trk.target_frequency.store(frequency, std::memory_order_relaxed);
    trk.target_tempo.store(std::max(0.05, tempo), std::memory_order_relaxed);
    trk.looping.store(looping ? 1 : 0, std::memory_order_relaxed);
    trk.restart_point_ms.store(std::max(0.0, restart_point_ms), std::memory_order_relaxed);
}

TA_API int32_t ta_track_get_clock(ta_engine* eng, int32_t track_handle, ta_track_clock* out_clock) {
    if (!eng || !out_clock || torii_audio::handleTag(track_handle) != torii_audio::kTagTrack) return 0;
    int32_t idx = torii_audio::handleIndex(track_handle);
    if (idx < 0 || idx >= torii_audio::kMaxTracks) return 0;
    auto& trk = eng->tracks[idx];
    if (trk.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;

    out_clock->current_time_ms = trk.published_time_ms.load(std::memory_order_acquire);
    out_clock->is_playing = trk.published_playing.load(std::memory_order_acquire);
    out_clock->has_completed = trk.published_completed.load(std::memory_order_acquire);
    out_clock->loop_count = trk.loop_count.load(std::memory_order_acquire);
    out_clock->end_event_count = trk.end_event_count.load(std::memory_order_acquire);
    return 1;
}

/* ---------------- Samples & Polyphonic Voices ---------------- */

TA_API int32_t ta_sample_create(ta_engine* eng, const uint8_t* data, size_t len, int32_t max_concurrency, ta_sample_info* out_info) {
    if (!eng || !data || len < 4) return 0;
    if (out_info) std::memset(out_info, 0, sizeof(*out_info));

    int32_t fmt = torii_audio::sniffFormatFromBytes(data, len);
    if (fmt == TA_FORMAT_UNKNOWN) return 0;

    // Decode ONCE to interleaved stereo float32 PCM (works even before any hardware audio device is initialized!)
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 2, 0);
    cfg.encodingFormat = torii_audio::formatToMa(fmt);

    ma_decoder dec;
    if (ma_decoder_init_memory(data, len, &cfg, &dec) != MA_SUCCESS)
        return 0;

    const int32_t sr = static_cast<int32_t>(dec.outputSampleRate > 0 ? dec.outputSampleRate : 44100);
    std::vector<float> pcm;
    ma_uint64 est = 0;
    if (ma_decoder_get_length_in_pcm_frames(&dec, &est) == MA_SUCCESS && est > 0 && est < 50000000)
        pcm.reserve(static_cast<size_t>(est) * 2);

    float chunk[4096 * 2];
    ma_uint64 read = 0;
    while (ma_decoder_read_pcm_frames(&dec, chunk, 4096, &read) == MA_SUCCESS && read > 0) {
        pcm.insert(pcm.end(), chunk, chunk + read * 2);
    }
    ma_decoder_uninit(&dec);

    const int64_t total_frames = static_cast<int64_t>(pcm.size() / 2);
    const size_t pcm_bytes = pcm.size() * sizeof(float);
    float* owned_pcm = nullptr;
    if (pcm_bytes > 0) {
        owned_pcm = static_cast<float*>(std::malloc(pcm_bytes));
        if (!owned_pcm) return 0;
        std::memcpy(owned_pcm, pcm.data(), pcm_bytes);
    }

    int32_t slot_idx = -1;
    {
        std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
        for (int32_t i = 0; i < torii_audio::kMaxSamples; ++i) {
            uint32_t expected = torii_audio::kSlotFree;
            if (eng->samples[i].state.compare_exchange_strong(expected, torii_audio::kSlotInitializing, std::memory_order_acq_rel)) {
                slot_idx = i;
                break;
            }
        }
    }
    if (slot_idx < 0) {
        std::free(owned_pcm);
        return 0;
    }

    auto& asset = eng->samples[slot_idx];
    asset.sample_rate = sr;
    asset.channels = 2;
    asset.total_frames = total_frames;
    asset.length_ms = total_frames > 0 ? (static_cast<double>(total_frames) * 1000.0 / sr) : 0.0;
    asset.pcm_stereo = owned_pcm;
    asset.pcm_bytes = pcm_bytes;
    asset.max_concurrency.store(std::max(1, max_concurrency), std::memory_order_relaxed);
    asset.ref_count.store(1, std::memory_order_relaxed);
    asset.state.store(torii_audio::kSlotActive, std::memory_order_release);

    if (out_info) {
        out_info->format = fmt;
        out_info->sample_rate = sr;
        out_info->channels = 2;
        out_info->total_frames = total_frames;
        out_info->pcm_bytes = static_cast<int64_t>(pcm_bytes);
        out_info->length_ms = asset.length_ms;
    }

    return torii_audio::makeHandle(torii_audio::kTagSample, slot_idx);
}

TA_API void ta_sample_set_concurrency(ta_engine* eng, int32_t sample_handle, int32_t max_concurrency) {
    if (!eng || torii_audio::handleTag(sample_handle) != torii_audio::kTagSample) return;
    int32_t idx = torii_audio::handleIndex(sample_handle);
    if (idx < 0 || idx >= torii_audio::kMaxSamples) return;
    eng->samples[idx].max_concurrency.store(std::max(1, max_concurrency), std::memory_order_release);
}

TA_API void ta_sample_destroy(ta_engine* eng, int32_t sample_handle) {
    if (!eng || torii_audio::handleTag(sample_handle) != torii_audio::kTagSample) return;
    int32_t idx = torii_audio::handleIndex(sample_handle);
    if (idx < 0 || idx >= torii_audio::kMaxSamples) return;

    std::lock_guard<std::mutex> lock(eng->ctrl_mutex);
    auto& asset = eng->samples[idx];
    if (asset.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return;

    // Stop any sample channels referencing this asset before freeing PCM memory
    for (int32_t c = 0; c < torii_audio::kMaxSampleChannels; ++c) {
        if (eng->sample_channels[c].state.load(std::memory_order_acquire) == torii_audio::kSlotActive &&
            eng->sample_channels[c].sample_idx.load(std::memory_order_acquire) == idx) {
            eng->sample_channels[c].concurrency_claimed.store(0, std::memory_order_release);
            eng->sample_channels[c].logical_state.store(TA_STATE_STOPPED, std::memory_order_release);
            eng->sample_channels[c].rt_state.store(TA_STATE_STOPPED, std::memory_order_release);
            eng->sample_channels[c].sample_idx.store(-1, std::memory_order_release);
        }
    }

    asset.state.store(torii_audio::kSlotRetiring, std::memory_order_release);
    torii_audio::waitRtQuiescent(eng);
    std::free(asset.pcm_stereo);
    asset.pcm_stereo = nullptr;
    asset.pcm_bytes = 0;
    asset.state.store(torii_audio::kSlotFree, std::memory_order_release);
}

TA_API int32_t ta_sample_channel_create(ta_engine* eng, int32_t sample_handle, int32_t mixer_handle) {
    if (!eng || torii_audio::handleTag(sample_handle) != torii_audio::kTagSample) return 0;
    int32_t sidx = torii_audio::handleIndex(sample_handle);
    if (sidx < 0 || sidx >= torii_audio::kMaxSamples) return 0;
    if (eng->samples[sidx].state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;

    for (int32_t i = 0; i < torii_audio::kMaxSampleChannels; ++i) {
        uint32_t expected = torii_audio::kSlotFree;
        if (eng->sample_channels[i].state.compare_exchange_strong(expected, torii_audio::kSlotInitializing, std::memory_order_acq_rel)) {
            auto& ch = eng->sample_channels[i];
            ch.sample_idx.store(sidx, std::memory_order_relaxed);
            ch.mixer_handle.store(mixer_handle, std::memory_order_relaxed);
            ch.target_volume.store(1.0f, std::memory_order_relaxed);
            ch.target_balance.store(0.0f, std::memory_order_relaxed);
            ch.target_frequency.store(1.0, std::memory_order_relaxed);
            ch.looping.store(0, std::memory_order_relaxed);
            ch.play_seq.store(0, std::memory_order_relaxed);
            ch.stop_seq.store(0, std::memory_order_relaxed);
            ch.applied_play_seq = 0;
            ch.applied_stop_seq = 0;
            ch.rt_state.store(TA_STATE_STOPPED, std::memory_order_relaxed);
            ch.logical_state.store(TA_STATE_STOPPED, std::memory_order_relaxed);
            ch.concurrency_claimed.store(0, std::memory_order_relaxed);
            ch.start_order.store(0, std::memory_order_relaxed);
            ch.phase = 0.0;
            ch.fading_out = false;
            ch.fade_out_samples = 0;
            ch.paused_by_zero_freq = false;
            ch.analysis.clear();
            ch.state.store(torii_audio::kSlotActive, std::memory_order_release);
            return torii_audio::makeHandle(torii_audio::kTagSampleChannel, i);
        }
    }
    return 0;
}

TA_API int32_t ta_sample_channel_play(ta_engine* eng, int32_t channel_handle) {
    if (!eng || torii_audio::handleTag(channel_handle) != torii_audio::kTagSampleChannel) return 0;
    int32_t idx = torii_audio::handleIndex(channel_handle);
    if (idx < 0 || idx >= torii_audio::kMaxSampleChannels) return 0;
    auto& ch = eng->sample_channels[idx];
    if (ch.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;

    int32_t sidx = ch.sample_idx.load(std::memory_order_acquire);
    if (sidx < 0 || sidx >= torii_audio::kMaxSamples) return 0;
    int32_t max_conc = eng->samples[sidx].max_concurrency.load(std::memory_order_acquire);

    // Enforce PlaybackConcurrency (BASS SampleOverrideLongestPlaying semantics):
    // If >= max_conc channels of this sample are currently playing, smoothly fade out the oldest one!
    int32_t active_count = 0;
    int32_t oldest_idx = -1;
    uint64_t oldest_order = UINT64_MAX;

    for (int32_t i = 0; i < torii_audio::kMaxSampleChannels; ++i) {
        if (i == idx) continue;
        auto& other = eng->sample_channels[i];
        if (other.state.load(std::memory_order_acquire) == torii_audio::kSlotActive &&
            other.sample_idx.load(std::memory_order_relaxed) == sidx &&
            other.concurrency_claimed.load(std::memory_order_acquire) != 0 &&
            other.rt_state.load(std::memory_order_acquire) == TA_STATE_PLAYING) {
            ++active_count;
            uint64_t ord = other.start_order.load(std::memory_order_relaxed);
            if (ord < oldest_order) {
                oldest_order = ord;
                oldest_idx = i;
            }
        }
    }

    if (active_count >= max_conc && oldest_idx >= 0) {
        eng->sample_channels[oldest_idx].concurrency_claimed.store(0, std::memory_order_release);
        eng->sample_channels[oldest_idx].logical_state.store(TA_STATE_STOPPED, std::memory_order_release);
        eng->sample_channels[oldest_idx].stop_seq.fetch_add(1, std::memory_order_release);
    }

    ch.start_order.store(eng->voice_order_counter.fetch_add(1, std::memory_order_relaxed), std::memory_order_relaxed);
    ch.concurrency_claimed.store(1, std::memory_order_release);
    ch.logical_state.store(TA_STATE_PLAYING, std::memory_order_release);
    ch.rt_state.store(TA_STATE_PLAYING, std::memory_order_release);
    ch.play_seq.fetch_add(1, std::memory_order_release);
    return 1;
}

TA_API int32_t ta_sample_channel_stop(ta_engine* eng, int32_t channel_handle) {
    if (!eng || torii_audio::handleTag(channel_handle) != torii_audio::kTagSampleChannel) return 0;
    int32_t idx = torii_audio::handleIndex(channel_handle);
    if (idx < 0 || idx >= torii_audio::kMaxSampleChannels) return 0;
    auto& ch = eng->sample_channels[idx];
    if (ch.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return 0;

    ch.concurrency_claimed.store(0, std::memory_order_release);
    ch.logical_state.store(TA_STATE_STOPPED, std::memory_order_release);
    ch.stop_seq.fetch_add(1, std::memory_order_release);
    return 1;
}

TA_API void ta_sample_channel_set_params(ta_engine* eng, int32_t channel_handle, float volume, float balance, double frequency, int32_t looping) {
    if (!eng || torii_audio::handleTag(channel_handle) != torii_audio::kTagSampleChannel) return;
    int32_t idx = torii_audio::handleIndex(channel_handle);
    if (idx < 0 || idx >= torii_audio::kMaxSampleChannels) return;
    auto& ch = eng->sample_channels[idx];
    if (ch.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return;

    ch.target_volume.store(volume, std::memory_order_relaxed);
    ch.target_balance.store(balance, std::memory_order_relaxed);
    ch.target_frequency.store(frequency, std::memory_order_relaxed);
    ch.looping.store(looping ? 1 : 0, std::memory_order_relaxed);
}

TA_API int32_t ta_sample_channel_get_state(ta_engine* eng, int32_t channel_handle) {
    if (!eng || torii_audio::handleTag(channel_handle) != torii_audio::kTagSampleChannel) return TA_STATE_STOPPED;
    int32_t idx = torii_audio::handleIndex(channel_handle);
    if (idx < 0 || idx >= torii_audio::kMaxSampleChannels) return TA_STATE_STOPPED;
    auto& ch = eng->sample_channels[idx];
    if (ch.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return TA_STATE_STOPPED;
    return ch.logical_state.load(std::memory_order_acquire);
}

TA_API void ta_sample_channel_destroy(ta_engine* eng, int32_t channel_handle) {
    if (!eng || torii_audio::handleTag(channel_handle) != torii_audio::kTagSampleChannel) return;
    int32_t idx = torii_audio::handleIndex(channel_handle);
    if (idx < 0 || idx >= torii_audio::kMaxSampleChannels) return;
    auto& ch = eng->sample_channels[idx];
    if (ch.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return;

    ch.concurrency_claimed.store(0, std::memory_order_release);
    ch.logical_state.store(TA_STATE_STOPPED, std::memory_order_release);
    ch.rt_state.store(TA_STATE_STOPPED, std::memory_order_release);
    ch.state.store(torii_audio::kSlotRetiring, std::memory_order_release);
    torii_audio::waitRtQuiescent(eng);
    ch.sample_idx.store(-1, std::memory_order_release);
    ch.state.store(torii_audio::kSlotFree, std::memory_order_release);
}

/* ---------------- Channel Analysis ---------------- */

TA_API int32_t ta_channel_get_amplitudes(ta_engine* eng, int32_t channel_handle, float* out_left, float* out_right, float* out_fft256) {
    if (!eng) return 0;
    int32_t tag = torii_audio::handleTag(channel_handle);
    int32_t idx = torii_audio::handleIndex(channel_handle);

    const torii_audio::AnalysisRing* ring = nullptr;
    bool active = false;

    if (tag == torii_audio::kTagTrack && idx >= 0 && idx < torii_audio::kMaxTracks) {
        auto& trk = eng->tracks[idx];
        if (trk.state.load(std::memory_order_acquire) == torii_audio::kSlotActive) {
            active = trk.published_playing.load(std::memory_order_acquire) != 0 && std::fabs(trk.target_frequency.load(std::memory_order_relaxed)) >= 1e-6;
            ring = &trk.analysis;
        }
    } else if (tag == torii_audio::kTagSampleChannel && idx >= 0 && idx < torii_audio::kMaxSampleChannels) {
        auto& ch = eng->sample_channels[idx];
        if (ch.state.load(std::memory_order_acquire) == torii_audio::kSlotActive) {
            active = ch.rt_state.load(std::memory_order_acquire) == TA_STATE_PLAYING && std::fabs(ch.target_frequency.load(std::memory_order_relaxed)) >= 1e-6;
            ring = &ch.analysis;
        }
    }

    if (!ring || !active) {
        if (out_left) *out_left = 0.0f;
        if (out_right) *out_right = 0.0f;
        if (out_fft256) std::memset(out_fft256, 0, 256 * sizeof(float));
        return 0;
    }

    float l = 0.0f, r = 0.0f;
    ring->computeLevels(1.0f / 60.0f, l, r);
    if (out_left) *out_left = l;
    if (out_right) *out_right = r;
    if (out_fft256) ring->computeFft512(out_fft256);
    return 1;
}

TA_API int32_t ta_channel_get_levels(ta_engine* eng, int32_t channel_handle, float window_sec, float* out_left, float* out_right) {
    if (!eng) return 0;
    int32_t tag = torii_audio::handleTag(channel_handle);
    int32_t idx = torii_audio::handleIndex(channel_handle);

    if (tag == torii_audio::kTagMixer)
        return ta_mixer_get_levels(eng, channel_handle, window_sec, out_left, out_right);

    const torii_audio::AnalysisRing* ring = nullptr;
    if (tag == torii_audio::kTagTrack && idx >= 0 && idx < torii_audio::kMaxTracks &&
        eng->tracks[idx].state.load(std::memory_order_acquire) == torii_audio::kSlotActive) {
        ring = &eng->tracks[idx].analysis;
    } else if (tag == torii_audio::kTagSampleChannel && idx >= 0 && idx < torii_audio::kMaxSampleChannels &&
               eng->sample_channels[idx].state.load(std::memory_order_acquire) == torii_audio::kSlotActive) {
        ring = &eng->sample_channels[idx].analysis;
    }

    if (!ring) {
        if (out_left) *out_left = 0.0f;
        if (out_right) *out_right = 0.0f;
        return 0;
    }

    float l = 0.0f, r = 0.0f;
    ring->computeLevels(window_sec, l, r);
    if (out_left) *out_left = l;
    if (out_right) *out_right = r;
    return 1;
}

TA_API int32_t ta_channel_get_state(ta_engine* eng, int32_t channel_handle) {
    if (!eng) return TA_STATE_STOPPED;
    int32_t tag = torii_audio::handleTag(channel_handle);
    int32_t idx = torii_audio::handleIndex(channel_handle);

    if (tag == torii_audio::kTagTrack && idx >= 0 && idx < torii_audio::kMaxTracks) {
        auto& trk = eng->tracks[idx];
        if (trk.state.load(std::memory_order_acquire) != torii_audio::kSlotActive) return TA_STATE_STOPPED;
        return trk.published_playing.load(std::memory_order_acquire) ? TA_STATE_PLAYING : TA_STATE_PAUSED;
    }
    if (tag == torii_audio::kTagSampleChannel)
        return ta_sample_channel_get_state(eng, channel_handle);
    return TA_STATE_STOPPED;
}

} // extern "C"
