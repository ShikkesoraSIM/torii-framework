#include "torii_audio.h"
#include "torii_audio_dsp.hpp"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <vector>

#include "torii_miniaudio_config.h"
#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"
#include "miniaudio.h"

namespace torii_audio {

static inline uint32_t readLE32(const uint8_t* p) noexcept {
    return static_cast<uint32_t>(p[0])
         | (static_cast<uint32_t>(p[1]) << 8)
         | (static_cast<uint32_t>(p[2]) << 16)
         | (static_cast<uint32_t>(p[3]) << 24);
}

static inline uint16_t readLE16(const uint8_t* p) noexcept {
    return static_cast<uint16_t>(p[0]) | (static_cast<uint16_t>(p[1]) << 8);
}

static size_t computeId3v2Skip(const uint8_t* data, size_t len) noexcept {
    if (len < 10)
        return 0;
    if (data[0] == 'I' && data[1] == 'D' && data[2] == '3'
        && data[3] < 0xFF && data[4] < 0xFF
        && (data[6] & 0x80) == 0 && (data[7] & 0x80) == 0
        && (data[8] & 0x80) == 0 && (data[9] & 0x80) == 0) {
        size_t sz = (static_cast<size_t>(data[6] & 0x7F) << 21)
                  | (static_cast<size_t>(data[7] & 0x7F) << 14)
                  | (static_cast<size_t>(data[8] & 0x7F) << 7)
                  |  static_cast<size_t>(data[9] & 0x7F);
        size_t total = 10 + sz + ((data[5] & 0x10) ? 10 : 0);
        return std::min(total, len);
    }
    return 0;
}

static bool isValidMp3FrameHeader(const uint8_t* h) noexcept {
    if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0)
        return false;
    uint8_t ver = (h[1] >> 3) & 0x03;   // 01 = reserved
    uint8_t layer = (h[1] >> 1) & 0x03; // 01 = Layer III
    uint8_t br = (h[2] >> 4) & 0x0F;    // 0x0F = bad
    uint8_t sr = (h[2] >> 2) & 0x03;    // 0x03 = reserved
    return ver != 1 && layer == 1 && br != 0x0F && sr != 0x03;
}

int32_t sniffFormatFromBytes(const uint8_t* data, size_t len) noexcept {
    if (!data || len < 4)
        return TA_FORMAT_UNKNOWN;

    // Explicitly reject WebM / Matroska, JPEG, PNG, GIF, ZIP, PDF, BM
    if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3) return TA_FORMAT_UNKNOWN;
    if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return TA_FORMAT_UNKNOWN;
    if (data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return TA_FORMAT_UNKNOWN;
    if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return TA_FORMAT_UNKNOWN;
    if (data[0] == 'P' && data[1] == 'K' && data[2] == 0x03 && data[3] == 0x04) return TA_FORMAT_UNKNOWN;
    if (len >= 12 && std::memcmp(data + 4, "ftyp", 4) == 0) return TA_FORMAT_UNKNOWN;

    // RIFF WAVE
    if (len >= 12 && std::memcmp(data, "RIFF", 4) == 0 && std::memcmp(data + 8, "WAVE", 4) == 0)
        return TA_FORMAT_WAVE;

    // OggS (check Vorbis identification header)
    if (len >= 35 && std::memcmp(data, "OggS", 4) == 0) {
        size_t scan = std::min<size_t>(len, 128);
        for (size_t i = 0; i + 7 <= scan; ++i) {
            if (data[i] == 0x01 && std::memcmp(data + i + 1, "vorbis", 6) == 0)
                return TA_FORMAT_OGG;
        }
        return TA_FORMAT_UNKNOWN;
    }

    // FLAC
    if (std::memcmp(data, "fLaC", 4) == 0)
        return TA_FORMAT_FLAC;

    // MP3 (optional ID3v2 header followed by MPEG Layer III frame sync)
    size_t offset = computeId3v2Skip(data, len);
    size_t limit = std::min(len, offset + 16384);
    for (size_t i = offset; i + 4 <= limit; ++i) {
        if (isValidMp3FrameHeader(data + i))
            return TA_FORMAT_MP3;
    }

    return TA_FORMAT_UNKNOWN;
}

static bool parseWavHeaderFallback(const uint8_t* data, size_t len, ta_audio_info* out) noexcept {
    if (len < 36 || std::memcmp(data, "RIFF", 4) != 0 || std::memcmp(data + 8, "WAVE", 4) != 0)
        return false;

    uint16_t audio_fmt = 0;
    uint16_t channels = 0;
    uint32_t sample_rate = 0;
    uint32_t byte_rate = 0;
    uint16_t block_align = 0;
    uint32_t data_bytes = 0;
    bool found_fmt = false;
    bool found_data = false;

    size_t pos = 12;
    while (pos + 8 <= len) {
        const uint8_t* cid = data + pos;
        uint32_t csize = readLE32(data + pos + 4);
        pos += 8;
        if (std::memcmp(cid, "fmt ", 4) == 0 && csize >= 16 && pos + 16 <= len) {
            audio_fmt = readLE16(data + pos);
            channels = readLE16(data + pos + 2);
            sample_rate = readLE32(data + pos + 4);
            byte_rate = readLE32(data + pos + 8);
            block_align = readLE16(data + pos + 12);
            found_fmt = true;
        } else if (std::memcmp(cid, "data", 4) == 0) {
            data_bytes = csize;
            found_data = true;
            break;
        }
        size_t step = csize + (csize & 1u);
        if (step > len - pos)
            break;
        pos += step;
    }

    if (!found_fmt || channels == 0 || sample_rate == 0)
        return false;
    // Allow PCM (1), IEEE Float (3), Extensible (0xFFFE), ALAW (6), MULAW (7), ADPCM (2, 17)
    if (audio_fmt != 1 && audio_fmt != 3 && audio_fmt != 0xFFFE && audio_fmt != 6 && audio_fmt != 7 && audio_fmt != 2 && audio_fmt != 17)
        return false;

    out->format = TA_FORMAT_WAVE;
    out->sample_rate = static_cast<int32_t>(sample_rate);
    out->channels = static_cast<int32_t>(channels);
    out->total_frames = (found_data && block_align > 0) ? static_cast<int64_t>(data_bytes / block_align) : 0;
    out->duration_ms = out->total_frames > 0 ? (static_cast<double>(out->total_frames) * 1000.0 / sample_rate) : 0.0;
    out->bitrate_kbps = byte_rate > 0 ? static_cast<int32_t>(std::llround(byte_rate * 8.0 / 1000.0)) : 0;
    return true;
}

static ma_encoding_format toMaEncoding(int32_t fmt) noexcept {
    switch (fmt) {
        case TA_FORMAT_MP3: return ma_encoding_format_mp3;
        case TA_FORMAT_OGG: return ma_encoding_format_vorbis;
        case TA_FORMAT_WAVE: return ma_encoding_format_wav;
        case TA_FORMAT_FLAC: return ma_encoding_format_flac;
        default: return ma_encoding_format_unknown;
    }
}

int32_t probeMemoryInternal(const uint8_t* data, size_t len, int32_t prescan, ta_audio_info* out_info) noexcept {
    if (!out_info)
        return 0;
    std::memset(out_info, 0, sizeof(*out_info));
    if (!data || len < 4)
        return 0;

    int32_t fmt = sniffFormatFromBytes(data, len);
    if (fmt == TA_FORMAT_UNKNOWN)
        return 0;

    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
    cfg.encodingFormat = toMaEncoding(fmt);

    ma_decoder dec;
    ma_result res = ma_decoder_init_memory(data, len, &cfg, &dec);
    if (res != MA_SUCCESS) {
        // Handle header-only 44-byte blank.wav (0-byte data chunk) cleanly!
        if (fmt == TA_FORMAT_WAVE && parseWavHeaderFallback(data, len, out_info))
            return 1;
        return 0;
    }

    const uint32_t sr = dec.outputSampleRate;
    const uint32_t ch = dec.outputChannels;
    if (sr == 0 || ch == 0) {
        ma_decoder_uninit(&dec);
        return 0;
    }

    ma_uint64 total_frames = 0;
    if (prescan) {
        // Exact frame count scan (equivalent to BassFlags.Prescan, handles truncated/VBR MP3s accurately)
        if (fmt == TA_FORMAT_MP3) {
            std::vector<float> tmp(4096 * ch);
            ma_uint64 read = 0;
            while (ma_decoder_read_pcm_frames(&dec, tmp.data(), 4096, &read) == MA_SUCCESS && read > 0) {
                total_frames += read;
            }
        } else {
            if (ma_decoder_get_length_in_pcm_frames(&dec, &total_frames) != MA_SUCCESS)
                total_frames = 0;
        }
    } else {
        if (ma_decoder_get_length_in_pcm_frames(&dec, &total_frames) != MA_SUCCESS)
            total_frames = 0;
    }

    ma_decoder_uninit(&dec);

    out_info->format = fmt;
    out_info->sample_rate = static_cast<int32_t>(sr);
    out_info->channels = static_cast<int32_t>(ch);
    out_info->total_frames = static_cast<int64_t>(total_frames);
    out_info->duration_ms = total_frames > 0 ? (static_cast<double>(total_frames) * 1000.0 / sr) : 0.0;

    if (out_info->duration_ms > 0.0) {
        size_t payload_bytes = len;
        if (fmt == TA_FORMAT_MP3) {
            size_t id3 = computeId3v2Skip(data, len);
            if (id3 < len)
                payload_bytes = len - id3;
        }
        double duration_sec = out_info->duration_ms * 0.001;
        out_info->bitrate_kbps = static_cast<int32_t>(std::llround((payload_bytes * 8.0) / (duration_sec * 1000.0)));
    }
    return 1;
}

} // namespace torii_audio

extern "C" {

TA_API int32_t ta_probe_memory(const uint8_t* data, size_t len, int32_t prescan, ta_audio_info* out_info) {
    return torii_audio::probeMemoryInternal(data, len, prescan, out_info);
}

TA_API int32_t ta_probe_callbacks(const ta_io_callbacks* cbs, void* user_data, int32_t prescan, ta_audio_info* out_info) {
    if (!cbs || !cbs->read || !cbs->length || !out_info)
        return 0;

    int64_t total_len = cbs->length(user_data);
    if (total_len <= 0) {
        std::memset(out_info, 0, sizeof(*out_info));
        return 0;
    }

    // For fast header-only probe without prescan, 256 KB is plenty unless file is smaller;
    // for prescan or small files (<= 32 MB), read full stream.
    size_t to_read = static_cast<size_t>(total_len);
    if (!prescan && to_read > 262144)
        to_read = 262144;
    if (to_read > 256 * 1024 * 1024)
        to_read = 256 * 1024 * 1024;

    if (cbs->seek)
        cbs->seek(user_data, 0);

    std::vector<uint8_t> buf(to_read);
    size_t got = 0;
    while (got < to_read) {
        int32_t chunk = static_cast<int32_t>(std::min<size_t>(to_read - got, 65536));
        int32_t n = cbs->read(user_data, buf.data() + got, chunk);
        if (n <= 0)
            break;
        got += static_cast<size_t>(n);
    }

    return torii_audio::probeMemoryInternal(buf.data(), got, prescan, out_info);
}

TA_API int32_t ta_waveform_generate(const uint8_t* data, size_t len, ta_waveform_result* out_result) {
    if (!out_result)
        return 0;
    out_result->points = nullptr;
    out_result->point_count = 0;
    out_result->channels = 0;

    if (!data || len < 4)
        return 0;

    int32_t fmt = torii_audio::sniffFormatFromBytes(data, len);
    if (fmt == TA_FORMAT_UNKNOWN)
        return 0;

    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
    cfg.encodingFormat = torii_audio::toMaEncoding(fmt);

    ma_decoder dec;
    if (ma_decoder_init_memory(data, len, &cfg, &dec) != MA_SUCCESS)
        return 0;

    const int32_t sr = static_cast<int32_t>(dec.outputSampleRate);
    const int32_t ch = static_cast<int32_t>(dec.outputChannels);
    if (sr <= 0 || ch <= 0) {
        ma_decoder_uninit(&dec);
        return 0;
    }

    // Match osu.Framework Waveform.cs exactly:
    //   resolution = 0.001f (1 ms)
    //   samplesPerPoint = (int)(sr * 0.001f * ch)
    const int32_t samples_per_point = std::max(ch, static_cast<int32_t>(sr * 0.001f * ch));
    const int32_t second_channel = ch > 1 ? 1 : 0;

    // Precompute FFT1024 bin ranges matching Waveform.cs:
    //   LOW:  20 - 100 Hz
    //   MID:  100 - 2000 Hz
    //   HIGH: 2000 - 10000 Hz
    constexpr int kFftSize = 1024;
    constexpr int kBins = kFftSize / 2;
    const double bin_hz = static_cast<double>(sr) / static_cast<double>(kFftSize);
    auto hzToBin = [&](double hz) -> int {
        return std::clamp(static_cast<int>(hz / bin_hz), 0, kBins - 1);
    };
    const int low_min = hzToBin(20.0);
    const int low_max = hzToBin(100.0);
    const int mid_min = low_max;
    const int mid_max = hzToBin(2000.0);
    const int high_min = mid_max;
    const int high_max = hzToBin(10000.0);

    signalsmith::linear::RealFFT<float> fft(kFftSize);
    float hann[kFftSize];
    for (int i = 0; i < kFftSize; ++i)
        hann[i] = 0.5f * (1.0f - std::cos(static_cast<float>(2.0 * torii_audio::kPi * i / (kFftSize - 1))));

    std::vector<ta_waveform_point> points;
    ma_uint64 est_frames = 0;
    if (ma_decoder_get_length_in_pcm_frames(&dec, &est_frames) == MA_SUCCESS && est_frames > 0) {
        size_t est_pts = static_cast<size_t>((est_frames * ch) / samples_per_point) + 16;
        if (est_pts < 10000000)
            points.reserve(est_pts);
    }

    constexpr uint32_t kChunkFrames = 4096;
    std::vector<float> pcm(kChunkFrames * ch);

    // State for single-pass point peak accumulation
    int32_t cur_sample_in_point = 0;
    float cur_peak_l = 0.0f;
    float cur_peak_r = 0.0f;

    // State for single-pass FFT1024 intensity calculation
    float fft_in[kFftSize];
    std::complex<float> fft_out[kBins];
    int fft_fill = 0;
    size_t fft_start_point = 0;

    auto flushFftBlock = [&](size_t end_point_idx) {
        if (fft_fill <= 0)
            return;
        for (int i = fft_fill; i < kFftSize; ++i)
            fft_in[i] = 0.0f;
        for (int i = 0; i < kFftSize; ++i)
            fft_in[i] *= hann[i];

        fft.fft(fft_in, fft_out);
        const float scale = 2.0f / 1024.0f;

        auto binMag = [&](int b) -> float {
            if (b == 0) return std::fabs(fft_out[0].real()) * scale;
            const float re = fft_out[b].real();
            const float im = fft_out[b].imag();
            return std::sqrt(re * re + im * im) * scale;
        };

        float low = 0.0f, mid = 0.0f, high = 0.0f;
        for (int b = low_min; b < low_max; ++b) low += binMag(b);
        for (int b = mid_min; b < mid_max; ++b) mid += binMag(b);
        for (int b = high_min; b < high_max; ++b) high += binMag(b);

        for (size_t p = fft_start_point; p < end_point_idx && p < points.size(); ++p) {
            points[p].low_intensity = low;
            points[p].mid_intensity = mid;
            points[p].high_intensity = high;
        }
        fft_fill = 0;
        fft_start_point = end_point_idx;
    };

    ma_uint64 frames_read = 0;
    while (ma_decoder_read_pcm_frames(&dec, pcm.data(), kChunkFrames, &frames_read) == MA_SUCCESS && frames_read > 0) {
        for (ma_uint64 f = 0; f < frames_read; ++f) {
            const float* frame = pcm.data() + f * ch;
            const float sL = frame[0];
            const float sR = frame[second_channel];

            const float aL = std::min(1.0f, std::fabs(sL));
            const float aR = std::min(1.0f, std::fabs(sR));
            if (aL > cur_peak_l) cur_peak_l = aL;
            if (aR > cur_peak_r) cur_peak_r = aR;

            // Mono sum for FFT
            fft_in[fft_fill++] = 0.5f * (sL + sR);

            cur_sample_in_point += ch;
            if (cur_sample_in_point >= samples_per_point) {
                ta_waveform_point pt{};
                pt.amplitude_left = cur_peak_l;
                pt.amplitude_right = cur_peak_r;
                points.push_back(pt);

                cur_sample_in_point -= samples_per_point;
                cur_peak_l = 0.0f;
                cur_peak_r = 0.0f;
            }

            if (fft_fill == kFftSize) {
                flushFftBlock(points.size());
            }
        }
    }

    if (cur_sample_in_point > 0) {
        ta_waveform_point pt{};
        pt.amplitude_left = cur_peak_l;
        pt.amplitude_right = cur_peak_r;
        points.push_back(pt);
    }
    if (fft_fill > 0) {
        flushFftBlock(points.size());
    }

    ma_decoder_uninit(&dec);

    out_result->channels = ch;
    out_result->point_count = static_cast<int32_t>(points.size());
    if (!points.empty()) {
        out_result->points = static_cast<ta_waveform_point*>(std::malloc(points.size() * sizeof(ta_waveform_point)));
        if (out_result->points) {
            std::memcpy(out_result->points, points.data(), points.size() * sizeof(ta_waveform_point));
        } else {
            out_result->point_count = 0;
            return 0;
        }
    }
    return 1;
}

TA_API void ta_waveform_free(ta_waveform_result* result) {
    if (!result)
        return;
    std::free(result->points);
    result->points = nullptr;
    result->point_count = 0;
    result->channels = 0;
}

TA_API int32_t ta_decode_memory_f32(const uint8_t* data, size_t len, float** out_pcm, int64_t* out_frames, int32_t* out_sample_rate, int32_t* out_channels) {
    if (!data || len < 4 || !out_pcm || !out_frames || !out_sample_rate || !out_channels)
        return 0;
    *out_pcm = nullptr;
    *out_frames = 0;
    *out_sample_rate = 0;
    *out_channels = 0;

    int32_t fmt = torii_audio::sniffFormatFromBytes(data, len);
    if (fmt == TA_FORMAT_UNKNOWN)
        return 0;

    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
    cfg.encodingFormat = torii_audio::toMaEncoding(fmt);
    ma_decoder dec;
    if (ma_decoder_init_memory(data, len, &cfg, &dec) != MA_SUCCESS)
        return 0;

    const int32_t sr = static_cast<int32_t>(dec.outputSampleRate);
    const int32_t ch = static_cast<int32_t>(dec.outputChannels);
    std::vector<float> pcm;
    std::vector<float> chunk(4096 * std::max(1, ch));
    ma_uint64 read = 0;
    while (ma_decoder_read_pcm_frames(&dec, chunk.data(), 4096, &read) == MA_SUCCESS && read > 0) {
        pcm.insert(pcm.end(), chunk.data(), chunk.data() + read * ch);
    }
    ma_decoder_uninit(&dec);

    float* buf = static_cast<float*>(std::malloc(pcm.size() * sizeof(float)));
    if (!buf && !pcm.empty())
        return 0;
    if (!pcm.empty())
        std::memcpy(buf, pcm.data(), pcm.size() * sizeof(float));

    *out_pcm = buf;
    *out_frames = static_cast<int64_t>(pcm.size() / std::max(1, ch));
    *out_sample_rate = sr;
    *out_channels = ch;
    return 1;
}

TA_API void ta_free_buffer(void* ptr) {
    std::free(ptr);
}

} // extern "C"
