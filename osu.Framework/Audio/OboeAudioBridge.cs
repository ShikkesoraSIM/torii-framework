// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Debug = System.Diagnostics.Debug;

namespace osu.Framework.Audio
{
    /// <summary>
    /// Managed wrapper around the native Oboe low-latency audio bridge.
    /// Provides accurate audio output latency measurement for rhythm-game synchronisation.
    /// </summary>
    public class OboeAudioBridge : IDisposable
    {
        private const string lib_name = "osu_native";

        private IntPtr nativePtr;
        private volatile bool disposed;

        private static readonly bool native_loaded;

        static OboeAudioBridge()
        {
            try
            {
                // Try safe load (null search path) first to avoid Samsung security crashes
                bool success = NativeLibrary.TryLoad(lib_name, typeof(OboeAudioBridge).Assembly, null, out _);

                if (!success)
                {
                    Debug.WriteLine("[osu!] Primary native library load (safe path) failed, attempting standard load...");
                    // Fallback to standard library loading which might search more paths but is riskier on some devices
                    success = NativeLibrary.TryLoad(lib_name, out _);
                }

                native_loaded = success;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to load native library for Oboe: {e.Message}");
                native_loaded = false;
            }

            if (!native_loaded)
                Debug.WriteLine("[osu!] Native library not found, Oboe unavailable");
            else
                Debug.WriteLine("[osu!] Native library loaded successfully for Oboe");
        }

        public static OboeAudioBridge? Create(int sampleRate = 0)
        {
            if (!native_loaded)
            {
                Debug.WriteLine("[osu!] Oboe Create() skipped —native library not loaded");
                return null;
            }

            try
            {
                IntPtr ptr = nOboeCreate(sampleRate);

                if (ptr == IntPtr.Zero)
                {
                    Debug.WriteLine($"[osu!] nOboeCreate({sampleRate}) returned null —stream open failed");
                    return null;
                }

                return new OboeAudioBridge(ptr);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] nOboeCreate failed: {e.Message}");
                return null;
            }
        }

        private OboeAudioBridge(IntPtr ptr) => nativePtr = ptr;

        public bool Start()
        {
            if (disposed || nativePtr == IntPtr.Zero) return false;
            try { return nOboeStart(nativePtr) != 0; }
            catch { return false; }
        }

        public void Stop()
        {
            if (disposed || nativePtr == IntPtr.Zero) return;
            try { nOboeStop(nativePtr); }
            catch { }
        }

        public double GetOutputLatencyMs()
        {
            if (disposed || nativePtr == IntPtr.Zero) return -1;
            try { return nOboeGetLatencyMs(nativePtr); }
            catch { return -1; }
        }

        /// <summary>
        /// Returns a fresh hardware-latency reading by calling calculateLatencyMillis()
        /// directly on the stream, bypassing the cached value that onAudioReady only
        /// refreshes every ~1 second. Use this for measurement loops that need
        /// accurate per-poll readings.
        /// </summary>
        public double GetInstantLatencyMs()
        {
            if (disposed || nativePtr == IntPtr.Zero) return -1;
            try { return nOboeGetInstantLatencyMs(nativePtr); }
            catch { return -1; }
        }

        public string GetLastErrorMessage()
        {
            if (disposed || nativePtr == IntPtr.Zero) return "Not initialized";
            try
            {
                IntPtr ptr = nOboeGetLastErrorMessage(nativePtr);
                return ptr == IntPtr.Zero ? "Unknown" : Marshal.PtrToStringAnsi(ptr) ?? "Unknown";
            }
            catch { return "P/Invoke error"; }
        }

        public bool IsActive
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;
                try { return nOboeIsActive(nativePtr) != 0; }
                catch { return false; }
            }
        }

        public int SampleRate
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return 0;
                try { return nOboeGetSampleRate(nativePtr); }
                catch { return 0; }
            }
        }

        public int FramesPerBurst
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return 0;
                try { return nOboeGetFramesPerBurst(nativePtr); }
                catch { return 0; }
            }
        }

        public int BufferSizeInFrames
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return 0;
                try { return nOboeGetBufferSizeInFrames(nativePtr); }
                catch { return 0; }
            }
        }

        public bool IsAAudio
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;
                try { return nOboeIsAAudio(nativePtr) != 0; }
                catch { return false; }
            }
        }

        public bool IsMMap
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;
                try { return nOboeIsMMap(nativePtr) != 0; }
                catch { return false; }
            }
        }

        public void SetProvider(IntPtr provider)
        {
            if (disposed || nativePtr == IntPtr.Zero) return;
            try { nOboeSetProvider(nativePtr, provider); }
            catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (nativePtr != IntPtr.Zero)
            {
                try { nOboeDestroy(nativePtr); }
                catch { }
                nativePtr = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        ~OboeAudioBridge() => Dispose();

        [DllImport(lib_name)] private static extern IntPtr nOboeCreate(int sampleRate);
        [DllImport(lib_name)] private static extern void nOboeDestroy(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nOboeStart(IntPtr ptr);
        [DllImport(lib_name)] private static extern void nOboeStop(IntPtr ptr);
        [DllImport(lib_name)] private static extern double nOboeGetLatencyMs(IntPtr ptr);
        [DllImport(lib_name)] private static extern double nOboeGetInstantLatencyMs(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nOboeIsActive(IntPtr ptr);
        [DllImport(lib_name)] private static extern int nOboeGetSampleRate(IntPtr ptr);
        [DllImport(lib_name)] private static extern int nOboeGetFramesPerBurst(IntPtr ptr);
        [DllImport(lib_name)] private static extern int nOboeGetBufferSizeInFrames(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nOboeIsAAudio(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nOboeIsMMap(IntPtr ptr);
        [DllImport(lib_name)] private static extern void nOboeSetProvider(IntPtr ptr, IntPtr provider);
        [DllImport(lib_name)] private static extern IntPtr nOboeGetLastErrorMessage(IntPtr ptr);
        [DllImport(lib_name)] internal static extern byte nSetThreadAffinity(int coreMask);
        [DllImport(lib_name)] internal static extern int nGetBigCoreMask();
        [DllImport(lib_name)] internal static extern int nTameBackgroundThreads(int littleCoreMask);
        [DllImport(lib_name)] internal static extern IntPtr nADPFCreateSession(long targetDurationNanos);
        [DllImport(lib_name)] internal static extern void nADPFReportActualDuration(IntPtr sessionPtr, long actualDurationNanos);
        [DllImport(lib_name)] internal static extern void nADPFUpdateTargetDuration(IntPtr sessionPtr, long targetDurationNanos);
        [DllImport(lib_name)] internal static extern void nADPFCloseSession(IntPtr sessionPtr);
        [DllImport(lib_name)] internal static extern void nADPFSetPreferPowerEfficiency(IntPtr sessionPtr, byte preferEfficiency);
        [DllImport(lib_name)] internal static extern void nInstallCrashHandler([MarshalAs(UnmanagedType.LPUTF8Str)] string? logPath);
        [DllImport(lib_name)] internal static extern void nReinstallCrashHandler();
    }
}
