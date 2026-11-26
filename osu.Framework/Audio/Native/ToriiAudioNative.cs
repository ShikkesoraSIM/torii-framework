// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace osu.Framework.Audio.Native
{
    internal enum TaError
    {
        Ok = 0,
        Generic = 1,
        Busy = 2,
        ExclusiveNotAllowed = 3,
        FormatUnsupported = 4,
        DeviceLost = 5,
        NotInitialized = 6,
        InvalidHandle = 7,
        DecodeFailed = 8,
        OutOfMemory = 9
    }

    internal enum TaDeviceMode
    {
        NoSound = 0,
        LegacyShared = 1,
        LowLatencyShared = 2,
        Exclusive = 3
    }

    internal enum TaFxType
    {
        BiquadLowPass = 0,
        BiquadHighPass = 1,
        BiquadBandPass = 2,
        BiquadNotch = 3,
        BiquadPeakingEq = 4,
        BiquadLowShelf = 5,
        BiquadHighShelf = 6,
        BiquadAllPass = 7,
        Chorus = 8,
        Phaser = 9,
        AutoWah = 10
    }

    internal enum TaChannelState
    {
        Stopped = 0,
        Playing = 1,
        Paused = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TaDeviceInfo
    {
        public int Index;
        public int IsDefault;
        public int IsEnabled;
        public fixed byte Name[256];

        public string GetName()
        {
            fixed (byte* p = Name)
            {
                int len = 0;
                while (len < 256 && p[len] != 0)
                    len++;
                return Encoding.UTF8.GetString(p, len);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TaDeviceCaps
    {
        public int SampleRate;
        public int Channels;
        public int ActualMode;
        public int IsFloatOutput;
        public int BitDepth;
        public double ActivePeriodMs;
        public double OutputLatencyMs;
        public fixed byte BackendName[64];

        public string GetBackendName()
        {
            fixed (byte* p = BackendName)
            {
                int len = 0;
                while (len < 64 && p[len] != 0)
                    len++;
                return Encoding.UTF8.GetString(p, len);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TaFxParams
    {
        public int Type;
        public float CenterHz;
        public float Q;
        public float Bandwidth;
        public float GainDb;
        public float DryMix;
        public float WetMix;
        public float Feedback;
        public float RateHz;
        public float Depth;
        public float DelayMs;
        public float RangeOctaves;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TaTrackInfo
    {
        public int Format;
        public int SampleRate;
        public int Channels;
        public int BitrateKbps;
        public long TotalFrames;
        public double LengthMs;
        public double LastSeekableMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TaTrackClock
    {
        public double CurrentTimeMs;
        public int IsPlaying;
        public int HasCompleted;
        public uint LoopCount;
        public uint EndEventCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TaSampleInfo
    {
        public int Format;
        public int SampleRate;
        public int Channels;
        public long TotalFrames;
        public long PcmBytes;
        public double LengthMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TaAudioInfo
    {
        public int Format;
        public int SampleRate;
        public int Channels;
        public int BitrateKbps;
        public long TotalFrames;
        public double DurationMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TaWaveformResult
    {
        public IntPtr Points;
        public int PointCount;
        public int Channels;
    }

    internal static unsafe class ToriiAudioNative
    {
        private const string lib_name = "torii_audio";

        static ToriiAudioNative()
        {
            // Ensure torii_audio resolves cleanly across Desktop, RID subdirs, Android, and iOS.
            NativeLibrary.SetDllImportResolver(typeof(ToriiAudioNative).Assembly, resolveLibrary);
        }

        private static IntPtr resolveLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != lib_name)
                return IntPtr.Zero;

            if (NativeLibrary.TryLoad(lib_name, assembly, searchPath, out IntPtr handle))
                return handle;

            string baseDir = AppContext.BaseDirectory;
            string[] candidates;

            if (OperatingSystem.IsWindows())
            {
                string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
                candidates = new[]
                {
                    Path.Combine(baseDir, "torii_audio.dll"),
                    Path.Combine(baseDir, "runtimes", arch, "native", "torii_audio.dll")
                };
            }
            else if (OperatingSystem.IsMacOS())
            {
                string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
                candidates = new[]
                {
                    Path.Combine(baseDir, "libtorii_audio.dylib"),
                    Path.Combine(baseDir, "runtimes", arch, "native", "libtorii_audio.dylib")
                };
            }
            else if (OperatingSystem.IsIOS())
            {
                candidates = new[] { "@rpath/torii_audio.framework/torii_audio" };
            }
            else
            {
                string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
                candidates = new[]
                {
                    Path.Combine(baseDir, "libtorii_audio.so"),
                    Path.Combine(baseDir, "runtimes", arch, "native", "libtorii_audio.so")
                };
            }

            foreach (string path in candidates)
            {
                if (NativeLibrary.TryLoad(path, out handle))
                    return handle;
            }

            return IntPtr.Zero;
        }

        /* ---------------- Engine & Device ---------------- */

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ta_engine_create();

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_engine_destroy(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_enumerate_devices(IntPtr engine, TaDeviceInfo* outDevices, int maxCount);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_device_init(
            IntPtr engine,
            int deviceIndex,
            int mode,
            double exclusivePeriodMs,
            int preferredSampleRate,
            out TaDeviceCaps outCaps,
            byte* outError,
            int outErrorCap);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_device_free(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_device_pause(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_device_start(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_device_is_started(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_device_is_lost(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern double ta_get_output_latency_ms(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern float ta_get_cpu_usage(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_flush_output(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_get_last_error(IntPtr engine);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_get_device_sample_rate(IntPtr engine);

        /* ---------------- Mixers & DSP FX ---------------- */

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_mixer_create(IntPtr engine, int isGlobalRoot);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_mixer_destroy(IntPtr engine, int mixerHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_mixer_add_channel(IntPtr engine, int mixerHandle, int channelHandle, int startPaused);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_mixer_remove_channel(IntPtr engine, int mixerHandle, int channelHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_mixer_flush(IntPtr engine, int mixerHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_mixer_get_channels(IntPtr engine, int mixerHandle, int* outHandles, int maxCount);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_mixer_get_levels(IntPtr engine, int mixerHandle, float windowSec, out float outLeft, out float outRight);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_mixer_fx_add(IntPtr engine, int mixerHandle, ref TaFxParams fxParams, int priority);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_mixer_fx_update(IntPtr engine, int mixerHandle, int fxHandle, ref TaFxParams fxParams);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_mixer_fx_remove(IntPtr engine, int mixerHandle, int fxHandle);

        /* ---------------- Tracks ---------------- */

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern byte* ta_alloc_buffer(nuint len);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_free_buffer(void* ptr);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_track_create(IntPtr engine, byte* data, nuint len, int quickPreview, out TaTrackInfo outInfo);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_track_create_owned(IntPtr engine, byte* ownedData, nuint len, int quickPreview, out TaTrackInfo outInfo);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_track_destroy(IntPtr engine, int trackHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_track_play(IntPtr engine, int trackHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_track_pause(IntPtr engine, int trackHandle, int flushMixer);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_track_seek_ms(IntPtr engine, int trackHandle, double targetMs, out double outActualMs);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_track_set_params(IntPtr engine, int trackHandle, float volume, float balance, double frequency, double tempo, int looping, double restartPointMs);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_track_get_clock(IntPtr engine, int trackHandle, out TaTrackClock outClock);

        /* ---------------- Samples & Voices ---------------- */

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_sample_create(IntPtr engine, byte* data, nuint len, int maxConcurrency, out TaSampleInfo outInfo);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_sample_set_concurrency(IntPtr engine, int sampleHandle, int maxConcurrency);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_sample_destroy(IntPtr engine, int sampleHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_sample_channel_create(IntPtr engine, int sampleHandle, int mixerHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_sample_channel_play(IntPtr engine, int channelHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_sample_channel_stop(IntPtr engine, int channelHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_sample_channel_set_params(IntPtr engine, int channelHandle, float volume, float balance, double frequency, int looping);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_sample_channel_get_state(IntPtr engine, int channelHandle);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_sample_channel_destroy(IntPtr engine, int channelHandle);

        /* ---------------- Channel Analysis ---------------- */

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_channel_get_amplitudes(IntPtr engine, int channelHandle, out float outLeft, out float outRight, float* outFft256);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_channel_get_levels(IntPtr engine, int channelHandle, float windowSec, out float outLeft, out float outRight);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_channel_get_state(IntPtr engine, int channelHandle);

        /* ---------------- Offline Probing & Waveform ---------------- */

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_probe_memory(byte* data, nuint len, int prescan, out TaAudioInfo outInfo);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ta_waveform_generate(byte* data, nuint len, out TaWaveformResult outResult);

        [DllImport(lib_name, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ta_waveform_free(ref TaWaveformResult result);
    }

    /// <summary>
    /// Process-wide owner of the native <c>torii_audio</c> engine instance.
    /// </summary>
    public static class ToriiAudioEngine
    {
        public const int NO_SOUND_DEVICE = 0;
        public const int DEFAULT_DEVICE = 1;
        public const int UNINITIALISED_DEVICE = -1;

        private static readonly object init_lock = new object();
        private static IntPtr engineHandle;

        internal static IntPtr Handle
        {
            get
            {
                if (engineHandle != IntPtr.Zero)
                    return engineHandle;

                lock (init_lock)
                {
                    if (engineHandle == IntPtr.Zero)
                        engineHandle = ToriiAudioNative.ta_engine_create();
                    return engineHandle;
                }
            }
        }

        internal static volatile int CurrentDevice = UNINITIALISED_DEVICE;
        internal static volatile TaError LastError = TaError.Ok;
        internal static volatile string LastErrorMessage = string.Empty;
        internal static TaDeviceCaps ActiveCaps;

        /// <summary>
        /// Pauses hardware audio output (e.g. when Android Activity stops).
        /// </summary>
        public static void Pause()
        {
            if (engineHandle != IntPtr.Zero)
                ToriiAudioNative.ta_device_pause(engineHandle);
        }

        /// <summary>
        /// Resumes hardware audio output (e.g. when Android Activity restarts).
        /// </summary>
        public static void Resume()
        {
            if (engineHandle != IntPtr.Zero)
                ToriiAudioNative.ta_device_start(engineHandle);
        }

        internal static unsafe void InitNoSound()
        {
            ToriiAudioNative.ta_device_init(Handle, NO_SOUND_DEVICE, (int)TaDeviceMode.NoSound, 0, 0, out _, null, 0);
            CurrentDevice = NO_SOUND_DEVICE;
        }

        /// <summary>
        /// Zero-LOH helper: streams a .NET <see cref="Stream"/> directly into a native buffer using a small pooled
        /// 64 KB staging array so loading 5-15 MB MP3/OGG tracks causes ZERO Large Object Heap GC pressure!
        /// Caller owns the returned pointer until passed to <c>ta_track_create_owned</c> or freed with <c>ta_free_buffer</c>.
        /// </summary>
        internal static unsafe bool ReadStreamToNative(Stream stream, out byte* nativePtr, out nuint length)
        {
            nativePtr = null;
            length = 0;

            if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> seg) && seg.Count > 0)
            {
                byte* dst = ToriiAudioNative.ta_alloc_buffer((nuint)seg.Count);
                if (dst == null)
                    return false;

                fixed (byte* src = &seg.Array![seg.Offset])
                    Buffer.MemoryCopy(src, dst, seg.Count, seg.Count);

                nativePtr = dst;
                length = (nuint)seg.Count;
                return true;
            }

            if (stream.CanSeek)
                stream.Seek(0, SeekOrigin.Begin);

            const int chunk_size = 65536;
            byte[] chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(chunk_size);
            try
            {
                if (stream.CanSeek && stream.Length > 0 && stream.Length < int.MaxValue)
                {
                    int cap = (int)stream.Length;
                    byte* dst = ToriiAudioNative.ta_alloc_buffer((nuint)cap);
                    if (dst == null)
                        return false;

                    int total = 0;
                    while (total < cap)
                    {
                        int n = stream.Read(chunk, 0, Math.Min(chunk_size, cap - total));
                        if (n <= 0)
                            break;
                        fixed (byte* src = chunk)
                            Buffer.MemoryCopy(src, dst + total, cap - total, n);
                        total += n;
                    }

                    if (total < 4)
                    {
                        ToriiAudioNative.ta_free_buffer(dst);
                        return false;
                    }

                    nativePtr = dst;
                    length = (nuint)total;
                    return true;
                }
                else
                {
                    nuint cap = 65536;
                    byte* dst = ToriiAudioNative.ta_alloc_buffer(cap);
                    if (dst == null)
                        return false;

                    nuint total = 0;
                    while (true)
                    {
                        int n = stream.Read(chunk, 0, chunk_size);
                        if (n <= 0)
                            break;

                        if (total + (nuint)n > cap)
                        {
                            nuint newCap = Math.Max(cap * 2, total + (nuint)n);
                            byte* grown = ToriiAudioNative.ta_alloc_buffer(newCap);
                            if (grown == null)
                            {
                                ToriiAudioNative.ta_free_buffer(dst);
                                return false;
                            }
                            Buffer.MemoryCopy(dst, grown, (long)newCap, (long)total);
                            ToriiAudioNative.ta_free_buffer(dst);
                            dst = grown;
                            cap = newCap;
                        }

                        fixed (byte* src = chunk)
                            Buffer.MemoryCopy(src, dst + total, (long)(cap - total), n);
                        total += (nuint)n;
                    }

                    if (total < 4)
                    {
                        ToriiAudioNative.ta_free_buffer(dst);
                        return false;
                    }

                    nativePtr = dst;
                    length = total;
                    return true;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        /// <summary>
        /// Helper for reading a .NET Stream into a contiguous byte array for zero-callback native decoding.
        /// </summary>
        internal static byte[] ReadStreamBytes(Stream stream)
        {
            if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> seg) && seg.Offset == 0 && seg.Count == seg.Array!.Length)
                return seg.Array;

            if (stream.CanSeek)
                stream.Seek(0, SeekOrigin.Begin);

            using var copy = new MemoryStream(stream.CanSeek && stream.Length > 0 && stream.Length < int.MaxValue ? (int)stream.Length : 4096);
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
