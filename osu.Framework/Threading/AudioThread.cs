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

        /// <summary>Whether the live wasapi session, if any, holds the device exclusively.</summary>
        private bool wasapiExclusiveActive;

        internal bool InitDevice(int deviceId, bool useExperimentalWasapi, bool exclusiveWasapi = false)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1); // The real device ID should always be used, as the -1 device has special cases which are hard to work with.

            // An exclusive-mode device belongs to us and nobody else, and "nobody else"
            // includes BASS's own re-initialisation below: leaving it held makes that fail.
            // Only exclusive sessions get released up front though. Releasing a SHARED one
            // here leaves the endpoint mid-teardown and the very next Bass.Init comes back
            // Busy, which cascades into "experimental WASAPI failed, disabling" and lands
            // the user on the No sound device with their setting silently turned off.
            if (wasapiExclusiveActive)
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

            if (!useExperimentalWasapi && RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
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

            // Nothing above is allowed to leak into what the caller sees: AudioManager
            // checks Bass.LastError right after this returns and surfaces anything
            // non-OK to the user as a "BASS faulted" notification. The wasapi device
            // enumeration in particular always ends on an end-of-list error, and the
            // latency probing can fail harmlessly too. A guaranteed-success call resets
            // the code (device 0, "No sound", always exists).
            Bass.GetDeviceInfo(0, out _);

            return true;
        }

        private double lastLatencyUpdate;
        private double smoothedLatency;
        private double legacySessionEstimateMs;

        /// <summary>
        /// Si se puede reparar la latencia ahora mismo. El juego la baja mientras se
        /// esta jugando: vaciar la cola cuesta un saltito de audio, y en el medio de un
        /// mapa eso descoloca peor que la latencia que estariamos arreglando.
        /// </summary>
        public readonly BindableBool AllowLatencyRepair = new BindableBool(true);

        private double latencyFloor;
        private double lastQueueFlush;
        private int creepSamples;
        private int failedFlushes;

        // cuanto se tiene que pasar de su propio piso para contar como creep, y cuantas
        // lecturas seguidas hacen falta para no salir corriendo por un pico aislado.
        private const double creep_threshold_ms = 8;
        private const int creep_samples_needed = 10;
        private const double flush_cooldown_ms = 15000;
        private const int max_failed_flushes = 3;

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

            // el medidor entero es cosa de windows: en otras plataformas cada consulta
            // que no corresponde deja un codigo de error colgado que despues aparece
            // como "BASS faulted" sin que haya fallado nada. fuera de windows este
            // codigo no existe y el framework queda igual que antes de agregarlo.
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return;

            // sin dispositivo inicializado no hay nada que medir, mismo motivo.
            if (initialised_devices.Count == 0)
                return;

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
                    latency = Math.Max(0, bassInfo.Latency);

                    // los dos valores de config son cosa de windows; en otras
                    // plataformas la consulta misma es un parametro invalido para bass.
                    if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                        latency += Math.Max(0, Bass.DeviceBufferLength) + Math.Max(0, Bass.UpdatePeriod) + legacySessionEstimateMs;
                }
            }
            catch
            {
                latency = 0;
            }

            // mismo criterio que en InitDevice: nada de esto puede dejar un codigo de
            // error colgado para que otro lo lea.
            Bass.GetDeviceInfo(0, out _);

            // the wasapi reading swings by a period or two between samples; smooth it so
            // the number on screen is readable instead of flickering.
            smoothedLatency = smoothedLatency > 0 && latency > 0
                ? smoothedLatency * 0.7 + latency * 0.3
                : latency;

            OutputLatency.Value = smoothedLatency;

            if (wasapiExclusiveActive)
                keepExclusiveQueueTight();
        }

        /// <summary>
        /// Deshace el creep de latencia del modo exclusivo.
        ///
        /// Cada tironcito (un alt tab, un frame que tardo de mas) deja un poco mas de
        /// audio encolado, y nadie lo saca nunca, asi que el retraso sube por escalones
        /// y se queda arriba hasta que se reinicializa el dispositivo. Como la cola ES
        /// el retraso, tirar lo encolado lo devuelve al valor de recien arrancado.
        /// </summary>
        /// <summary>
        /// Torii: vacia la cola del exclusivo AHORA si esta por encima de su piso.
        /// Lo llama el juego en la pantalla de carga, justo antes del gameplay:
        /// adentro la reparacion automatica esta apagada a proposito, asi que este
        /// es el ultimo momento util para no arrastrar una cola inflada (y su
        /// latencia) durante el mapa entero. El saltito del flush cae en la carga,
        /// donde no molesta.
        /// </summary>
        /// <param name="onFlushed">se invoca EN EL HILO DE AUDIO solo si de verdad se vacio.</param>
        public void FlushExclusiveQueueNow(Action? onFlushed = null)
        {
            Scheduler.Add(() =>
            {
                if (!wasapiExclusiveActive)
                    return;

                if (latencyFloor <= 0 || smoothedLatency <= 0)
                    return;

                // margen chico a proposito (no el de la reparacion en caliente): aca no
                // hay play en curso que interrumpir, cualquier mejora real vale.
                if (smoothedLatency < latencyFloor + pre_gameplay_flush_margin_ms)
                    return;

                lastQueueFlush = Clock.CurrentTime;
                creepSamples = 0;

                if (!BassWasapi.Stop(true))
                    return;

                BassWasapi.Start();
                smoothedLatency = 0;

                onFlushed?.Invoke();
            });
        }

        private const double pre_gameplay_flush_margin_ms = 2;

        private void keepExclusiveQueueTight()
        {
            if (smoothedLatency <= 0)
                return;

            // el piso es lo mejor que dio este dispositivo desde que arranco, que es
            // justo el objetivo: volver a lo que marcaba recien inicializado.
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

            // un pico suelto no es creep; recien importa si se quedo arriba.
            if (++creepSamples < creep_samples_needed)
                return;

            // jugando no se toca. lo dejamos confirmado para que se repare apenas se
            // vuelva al menu, o sea antes del proximo intento y no en el medio de este.
            if (!AllowLatencyRepair.Value)
            {
                creepSamples = creep_samples_needed;
                return;
            }

            // si vaciar no lo baja, el piso viejo ya no existe (otra carga, otro formato):
            // aceptamos el nuevo en vez de quedarnos chasqueando para siempre.
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

            // Stop(true) vacia lo encolado. el dispositivo no se cierra ni se reinicializa,
            // solo vuelve a arrancar sin el retraso acumulado encima.
            if (!BassWasapi.Stop(true))
                return;

            BassWasapi.Start();

            // que la proxima lectura no arrastre el promedio de antes de vaciar.
            smoothedLatency = 0;
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
                        // el buffer va en el default del dispositivo. pedirle uno mas grande
                        // es latencia pura, pero pedirle uno mas CHICO tambien sube el piso:
                        // probamos con dos periodos y el driver termino alojando mas que su
                        // propio default, de 7ms a 12ms. el que sabe cual es el minimo real
                        // es el, no nosotros.
                        if (BassWasapi.Init(wasapiDevice, frequency, channels, Procedure: wasapiProcedure, Flags: flags, Buffer: 0f, Period: period))
                        {
                            wasapiExclusiveActive = true;
                            Logger.Log($"Initialising BassWasapi for device {wasapiDevice} (exclusive {frequency}hz {channels}ch {format}, period {period}s)...success!");
                            return true;
                        }
                    }
                }
            }

            // el motivo real importa: Busy es "otra app tiene el device" y NotAvailable
            // suele ser el checkbox de exclusivo apagado en Windows, no un formato malo.
            string reason = Bass.LastError switch
            {
                Errors.Busy => @"the device is in use by another application (Busy)",
                Errors.NotAvailable => @"Windows is blocking exclusive access (NotAvailable)",
                _ => $@"no supported format ({Bass.LastError})",
            };

            Logger.Log($"Initialising BassWasapi for device {wasapiDevice} (exclusive)...FAILED, {reason}", level: LogLevel.Important);
            return false;
        }

        private void freeWasapi()
        {
            wasapiExclusiveActive = false;
            latencyFloor = 0;
            smoothedLatency = 0;
            creepSamples = 0;
            failedFlushes = 0;

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
