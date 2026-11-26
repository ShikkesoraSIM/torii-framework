#pragma once

#include "torii_audio.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>

#include "fft.h"

namespace torii_audio {

static constexpr double kPi = 3.14159265358979323846;

/*
 * Fast zero-allocation soft-knee master limiter to prevent harsh digital clipping
 * when summing 128+ concurrent hitsounds + track audio.
 * Linear up to 0.96 (-0.35 dBFS), smooth tanh knee to +/-1.0.
 */
inline float softLimitSample(float x) noexcept {
    const float ax = std::fabs(x);
    if (ax <= 0.96f)
        return x;
    const float excess = (ax - 0.96f) / 0.04f;
    const float limited = 0.96f + 0.04f * std::tanh(excess);
    return x < 0.0f ? -limited : limited;
}

/*
 * BASS-compatible linear stereo panning law:
 *   pan in [-1, +1]
 *   pan < 0: L = 1, R = 1 + pan
 *   pan > 0: L = 1 - pan, R = 1
 */
inline void computePanGains(float balance, float& outL, float& outR) noexcept {
    float b = std::clamp(balance, -1.0f, 1.0f);
    outL = b > 0.0f ? (1.0f - b) : 1.0f;
    outR = b < 0.0f ? (1.0f + b) : 1.0f;
}

/*
 * 4-point Catmull-Rom / Hermite interpolation for ultra-clean varispeed resampling.
 */
inline float hermite4(float y0, float y1, float y2, float y3, float t) noexcept {
    const float c0 = y1;
    const float c1 = 0.5f * (y2 - y0);
    const float c2 = y0 - 2.5f * y1 + 2.0f * y2 - 0.5f * y3;
    const float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
    return ((c3 * t + c2) * t + c1) * t + c0;
}

/*
 * RBJ Audio EQ Cookbook stereo biquad filter with per-sample coefficient smoothing
 * (eliminates zipper noise & clicks during rapid CutoffTo sweeps like FailAnimation / PlayerLoader)
 * plus modulation effects (Chorus / Phaser / AutoWah) for full BASS_FX parity.
 */
struct DspEffectNode {
    int32_t handle = 0;
    int32_t priority = 0;
    uint64_t insertion_seq = 0;
    ta_fx_params params{};
    bool active = true;

    // Biquad coefficients (current + target for smooth ramping)
    double b0 = 1.0, b1 = 0.0, b2 = 0.0, a1 = 0.0, a2 = 0.0;
    double tb0 = 1.0, tb1 = 0.0, tb2 = 0.0, ta1 = 0.0, ta2 = 0.0;
    double z1L = 0.0, z2L = 0.0, z1R = 0.0, z2R = 0.0;
    bool coeffs_initialized = false;

    // Modulation FX state (Chorus / Phaser / AutoWah)
    double lfo_phase = 0.0;
    float env_follower = 0.0f;
    std::vector<float> delay_buf_l;
    std::vector<float> delay_buf_r;
    uint32_t delay_write = 0;
    // 4-stage allpass state for phaser
    float ap_l[4]{};
    float ap_r[4]{};

    void updateTargets(int32_t sample_rate) noexcept {
        if (sample_rate <= 0)
            sample_rate = 44100;

        if (params.type == TA_FX_CHORUS) {
            if (delay_buf_l.empty()) {
                delay_buf_l.assign(4096, 0.0f);
                delay_buf_r.assign(4096, 0.0f);
            }
            return;
        }
        if (params.type == TA_FX_PHASER || params.type == TA_FX_AUTOWAH) {
            return;
        }

        const double sr = static_cast<double>(sample_rate);
        const double max_freq = sr * 0.495;
        const double freq = std::clamp(static_cast<double>(params.center_hz), 1.0, max_freq);
        const double w0 = 2.0 * kPi * freq / sr;
        const double cos_w0 = std::cos(w0);
        const double sin_w0 = std::sin(w0);

        double alpha = 0.0;
        if (params.bandwidth > 0.0f) {
            const double bw = std::clamp(static_cast<double>(params.bandwidth), 0.01, 10.0);
            alpha = sin_w0 * std::sinh((std::log(2.0) * 0.5) * bw * (w0 / std::max(sin_w0, 1e-9)));
        } else {
            const double q = std::clamp(static_cast<double>(params.q > 0.0f ? params.q : 0.70710678f), 0.05, 30.0);
            alpha = sin_w0 / (2.0 * q);
        }

        const double A = std::pow(10.0, static_cast<double>(params.gain_db) / 40.0);
        double nb0 = 1.0, nb1 = 0.0, nb2 = 0.0, na0 = 1.0, na1 = 0.0, na2 = 0.0;

        switch (params.type) {
            case TA_FX_BIQUAD_LOWPASS:
                nb0 = (1.0 - cos_w0) * 0.5;
                nb1 = 1.0 - cos_w0;
                nb2 = (1.0 - cos_w0) * 0.5;
                na0 = 1.0 + alpha;
                na1 = -2.0 * cos_w0;
                na2 = 1.0 - alpha;
                break;
            case TA_FX_BIQUAD_HIGHPASS:
                nb0 = (1.0 + cos_w0) * 0.5;
                nb1 = -(1.0 + cos_w0);
                nb2 = (1.0 + cos_w0) * 0.5;
                na0 = 1.0 + alpha;
                na1 = -2.0 * cos_w0;
                na2 = 1.0 - alpha;
                break;
            case TA_FX_BIQUAD_BANDPASS:
                nb0 = alpha;
                nb1 = 0.0;
                nb2 = -alpha;
                na0 = 1.0 + alpha;
                na1 = -2.0 * cos_w0;
                na2 = 1.0 - alpha;
                break;
            case TA_FX_BIQUAD_NOTCH:
                nb0 = 1.0;
                nb1 = -2.0 * cos_w0;
                nb2 = 1.0;
                na0 = 1.0 + alpha;
                na1 = -2.0 * cos_w0;
                na2 = 1.0 - alpha;
                break;
            case TA_FX_BIQUAD_PEAKING_EQ:
                nb0 = 1.0 + alpha * A;
                nb1 = -2.0 * cos_w0;
                nb2 = 1.0 - alpha * A;
                na0 = 1.0 + alpha / A;
                na1 = -2.0 * cos_w0;
                na2 = 1.0 - alpha / A;
                break;
            case TA_FX_BIQUAD_LOW_SHELF: {
                const double sq = 2.0 * std::sqrt(A) * alpha;
                nb0 = A * ((A + 1.0) - (A - 1.0) * cos_w0 + sq);
                nb1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cos_w0);
                nb2 = A * ((A + 1.0) - (A - 1.0) * cos_w0 - sq);
                na0 = (A + 1.0) + (A - 1.0) * cos_w0 + sq;
                na1 = -2.0 * ((A - 1.0) + (A + 1.0) * cos_w0);
                na2 = (A + 1.0) + (A - 1.0) * cos_w0 - sq;
                break;
            }
            case TA_FX_BIQUAD_HIGH_SHELF: {
                const double sq = 2.0 * std::sqrt(A) * alpha;
                nb0 = A * ((A + 1.0) + (A - 1.0) * cos_w0 + sq);
                nb1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cos_w0);
                nb2 = A * ((A + 1.0) + (A - 1.0) * cos_w0 - sq);
                na0 = (A + 1.0) - (A - 1.0) * cos_w0 + sq;
                na1 = 2.0 * ((A - 1.0) - (A + 1.0) * cos_w0);
                na2 = (A + 1.0) - (A - 1.0) * cos_w0 - sq;
                break;
            }
            case TA_FX_BIQUAD_ALLPASS:
                nb0 = 1.0 - alpha;
                nb1 = -2.0 * cos_w0;
                nb2 = 1.0 + alpha;
                na0 = 1.0 + alpha;
                na1 = -2.0 * cos_w0;
                na2 = 1.0 - alpha;
                break;
            default:
                break;
        }

        const double inv_a0 = 1.0 / na0;
        tb0 = nb0 * inv_a0;
        tb1 = nb1 * inv_a0;
        tb2 = nb2 * inv_a0;
        ta1 = na1 * inv_a0;
        ta2 = na2 * inv_a0;

        if (!coeffs_initialized) {
            b0 = tb0; b1 = tb1; b2 = tb2; a1 = ta1; a2 = ta2;
            coeffs_initialized = true;
        }
    }

    void processStereo(float* interleaved, uint32_t frames, int32_t sample_rate) noexcept {
        if (!active || frames == 0)
            return;

        if (params.type == TA_FX_CHORUS) {
            processChorus(interleaved, frames, sample_rate);
            return;
        }
        if (params.type == TA_FX_PHASER) {
            processPhaser(interleaved, frames, sample_rate);
            return;
        }
        if (params.type == TA_FX_AUTOWAH) {
            processAutoWah(interleaved, frames, sample_rate);
            return;
        }

        // Smooth biquad coefficients across the block (1-pole ~2ms time constant)
        const double smooth = 1.0 - std::exp(-1.0 / std::max(1.0, 0.002 * sample_rate));
        for (uint32_t i = 0; i < frames; ++i) {
            b0 += (tb0 - b0) * smooth;
            b1 += (tb1 - b1) * smooth;
            b2 += (tb2 - b2) * smooth;
            a1 += (ta1 - a1) * smooth;
            a2 += (ta2 - a2) * smooth;

            const double inL = interleaved[i * 2 + 0];
            const double inR = interleaved[i * 2 + 1];

            // Transposed Direct Form II (numerically superior under rapid cutoff modulation)
            const double outL = b0 * inL + z1L;
            z1L = b1 * inL - a1 * outL + z2L;
            z2L = b2 * inL - a2 * outL;

            const double outR = b0 * inR + z1R;
            z1R = b1 * inR - a1 * outR + z2R;
            z2R = b2 * inR - a2 * outR;

            // Flush denormals
            if (std::fabs(z1L) < 1e-25) z1L = 0.0;
            if (std::fabs(z2L) < 1e-25) z2L = 0.0;
            if (std::fabs(z1R) < 1e-25) z1R = 0.0;
            if (std::fabs(z2R) < 1e-25) z2R = 0.0;

            interleaved[i * 2 + 0] = static_cast<float>(outL);
            interleaved[i * 2 + 1] = static_cast<float>(outR);
        }
    }

private:
    void processChorus(float* interleaved, uint32_t frames, int32_t sample_rate) noexcept {
        if (delay_buf_l.empty())
            return;
        const uint32_t mask = static_cast<uint32_t>(delay_buf_l.size() - 1);
        const float dry = params.dry_mix;
        const float wet = params.wet_mix;
        const float fb = std::clamp(params.feedback, -0.7f, 0.7f);
        const double rate = params.rate_hz > 0.0f ? params.rate_hz : 1.5;
        const double base_delay_samples = std::clamp(static_cast<double>(params.delay_ms > 0.0f ? params.delay_ms : 15.0f), 1.0, 40.0) * 0.001 * sample_rate;
        const double sweep_samples = std::clamp(static_cast<double>(params.depth > 0.0f ? params.depth : 0.25f), 0.01, 1.0) * base_delay_samples * 0.5;
        const double phase_inc = 2.0 * kPi * rate / sample_rate;

        for (uint32_t i = 0; i < frames; ++i) {
            const float inL = interleaved[i * 2 + 0];
            const float inR = interleaved[i * 2 + 1];

            const double dL = base_delay_samples + sweep_samples * std::sin(lfo_phase);
            const double dR = base_delay_samples + sweep_samples * std::sin(lfo_phase + kPi * 0.5);
            lfo_phase += phase_inc;
            if (lfo_phase >= 2.0 * kPi) lfo_phase -= 2.0 * kPi;

            auto readDelay = [&](const std::vector<float>& buf, double d) -> float {
                double rp = static_cast<double>(delay_write) - d;
                while (rp < 0.0) rp += buf.size();
                uint32_t idx0 = static_cast<uint32_t>(rp) & mask;
                uint32_t idx1 = (idx0 + 1) & mask;
                float frac = static_cast<float>(rp - std::floor(rp));
                return buf[idx0] + (buf[idx1] - buf[idx0]) * frac;
            };

            const float wL = readDelay(delay_buf_l, dL);
            const float wR = readDelay(delay_buf_r, dR);

            delay_buf_l[delay_write] = inL + wL * fb;
            delay_buf_r[delay_write] = inR + wR * fb;
            delay_write = (delay_write + 1) & mask;

            interleaved[i * 2 + 0] = inL * dry + wL * wet;
            interleaved[i * 2 + 1] = inR * dry + wR * wet;
        }
    }

    void processPhaser(float* interleaved, uint32_t frames, int32_t sample_rate) noexcept {
        const float dry = params.dry_mix;
        const float wet = params.wet_mix;
        const double rate = params.rate_hz > 0.0f ? params.rate_hz : 0.7;
        const double phase_inc = 2.0 * kPi * rate / sample_rate;
        for (uint32_t i = 0; i < frames; ++i) {
            const double mod = 0.5 + 0.5 * std::sin(lfo_phase);
            lfo_phase += phase_inc;
            if (lfo_phase >= 2.0 * kPi) lfo_phase -= 2.0 * kPi;

            const double f = 300.0 + mod * 2400.0;
            const float a = static_cast<float>((1.0 - std::tan(kPi * f / sample_rate)) / (1.0 + std::tan(kPi * f / sample_rate)));

            float xL = interleaved[i * 2 + 0];
            float xR = interleaved[i * 2 + 1];
            float yL = xL, yR = xR;
            for (int s = 0; s < 4; ++s) {
                float outL = a * yL + ap_l[s];
                ap_l[s] = yL - a * outL;
                yL = outL;

                float outR = a * yR + ap_r[s];
                ap_r[s] = yR - a * outR;
                yR = outR;
            }
            interleaved[i * 2 + 0] = xL * dry + yL * wet;
            interleaved[i * 2 + 1] = xR * dry + yR * wet;
        }
    }

    void processAutoWah(float* interleaved, uint32_t frames, int32_t sample_rate) noexcept {
        const float dry = params.dry_mix;
        const float wet = params.wet_mix;
        for (uint32_t i = 0; i < frames; ++i) {
            const float inL = interleaved[i * 2 + 0];
            const float inR = interleaved[i * 2 + 1];
            const float peak = std::max(std::fabs(inL), std::fabs(inR));
            env_follower = env_follower * 0.995f + peak * 0.005f;
            const double freq = std::clamp(250.0 + env_follower * 3500.0, 150.0, sample_rate * 0.45);
            const double w0 = 2.0 * kPi * freq / sample_rate;
            const double alpha = std::sin(w0) / 4.0;
            const double inv_a0 = 1.0 / (1.0 + alpha);
            const double cb0 = alpha * inv_a0;
            const double ca1 = (-2.0 * std::cos(w0)) * inv_a0;
            const double ca2 = (1.0 - alpha) * inv_a0;

            const double outL = cb0 * inL + z1L;
            z1L = -ca1 * outL + z2L;
            z2L = -cb0 * inL - ca2 * outL;

            const double outR = cb0 * inR + z1R;
            z1R = -ca1 * outR + z2R;
            z2R = -cb0 * inR - ca2 * outR;

            interleaved[i * 2 + 0] = inL * dry + static_cast<float>(outL) * wet * 2.0f;
            interleaved[i * 2 + 1] = inR * dry + static_cast<float>(outR) * wet * 2.0f;
        }
    }
};

/*
 * Lock-free 4096-frame stereo analysis ring buffer.
 * Written by RT audio callback (zero locks, zero allocs); read by AudioThread for
 * FFT512 (CurrentAmplitudes) and peak stereo levels (AudioChannelDisplay).
 */
struct AnalysisRing {
    static constexpr uint32_t kCapacity = 4096;
    static constexpr uint32_t kMask = kCapacity - 1;

    float left[kCapacity]{};
    float right[kCapacity]{};
    std::atomic<uint64_t> write_pos{0};
    std::atomic<int32_t> sample_rate{44100};

    void clear() noexcept {
        std::memset(left, 0, sizeof(left));
        std::memset(right, 0, sizeof(right));
        write_pos.store(0, std::memory_order_release);
    }

    void pushInterleaved(const float* interleaved, uint32_t frames, int32_t sr) noexcept {
        sample_rate.store(sr, std::memory_order_relaxed);
        uint64_t wp = write_pos.load(std::memory_order_relaxed);
        for (uint32_t i = 0; i < frames; ++i) {
            const uint32_t idx = static_cast<uint32_t>((wp + i) & kMask);
            left[idx] = interleaved[i * 2 + 0];
            right[idx] = interleaved[i * 2 + 1];
        }
        write_pos.store(wp + frames, std::memory_order_release);
    }

    void computeLevels(float window_sec, float& outL, float& outR) const noexcept {
        const uint64_t wp = write_pos.load(std::memory_order_acquire);
        if (wp == 0) {
            outL = 0.0f;
            outR = 0.0f;
            return;
        }
        const int32_t sr = std::max(8000, sample_rate.load(std::memory_order_relaxed));
        uint32_t count = static_cast<uint32_t>(std::clamp(window_sec * sr, 1.0f, static_cast<float>(kCapacity - 16)));
        if (count > wp)
            count = static_cast<uint32_t>(wp);

        float maxL = 0.0f, maxR = 0.0f;
        const uint64_t start = wp - count;
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t idx = static_cast<uint32_t>((start + i) & kMask);
            const float al = std::fabs(left[idx]);
            const float ar = std::fabs(right[idx]);
            if (al > maxL) maxL = al;
            if (ar > maxR) maxR = ar;
        }
        outL = maxL;
        outR = maxR;
    }

    void computeFft512(float* out_bins_256) const noexcept {
        static thread_local signalsmith::linear::RealFFT<float> fft(512);
        static thread_local float hann[512];
        static thread_local bool hann_init = false;
        if (!hann_init) {
            for (int i = 0; i < 512; ++i)
                hann[i] = 0.5f * (1.0f - std::cos(static_cast<float>(2.0 * kPi * i / 511.0)));
            hann_init = true;
        }

        const uint64_t wp = write_pos.load(std::memory_order_acquire);
        if (wp < 512) {
            std::memset(out_bins_256, 0, 256 * sizeof(float));
            return;
        }

        float time_buf[512];
        const uint64_t start = wp - 512;
        for (uint32_t i = 0; i < 512; ++i) {
            const uint32_t idx = static_cast<uint32_t>((start + i) & kMask);
            // Mono sum matching BASS DataFlags.FFT512 default mono mix
            time_buf[i] = 0.5f * (left[idx] + right[idx]) * hann[i];
        }

        std::complex<float> freq_buf[256];
        fft.fft(time_buf, freq_buf);

        // Match BASS FFT512 magnitude scaling (~2/N with Hann compensation -> 4/N; DC/Nyquist packed at bin 0)
        constexpr float scale = 2.0f / 512.0f;
        out_bins_256[0] = std::fabs(freq_buf[0].real()) * scale;
        for (int i = 1; i < 256; ++i) {
            const float re = freq_buf[i].real();
            const float im = freq_buf[i].imag();
            out_bins_256[i] = std::sqrt(re * re + im * im) * scale;
        }
    }
};

} // namespace torii_audio
