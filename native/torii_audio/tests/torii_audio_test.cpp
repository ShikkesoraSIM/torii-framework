#include "torii_audio.h"
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <thread>
#include <vector>

#define CHECK(expr) do { if (!(expr)) { fprintf(stderr, "FAIL at line %d: %s\n", __LINE__, #expr); std::exit(1); } } while (0)

static std::vector<uint8_t> readFile(const char* path) {
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f) return {};
    auto sz = f.tellg();
    if (sz <= 0) return {};
    std::vector<uint8_t> buf(static_cast<size_t>(sz));
    f.seekg(0);
    f.read(reinterpret_cast<char*>(buf.data()), sz);
    return buf;
}

static std::vector<uint8_t> makeSineWav(int sampleRate, double seconds, double freqHz) {
    int totalFrames = static_cast<int>(sampleRate * seconds);
    int dataSize = totalFrames * 2 * 2;
    std::vector<uint8_t> out(44 + dataSize);
    auto w16 = [&](size_t off, uint16_t v) { out[off] = v & 0xFF; out[off + 1] = (v >> 8) & 0xFF; };
    auto w32 = [&](size_t off, uint32_t v) { out[off] = v & 0xFF; out[off + 1] = (v >> 8) & 0xFF; out[off + 2] = (v >> 16) & 0xFF; out[off + 3] = (v >> 24) & 0xFF; };
    std::memcpy(out.data(), "RIFF", 4);
    w32(4, 36 + dataSize);
    std::memcpy(out.data() + 8, "WAVEfmt ", 8);
    w32(16, 16);
    w16(20, 1); // PCM
    w16(22, 2); // stereo
    w32(24, sampleRate);
    w32(28, sampleRate * 4);
    w16(32, 4);
    w16(34, 16);
    std::memcpy(out.data() + 36, "data", 4);
    w32(40, dataSize);
    int16_t* samples = reinterpret_cast<int16_t*>(out.data() + 44);
    for (int i = 0; i < totalFrames; ++i) {
        double v = std::sin(2.0 * 3.14159265358979323846 * freqHz * i / sampleRate) * 0.5;
        int16_t s = static_cast<int16_t>(v * 32767.0);
        samples[i * 2 + 0] = s;
        samples[i * 2 + 1] = s;
    }
    return out;
}

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);
    printf("=== torii_audio Native Verification Suite ===\n");

    ta_engine* eng = ta_engine_create();
    CHECK(eng != nullptr);

    // 1. Device enumeration
    ta_device_info devs[32]{};
    int32_t dev_count = ta_enumerate_devices(eng, devs, 32);
    printf("[1] Enumerated %d playback device(s):\n", dev_count);
    for (int i = 0; i < dev_count && i < 8; ++i)
        printf("    [%d] %s%s\n", devs[i].index, devs[i].name, devs[i].is_default ? " (DEFAULT)" : "");

    // 2. Initialize NoSound software pump
    ta_device_caps caps{};
    char err[256]{};
    int32_t rc = ta_device_init(eng, 0, TA_MODE_NO_SOUND, 0.0, 0, &caps, err, sizeof(err));
    CHECK(rc == TA_OK);
    printf("[2] NoSound init OK: %s %dHz %dch\n", caps.backend_name, caps.sample_rate, caps.channels);

    // 3. Mixers + Biquad FX
    int32_t track_mixer = ta_mixer_create(eng, 0);
    int32_t sample_mixer = ta_mixer_create(eng, 0);
    CHECK(track_mixer != 0 && sample_mixer != 0);

    ta_fx_params bqf{};
    bqf.type = TA_FX_BIQUAD_LOWPASS;
    bqf.center_hz = 1200.0f;
    bqf.q = 0.7f;
    int32_t fx = ta_mixer_fx_add(eng, track_mixer, &bqf, 0);
    CHECK(fx != 0);
    bqf.center_hz = 5000.0f;
    CHECK(ta_mixer_fx_update(eng, track_mixer, fx, &bqf) == 1);

    // 4. Track playback, Seek, DT Tempo (Signalsmith Stretch!), Reverse, FFT
    auto wav = makeSineWav(44100, 3.0, 440.0);
    ta_track_info tinfo{};
    int32_t trk = ta_track_create(eng, wav.data(), wav.size(), 0, &tinfo);
    CHECK(trk != 0);
    CHECK(std::fabs(tinfo.length_ms - 3000.0) < 5.0);
    ta_mixer_add_channel(eng, track_mixer, trk, 1);

    // Seek past end must return 0 (TrackBassTest contract!)
    double actual = 0.0;
    CHECK(ta_track_seek_ms(eng, trk, tinfo.length_ms, &actual) == 0);
    CHECK(ta_track_seek_ms(eng, trk, -10.0, &actual) == 0);
    CHECK(ta_track_seek_ms(eng, trk, 500.0, &actual) == 1);
    CHECK(std::fabs(actual - 500.0) < 1.0);

    // Normal 1.0x playback
    CHECK(ta_track_play(eng, trk) == 1);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    ta_track_clock clk1{};
    ta_track_get_clock(eng, trk, &clk1);
    printf("[4a] 1.0x playback after 150ms from 500ms -> CurrentTime=%.2fms playing=%d\n", clk1.current_time_ms, clk1.is_playing);
    CHECK(clk1.current_time_ms > 580.0 && clk1.current_time_ms < 750.0);

    // Amplitudes + FFT512 check
    float ampL = 0, ampR = 0, fft256[256]{};
    int32_t has_amp = ta_channel_get_amplitudes(eng, trk, &ampL, &ampR, fft256);
    CHECK(has_amp == 1 && ampL > 0.1f && ampR > 0.1f);
    printf("[4b] Amplitudes L=%.3f R=%.3f bin5(440Hz)=%.4f\n", ampL, ampR, fft256[5]);

    // DT 1.5x Signalsmith Stretch test
    ta_track_set_params(eng, trk, 1.0f, 0.0f, 1.0, 1.5, 0, 0.0);
    double t_before_dt = clk1.current_time_ms;
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    ta_track_clock clk2{};
    ta_track_get_clock(eng, trk, &clk2);
    printf("[4c] DT 1.5x after 200ms -> CurrentTime=%.2fms (delta=%.2fms)\n", clk2.current_time_ms, clk2.current_time_ms - t_before_dt);
    CHECK(clk2.current_time_ms > t_before_dt + 150.0);

    // Reverse playback test
    ta_track_set_params(eng, trk, 1.0f, 0.0f, -1.0, 1.0, 0, 0.0);
    double t_before_rev = clk2.current_time_ms;
    std::this_thread::sleep_for(std::chrono::milliseconds(120));
    ta_track_clock clk3{};
    ta_track_get_clock(eng, trk, &clk3);
    printf("[4d] Reverse -1.0x after 120ms -> CurrentTime=%.2fms (delta=%.2fms)\n", clk3.current_time_ms, clk3.current_time_ms - t_before_rev);
    CHECK(clk3.current_time_ms < t_before_rev - 40.0);

    // 5. Polyphonic hitsounds stress test (64 voices with concurrency = 6)
    auto hs = makeSineWav(44100, 0.25, 880.0);
    ta_sample_info sinfo{};
    int32_t smp = ta_sample_create(eng, hs.data(), hs.size(), 6, &sinfo);
    CHECK(smp != 0);
    std::vector<int32_t> chs;
    for (int i = 0; i < 64; ++i) {
        int32_t ch = ta_sample_channel_create(eng, smp, sample_mixer);
        CHECK(ch != 0);
        ta_sample_channel_play(eng, ch);
        chs.push_back(ch);
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    int playing_voices = 0;
    for (int32_t ch : chs)
        if (ta_sample_channel_get_state(eng, ch) == TA_STATE_PLAYING)
            ++playing_voices;
    printf("[5] 64 hitsound triggers with concurrency=6 -> %d active voices (CPU=%.2f%%)\n", playing_voices, ta_get_cpu_usage(eng));
    CHECK(playing_voices <= 6 && playing_voices >= 1);

    // 6. Offline deviceless Waveform + Probing on real osu! test files
    ta_waveform_result wf{};
    CHECK(ta_waveform_generate(wav.data(), wav.size(), &wf) == 1);
    printf("[6a] Synthetic Waveform generated: %d points, %dch (pt[100] L=%.3f low=%.3f mid=%.3f)\n",
           wf.point_count, wf.channels, wf.points[100].amplitude_left, wf.points[100].low_intensity, wf.points[100].mid_intensity);
    CHECK(wf.point_count >= 2990 && wf.point_count <= 3010);
    ta_waveform_free(&wf);

    // Test real osu.Game.Tests audio fixtures if present
    const char* sampleDir = "E:/ToriiBuilds/bass-replacement/torii-osu/osu.Game.Tests/Resources/Samples/";
    auto testProbe = [&](const char* name, int expectedFmt, int prescan) {
        std::string p = std::string(sampleDir) + name;
        auto bytes = readFile(p.c_str());
        if (bytes.empty()) {
            printf("    (skip missing fixture %s)\n", name);
            return;
        }
        ta_audio_info info{};
        int ok = ta_probe_memory(bytes.data(), bytes.size(), prescan, &info);
        printf("[6b] Probe %-22s -> ok=%d fmt=%d sr=%d ch=%d dur=%.2fms br=%dkbps\n",
               name, ok, info.format, info.sample_rate, info.channels, info.duration_ms, info.bitrate_kbps);
        CHECK(info.format == expectedFmt);
    };
    testProbe("blank.wav", TA_FORMAT_WAVE, 1);
    testProbe("corrupt.wav", TA_FORMAT_UNKNOWN, 0);
    testProbe("test-sample.webm", TA_FORMAT_UNKNOWN, 0);
    testProbe("test-sample-cut.mp3", TA_FORMAT_MP3, 1);
    testProbe("test-sample.ogg", TA_FORMAT_OGG, 1);

    // 7. Live Windows WASAPI Low-Latency Shared probe
    if (dev_count > 0) {
        rc = ta_device_init(eng, 1, TA_MODE_LOW_LATENCY_SHARED, 0.0, 0, &caps, err, sizeof(err));
        printf("[7] Live WASAPI Shared init -> rc=%d backend=%s %dHz %dch period=%.2fms latency=%.2fms err='%s'\n",
               rc, caps.backend_name, caps.sample_rate, caps.channels, caps.active_period_ms, caps.output_latency_ms, err);
        CHECK(rc == TA_OK);
        std::this_thread::sleep_for(std::chrono::milliseconds(80));
        printf("    Live measured latency=%.2fms CPU=%.2f%%\n", ta_get_output_latency_ms(eng), ta_get_cpu_usage(eng));
    }

    for (int32_t ch : chs) ta_sample_channel_destroy(eng, ch);
    ta_sample_destroy(eng, smp);
    ta_track_destroy(eng, trk);
    ta_mixer_fx_remove(eng, track_mixer, fx);
    ta_mixer_destroy(eng, track_mixer);
    ta_mixer_destroy(eng, sample_mixer);
    ta_engine_destroy(eng);

    printf("=== ALL TORII_AUDIO NATIVE TESTS PASSED ===\n");
    return 0;
}
