// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Statistics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using osu.Framework.Audio;
using osu.Framework.Audio.Native;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Logging;

namespace osu.Framework.Threading
{
    public class AudioThread : GameThread
    {
        public AudioThread()
            : base(name: "Audio")
        {
            OnNewFrame += onNewFrame;
        }

        public override bool IsCurrent => ThreadSafety.IsAudioThread;

        internal sealed override void MakeCurrent()
        {
            base.MakeCurrent();

            ThreadSafety.IsAudioThread = true;
        }

        internal override IEnumerable<StatisticsCounterType> StatisticsCounters => new[]
        {
            StatisticsCounterType.TasksRun,
            StatisticsCounterType.Tracks,
            StatisticsCounterType.Samples,
            StatisticsCounterType.SChannels,
            StatisticsCounterType.Components,
            StatisticsCounterType.MixChannels,
        };

        private readonly List<AudioManager> managers = new List<AudioManager>();
        private AudioManager[] managersSnapshot = Array.Empty<AudioManager>();

        private static readonly HashSet<int> initialised_devices = new HashSet<int>();

        private static readonly GlobalStatistic<double> cpu_usage = GlobalStatistics.Get<double>("Audio", "Audio CPU%");

        private long frameCount;

        private void onNewFrame()
        {
            if (frameCount++ % 1000 == 0)
                cpu_usage.Value = ToriiAudioNative.ta_get_cpu_usage(ToriiAudioEngine.Handle);

            AudioManager[] currentManagers = System.Threading.Volatile.Read(ref managersSnapshot);
            for (int i = 0; i < currentManagers.Length; i++)
                currentManagers[i].Update();

            updateOutputLatency();
        }

        internal void RegisterManager(AudioManager manager)
        {
            manager.GlobalMixerHandle.BindTo(globalMixerHandle);

            lock (managers)
            {
                if (managers.Contains(manager))
                {
                    manager.GlobalMixerHandle.UnbindFrom(globalMixerHandle);
                    throw new InvalidOperationException($"{manager} was already registered");
                }

                managers.Add(manager);
                System.Threading.Volatile.Write(ref managersSnapshot, managers.ToArray());
            }
        }

        internal void UnregisterManager(AudioManager manager)
        {
            lock (managers)
            {
                managers.Remove(manager);
                System.Threading.Volatile.Write(ref managersSnapshot, managers.ToArray());
            }

            manager.GlobalMixerHandle.UnbindFrom(globalMixerHandle);
        }

        protected override void OnExit()
        {
            base.OnExit();

            lock (managers)
            {
                for (int i = managers.Count - 1; i >= 0; i--)
                {
                    var m = managers[i];

                    m.Dispose();
                    m.Update();
                }

                managers.Clear();
            }

            foreach (int d in initialised_devices.ToArray())
                FreeDevice(d);
        }

        #region Audio Device Initialisation (torii_audio / miniaudio)

        private readonly Bindable<int?> globalMixerHandle = new Bindable<int?>();

        /// <summary>
        /// Output latency of the initialised device, in milliseconds. Zero if unknown.
        /// </summary>
        internal readonly Bindable<double> OutputLatency = new Bindable<double>();

        /// <summary>Whether the live session holds the device exclusively.</summary>
        private bool wasapiExclusiveActive;

        /// <summary>Periodo pedido para exclusivo, en ms. 0 = automatico.</summary>
        public double ExclusiveUpdatePeriodMs;

        /// <summary>El periodo real que el dispositivo acepto, en ms.</summary>
        public double ActiveExclusivePeriodMs { get; private set; }

        /// <summary>Preferred native output sample rate (e.g., Android PropertyOutputSampleRate).</summary>
        public int PreferredSampleRate { get; set; }

        internal unsafe bool InitDevice(int deviceId, bool useExperimentalWasapi, bool exclusiveWasapi = false)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1);

            IntPtr eng = ToriiAudioEngine.Handle;

            TaDeviceMode mode;
            if (deviceId == ToriiAudioEngine.NO_SOUND_DEVICE)
                mode = TaDeviceMode.NoSound;
            else if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
            {
                if (useExperimentalWasapi && exclusiveWasapi)
                    mode = TaDeviceMode.Exclusive;
                else if (useExperimentalWasapi)
                    mode = TaDeviceMode.LowLatencyShared;
                else
                    mode = TaDeviceMode.LegacyShared;
            }
            else
            {
                mode = TaDeviceMode.LowLatencyShared;
            }

            byte* errBuf = stackalloc byte[256];
            int rc = ToriiAudioNative.ta_device_init(
                eng,
                deviceId,
                (int)mode,
                ExclusiveUpdatePeriodMs,
                PreferredSampleRate,
                out TaDeviceCaps caps,
                errBuf,
                256);

            if (rc != (int)TaError.Ok)
            {
                int errLen = 0;
                while (errLen < 256 && errBuf[errLen] != 0) errLen++;
                string errMsg = errLen > 0 ? Encoding.UTF8.GetString(errBuf, errLen) : ((TaError)rc).ToString();

                ToriiAudioEngine.LastError = (TaError)rc;
                ToriiAudioEngine.LastErrorMessage = errMsg;
                wasapiExclusiveActive = false;
                ActiveExclusivePeriodMs = 0;

                if (mode == TaDeviceMode.Exclusive)
                    Logger.Log($"Initialising torii_audio for device {deviceId} (exclusive)...FAILED, {errMsg}", level: LogLevel.Important);

                return false;
            }

            ToriiAudioEngine.LastError = TaError.Ok;
            ToriiAudioEngine.LastErrorMessage = string.Empty;
            ToriiAudioEngine.CurrentDevice = deviceId;
            ToriiAudioEngine.ActiveCaps = caps;

            wasapiExclusiveActive = caps.ActualMode == (int)TaDeviceMode.Exclusive;
            ActiveExclusivePeriodMs = wasapiExclusiveActive ? caps.ActivePeriodMs : 0;

            if (globalMixerHandle.Value == null)
                globalMixerHandle.Value = ToriiAudioNative.ta_mixer_create(eng, 1);

            initialised_devices.Add(deviceId);

            smoothedLatency = 0;
            lastLatencyUpdate = 0;
            latencyFloor = 0;
            creepSamples = 0;
            failedFlushes = 0;

            return true;
        }

        private double lastLatencyUpdate;
        private double smoothedLatency;

        public readonly BindableBool AllowLatencyRepair = new BindableBool(true);

        private double latencyFloor;
        private double lastQueueFlush;
        private int creepSamples;
        private int failedFlushes;

        private const double creep_threshold_ms = 8;
        private const int creep_samples_needed = 10;
        private const double flush_cooldown_ms = 15000;
        private const int max_failed_flushes = 3;
        private const double pre_gameplay_flush_margin_ms = 2;

        private void updateOutputLatency()
        {
            if (Clock.CurrentTime - lastLatencyUpdate < 200)
                return;

            lastLatencyUpdate = Clock.CurrentTime;

            if (initialised_devices.Count == 0)
                return;

            double latency = ToriiAudioNative.ta_get_output_latency_ms(ToriiAudioEngine.Handle);

            smoothedLatency = smoothedLatency > 0 && latency > 0
                ? smoothedLatency * 0.7 + latency * 0.3
                : latency;

            OutputLatency.Value = smoothedLatency;

            if (wasapiExclusiveActive)
                keepExclusiveQueueTight();
        }

        /// <summary>
        /// Torii: vacia la cola del exclusivo AHORA si esta por encima de su piso.
        /// </summary>
        public void FlushExclusiveQueueNow(Action? onFlushed = null)
        {
            Scheduler.Add(() =>
            {
                if (!wasapiExclusiveActive)
                    return;

                if (latencyFloor <= 0 || smoothedLatency <= 0)
                    return;

                if (smoothedLatency < latencyFloor + pre_gameplay_flush_margin_ms)
                    return;

                lastQueueFlush = Clock.CurrentTime;
                creepSamples = 0;

                if (ToriiAudioNative.ta_flush_output(ToriiAudioEngine.Handle) == 0)
                    return;

                smoothedLatency = 0;
                onFlushed?.Invoke();
            });
        }

        private void keepExclusiveQueueTight()
        {
            if (smoothedLatency <= 0)
                return;

            if (latencyFloor <= 0 || smoothedLatency < latencyFloor)
            {
                latencyFloor = smoothedLatency;
                creepSamples = 0;
                return;
            }

            if (smoothedLatency < latencyFloor + creep_threshold_ms)
            {
                creepSamples = 0;
                failedFlushes = 0;
                return;
            }

            if (++creepSamples < creep_samples_needed)
                return;

            if (!AllowLatencyRepair.Value)
            {
                creepSamples = creep_samples_needed;
                return;
            }

            if (failedFlushes >= max_failed_flushes)
            {
                latencyFloor = smoothedLatency;
                creepSamples = 0;
                failedFlushes = 0;
                return;
            }

            if (Clock.CurrentTime - lastQueueFlush < flush_cooldown_ms)
                return;

            lastQueueFlush = Clock.CurrentTime;
            creepSamples = 0;
            failedFlushes++;

            if (ToriiAudioNative.ta_flush_output(ToriiAudioEngine.Handle) == 0)
                return;

            smoothedLatency = 0;
        }

        internal void FreeDevice(int deviceId)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);

            if (ToriiAudioEngine.CurrentDevice == deviceId)
            {
                ToriiAudioNative.ta_device_free(ToriiAudioEngine.Handle);
                ToriiAudioEngine.CurrentDevice = ToriiAudioEngine.UNINITIALISED_DEVICE;
            }

            wasapiExclusiveActive = false;
            ActiveExclusivePeriodMs = 0;
            latencyFloor = 0;
            smoothedLatency = 0;
            creepSamples = 0;
            failedFlushes = 0;

            if (globalMixerHandle.Value != null)
            {
                ToriiAudioNative.ta_mixer_destroy(ToriiAudioEngine.Handle, globalMixerHandle.Value.Value);
                globalMixerHandle.Value = null;
            }

            initialised_devices.Remove(deviceId);
        }

        internal static void PreloadBass()
        {
            // No-op: torii_audio is self-contained and resolved via ToriiAudioNative.
        }

        #endregion
    }
}
