// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Statistics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Logging;
using osu.Framework.Platform.Linux.Native;

namespace osu.Framework.Threading
{
    public class AudioThread : GameThread
    {
        public AudioThread()
            : base(name: "Audio")
        {
            OnNewFrame += onNewFrame;
            PreloadBass();
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

        private static readonly HashSet<int> initialised_devices = new HashSet<int>();

        private static readonly GlobalStatistic<double> cpu_usage = GlobalStatistics.Get<double>("Audio", "Bass CPU%");

        private long frameCount;

        private void onNewFrame()
        {
            if (frameCount++ % 1000 == 0)
                cpu_usage.Value = Bass.CPUUsage;

            lock (managers)
            {
                for (int i = 0; i < managers.Count; i++)
                {
                    var m = managers[i];
                    m.Update();
                }
            }

            updateOutputLatency();
        }

        internal void RegisterManager(AudioManager manager)
        {
            lock (managers)
            {
                if (managers.Contains(manager))
                    throw new InvalidOperationException($"{manager} was already registered");

                managers.Add(manager);
            }

            manager.GlobalMixerHandle.BindTo(globalMixerHandle);
        }

        internal void UnregisterManager(AudioManager manager)
        {
            lock (managers)
                managers.Remove(manager);

            manager.GlobalMixerHandle.UnbindFrom(globalMixerHandle);
        }

        protected override void OnExit()
        {
            base.OnExit();

            lock (managers)
            {
                // AudioManagers are iterated over backwards since disposal will unregister and remove them from the list.
                for (int i = managers.Count - 1; i >= 0; i--)
                {
                    var m = managers[i];

                    m.Dispose();

                    // Audio component disposal (including the AudioManager itself) is scheduled and only runs when the AudioThread updates.
                    // But the AudioThread won't run another update since it's exiting, so an update must be performed manually in order to finish the disposal.
                    m.Update();
                }

                managers.Clear();
            }

            // Safety net to ensure we have freed all devices before exiting.
            // This is mainly required for device-lost scenarios.
            // See https://github.com/ppy/osu-framework/pull/3378 for further discussion.
            foreach (int d in initialised_devices.ToArray())
                FreeDevice(d);
        }

        #region BASS Initialisation

        // TODO: All this bass init stuff should probably not be in this class.

        private WasapiProcedure? wasapiProcedure;
        private WasapiNotifyProcedure? wasapiNotifyProcedure;

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        private readonly Bindable<int?> globalMixerHandle = new Bindable<int?>();

        /// <summary>
        /// Output latency of the initialised device, in milliseconds. Zero if unknown.
        /// </summary>
        internal readonly Bindable<double> OutputLatency = new Bindable<double>();

        internal bool InitDevice(int deviceId, bool useExperimentalWasapi, bool exclusiveWasapi = false)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1); // The real device ID should always be used, as the -1 device has special cases which are hard to work with.

            // An exclusive-mode device belongs to us and nobody else, and "nobody else"
            // includes BASS's own re-initialisation below: leaving it held makes that fail,
            // which then cascades into WASAPI being switched off entirely. Let go first.
            freeWasapi();

            // Try to initialise the device, or request a re-initialise.
            if (!Bass.Init(deviceId, Flags: (DeviceInitFlags)128)) // 128 == BASS_DEVICE_REINIT
                return false;

            if (useExperimentalWasapi)
                attemptWasapiInitialisation(exclusiveWasapi);
            else
                freeWasapi();

            initialised_devices.Add(deviceId);

            // el modo cambio: la medicion vieja ya no vale nada.
            smoothedLatency = 0;
            lastLatencyUpdate = 0;

            // en legacy, bass entrega a una sesion compartida de windows que corre por
            // adentro y que el no reporta: dos periodos del dispositivo de colchon, que
            // es como funciona una sesion event-driven. se estima una vez aca porque
            // enumerar dispositivos no es gratis.
            legacySessionEstimateMs = 0;

            if (!useExperimentalWasapi)
            {
                try
                {
                    int wasapiDevice = findWasapiDevice();

                    if (wasapiDevice >= 0 && BassWasapi.GetDeviceInfo(wasapiDevice, out WasapiDeviceInfo info) && info.DefaultUpdatePeriod > 0)
                        legacySessionEstimateMs = info.DefaultUpdatePeriod * 2 * 1000;
                }
                catch
                {
                }
            }

            return true;
        }

        private double lastLatencyUpdate;
        private double smoothedLatency;
        private double legacySessionEstimateMs;

        /// <summary>
        /// How far behind the audio output currently is, in milliseconds.
        ///
        /// This is measured, not assumed. Under WASAPI it's how much audio is sitting in
        /// the device queue waiting to be heard, which is the honest answer and moves
        /// around; the buffer SIZE is not the same thing and reads far too high. Without
        /// WASAPI, BASS reports its own measured playback delay.
        /// </summary>
        private void updateOutputLatency()
        {
            // sampling this every audio frame would be noise; a few times a second is
            // plenty for something a human reads off the screen.
            if (Clock.CurrentTime - lastLatencyUpdate < 200)
                return;

            lastLatencyUpdate = Clock.CurrentTime;

            double latency = 0;

            try
            {
                if (globalMixerHandle.Value != null && BassWasapi.GetInfo(out WasapiInfo info) && info.Frequency > 0)
                {
                    int queuedBytes = BassWasapi.GetData(IntPtr.Zero, (int)DataFlags.Available);

                    if (queuedBytes > 0)
                    {
                        int bytesPerSample = info.Format switch
                        {
                            WasapiFormat.Float => 4,
                            WasapiFormat.Bit32 => 4,
                            WasapiFormat.Bit24 => 3,
                            WasapiFormat.Bit16 => 2,
                            WasapiFormat.Bit8 => 1,
                            _ => 2,
                        };

                        latency = queuedBytes / ((double)info.Frequency * Math.Max(1, info.Channels) * bytesPerSample) * 1000;
                    }
                }
                else if (Bass.GetInfo(out BassInfo bassInfo))
                {
                    // el camino entero: lo que bass estima del dispositivo + su propio
                    // buffer y periodo de update + la sesion compartida de windows que
                    // usa por adentro y no cuenta. sin ese ultimo termino el numero daba
                    // 25ms y decia que legacy era mas rapido que wasapi compartido, que
                    // es exactamente al reves de lo que se siente jugando.
                    latency = bassInfo.Latency + Bass.DeviceBufferLength + Bass.UpdatePeriod + legacySessionEstimateMs;
                }
            }
            catch
            {
                latency = 0;
            }

            // the wasapi reading swings by a period or two between samples; smooth it so
            // the number on screen is readable instead of flickering.
            smoothedLatency = smoothedLatency > 0 && latency > 0
                ? smoothedLatency * 0.7 + latency * 0.3
                : latency;

            OutputLatency.Value = smoothedLatency;
        }

        internal void FreeDevice(int deviceId)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);

            int selectedDevice = Bass.CurrentDevice;

            if (canSelectDevice(deviceId))
            {
                Bass.CurrentDevice = deviceId;
                Bass.Free();
            }

            freeWasapi();

            if (selectedDevice != deviceId && canSelectDevice(selectedDevice))
                Bass.CurrentDevice = selectedDevice;

            initialised_devices.Remove(deviceId);

            static bool canSelectDevice(int deviceId) => Bass.GetDeviceInfo(deviceId, out var deviceInfo) && deviceInfo.IsInitialized;
        }

        /// <summary>
        /// Makes BASS available to be consumed.
        /// </summary>
        internal static void PreloadBass()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Linux)
            {
                // required for the time being to address libbass_fx.so load failures (see https://github.com/ppy/osu/issues/2852)
                Library.Load("libbass.so", Library.LoadFlags.RTLD_LAZY | Library.LoadFlags.RTLD_GLOBAL);
            }
        }

        private bool attemptWasapiInitialisation(bool exclusive)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            Logger.Log("Attempting local BassWasapi initialisation");

            int wasapiDevice = findWasapiDevice();

            // To keep things in a sane state let's only keep one device initialised via wasapi.
            freeWasapi();

            // Drivers are third-party code we don't control, and this runs on the audio
            // thread where an exception is fatal. Failing to initialise must stay a
            // "return false" (the caller falls back), never a crash.
            try
            {
                return initWasapi(wasapiDevice, exclusive);
            }
            catch (Exception e)
            {
                Logger.Log($"BassWasapi initialisation threw for device {wasapiDevice}: {e.Message}", level: LogLevel.Error);

                try
                {
                    freeWasapi();
                }
                catch { }

                return false;
            }
        }

        private bool initWasapi(int wasapiDevice, bool exclusive)
        {
            // This is intentionally initialised inline and stored to a field.
            // If we don't do this, it gets GC'd away.
            wasapiProcedure = (buffer, length, _) =>
            {
                if (globalMixerHandle.Value == null)
                    return 0;

                return Bass.ChannelGetData(globalMixerHandle.Value!.Value, buffer, length);
            };
            wasapiNotifyProcedure = (notify, device, _) => Scheduler.Add(() =>
            {
                if (notify == WasapiNotificationType.DefaultOutput)
                {
                    freeWasapi();
                    initWasapi(device, exclusive);
                }
            });

            bool initialised = exclusive
                ? initWasapiExclusive(wasapiDevice)
                : initWasapiShared(wasapiDevice);

            if (!initialised)
                return false;

            BassWasapi.GetInfo(out var wasapiInfo);
            globalMixerHandle.Value = BassMix.CreateMixerStream(wasapiInfo.Frequency, wasapiInfo.Channels, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
            BassWasapi.Start();

            BassWasapi.SetNotify(wasapiNotifyProcedure);
            return true;
        }

        /// <summary>
        /// The WASAPI device index matching the current BASS device, or -1.
        /// </summary>
        private static int findWasapiDevice()
        {
            int wasapiDevice = -1;

            // WASAPI device indices don't match normal BASS devices.
            // Each device is listed multiple times with each supported channel/frequency pair.
            //
            // Working backwards to find the correct device is how bass does things internally (see BassWasapi.GetBassDevice).
            if (Bass.CurrentDevice > 0)
            {
                string driver = Bass.GetDeviceInfo(Bass.CurrentDevice).Driver;

                if (!string.IsNullOrEmpty(driver))
                {
                    // In the normal execution case, BassWasapi.GetDeviceInfo will return false as soon as we reach the end of devices.
                    // This while condition is just a safety to avoid looping forever.
                    // It's intentionally quite high because if a user has many audio devices, this list can get long.
                    //
                    // Retrieving device info here isn't free. In the future we may want to investigate a better method.
                    while (wasapiDevice < 16384)
                    {
                        if (!BassWasapi.GetDeviceInfo(++wasapiDevice, out WasapiDeviceInfo info))
                            break;

                        if (info.ID == driver)
                            break;
                    }
                }
            }

            return wasapiDevice;
        }

        private bool initWasapiShared(int wasapiDevice)
        {
            bool initialised = BassWasapi.Init(wasapiDevice, Procedure: wasapiProcedure, Flags: WasapiInitFlags.EventDriven | WasapiInitFlags.AutoFormat, Buffer: 0f, Period: float.Epsilon);
            Logger.Log($"Initialising BassWasapi for device {wasapiDevice} (shared)...{(initialised ? "success!" : $"FAILED ({Bass.LastError})")}");
            return initialised;
        }

        /// <summary>
        /// Exclusive mode is fussy in a way shared mode isn't: the device only accepts
        /// formats it actually supports, and won't run at an arbitrarily small period
        /// either. Both vary per device (every headset is different), so candidates are
        /// tried in order of preference until one initialises.
        /// </summary>
        private bool initWasapiExclusive(int wasapiDevice)
        {
            BassWasapi.GetDeviceInfo(wasapiDevice, out WasapiDeviceInfo info);

            var formats = new List<(int frequency, int channels)>();

            // whatever the device is already running at is the safest bet.
            if (info.MixFrequency > 0 && info.MixChannels > 0)
                formats.Add((info.MixFrequency, info.MixChannels));

            foreach (int rate in new[] { 48000, 44100, 96000, 192000 })
            {
                formats.Add((rate, 2));

                if (info.MixChannels > 2)
                    formats.Add((rate, info.MixChannels));
            }

            // the device's own minimum is the lowest latency on offer; 0 lets it decide.
            float[] periods = info.MinimumUpdatePeriod > 0
                ? new[] { (float)info.MinimumUpdatePeriod, (float)info.DefaultUpdatePeriod, 0f }
                : new[] { 0f };

            // Two passes: floating point formats first. Everything upstream of here is
            // float, so an integer format means a conversion, and a 16 bit one without
            // dithering is audible as gritty quantisation noise (worst on bass and on
            // reverb tails) which reads as "lower quality" rather than as a glitch.
            foreach (bool floatOnly in new[] { true, false })
            {
                foreach ((int frequency, int channels) in formats)
                {
                    var format = BassWasapi.CheckFormat(wasapiDevice, frequency, channels, WasapiInitFlags.Exclusive);

                    if (format == WasapiFormat.Unknown)
                        continue;

                    if (floatOnly && format != WasapiFormat.Float)
                        continue;

                    var flags = WasapiInitFlags.EventDriven | WasapiInitFlags.Exclusive;

                    // dithering only matters when we're being narrowed to integer samples.
                    if (format != WasapiFormat.Float)
                        flags |= WasapiInitFlags.Dither;

                    foreach (float period in periods)
                    {
                        // buffer stays at the device default on purpose: padding it out
                        // would just be latency, which is the entire point of this mode.
                        if (BassWasapi.Init(wasapiDevice, frequency, channels, Procedure: wasapiProcedure, Flags: flags, Buffer: 0f, Period: period))
                        {
                            Logger.Log($"Initialising BassWasapi for device {wasapiDevice} (exclusive {frequency}hz {channels}ch {format}, period {period}s)...success!");
                            return true;
                        }
                    }
                }
            }

            Logger.Log($"Initialising BassWasapi for device {wasapiDevice} (exclusive)...FAILED, no supported format ({Bass.LastError})", level: LogLevel.Important);
            return false;
        }

        private void freeWasapi()
        {
            if (globalMixerHandle.Value == null) return;

            // The mixer probably doesn't need to be recycled. Just keeping things sane for now.
            Bass.StreamFree(globalMixerHandle.Value.Value);
            BassWasapi.Stop();
            BassWasapi.Free();
            globalMixerHandle.Value = null;
        }

        #endregion
    }
}
