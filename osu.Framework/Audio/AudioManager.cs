// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Native;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Development;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Audio
{
    public class AudioManager : AudioCollectionManager<AudioComponent>
    {
        /// <summary>
        /// The number of internal synthetic devices preceding the first real audio device:
        /// index 0 = NoSound, index 1 = OS Default.
        /// </summary>
        protected const int BASS_INTERNAL_DEVICE_COUNT = 2;

        private const int default_device_index = ToriiAudioEngine.DEFAULT_DEVICE;

        private TaError lastFaultedError = TaError.Ok;
        private int lastFaultedDevice = -1;

        /// <summary>
        /// The manager component responsible for audio tracks (e.g. songs).
        /// </summary>
        public ITrackStore Tracks => globalTrackStore.Value;

        /// <summary>
        /// The manager component responsible for audio samples (e.g. sound effects).
        /// </summary>
        public ISampleStore Samples => globalSampleStore.Value;

        /// <summary>
        /// The thread audio operations are run on.
        /// </summary>
        private readonly AudioThread thread;

        /// <summary>
        /// The global mixer which all tracks are routed into by default.
        /// </summary>
        public readonly AudioMixer TrackMixer;

        /// <summary>
        /// The global mixer which all samples are routed into by default.
        /// </summary>
        public readonly AudioMixer SampleMixer;

        /// <summary>
        /// The names of all available audio devices.
        /// </summary>
        public IEnumerable<string> AudioDeviceNames => audioDeviceNames;

        /// <summary>
        /// Is fired whenever a new audio device is discovered and provides its name.
        /// </summary>
        public event Action<string> OnNewDevice;

        /// <summary>
        /// Is fired whenever an audio device is lost and provides its name.
        /// </summary>
        public event Action<string> OnLostDevice;

        /// <summary>
        /// The preferred audio device we should use. A value of
        /// <see cref="string.Empty"/> denotes the OS default.
        /// </summary>
        public readonly Bindable<string> AudioDevice = new Bindable<string>();

        /// <summary>
        /// Whether to use low-latency WASAPI event-driven initialisation on Windows.
        /// </summary>
        public readonly BindableBool UseExperimentalWasapi = new BindableBool();

        /// <summary>
        /// Whether WASAPI should take exclusive control of the output device.
        /// Only has an effect when <see cref="UseExperimentalWasapi"/> is also active.
        /// </summary>
        public readonly BindableBool UseExclusiveWasapi = new BindableBool();

        /// <summary>
        /// Periodo de update a pedirle al dispositivo en modo exclusivo, en ms.
        /// 0 = automatico (el minimo que declare el dispositivo).
        /// </summary>
        public readonly BindableDouble ExclusiveUpdatePeriodMs = new BindableDouble();

        /// <summary>
        /// Output latency of the current device in milliseconds, as reported by the audio
        /// backend. Zero when it can't be determined.
        /// </summary>
        public IBindable<double> OutputLatency => outputLatency;

        private readonly Bindable<double> outputLatency = new Bindable<double>();

        /// <summary>
        /// Volume of all samples played game-wide.
        /// </summary>
        public readonly BindableDouble VolumeSample = new BindableDouble(1)
        {
            MinValue = 0,
            MaxValue = 1
        };

        /// <summary>
        /// Volume of all tracks played game-wide.
        /// </summary>
        public readonly BindableDouble VolumeTrack = new BindableDouble(1)
        {
            MinValue = 0,
            MaxValue = 1
        };

        /// <summary>
        /// Whether a global mixer is being used for audio routing.
        /// </summary>
        public IBindable<bool> UsingGlobalMixer => usingGlobalMixer;

        private readonly Bindable<bool> usingGlobalMixer = new BindableBool();

        /// <summary>
        /// Global root mixer handle when active.
        /// </summary>
        internal readonly IBindable<int?> GlobalMixerHandle = new Bindable<int?>();

        public override bool IsLoaded => base.IsLoaded && ToriiAudioEngine.CurrentDevice != ToriiAudioEngine.UNINITIALISED_DEVICE;

        // Mutated by multiple threads, must be thread safe.
        private ImmutableArray<AudioDeviceInfo> audioDevices = ImmutableArray<AudioDeviceInfo>.Empty;
        private ImmutableList<string> audioDeviceNames = ImmutableList<string>.Empty;

        private Scheduler scheduler => thread.Scheduler;

        private Scheduler eventScheduler => EventScheduler ?? scheduler;

        private readonly CancellationTokenSource cancelSource = new CancellationTokenSource();

        /// <summary>
        /// The scheduler used for invoking publicly exposed delegate events.
        /// </summary>
        public Scheduler EventScheduler;

        internal IBindableList<AudioMixer> ActiveMixers => activeMixers;
        private readonly BindableList<AudioMixer> activeMixers = new BindableList<AudioMixer>();

        private readonly Lazy<TrackStore> globalTrackStore;
        private readonly Lazy<SampleStore> globalSampleStore;
        private readonly ResourceStore<byte[]> sampleByteStore;

        public AudioManager(AudioThread audioThread, ResourceStore<byte[]> trackStore, ResourceStore<byte[]> sampleStore, [CanBeNull] FrameworkConfigManager config)
        {
            thread = audioThread;
            sampleByteStore = sampleStore;

            thread.RegisterManager(this);
            outputLatency.BindTo(thread.OutputLatency);

            if (config != null)
            {
                config.BindWith(FrameworkSetting.AudioDevice, AudioDevice);
                config.BindWith(FrameworkSetting.AudioUseExperimentalWasapi, UseExperimentalWasapi);
                config.BindWith(FrameworkSetting.AudioWasapiExclusive, UseExclusiveWasapi);
                config.BindWith(FrameworkSetting.VolumeUniversal, Volume);
                config.BindWith(FrameworkSetting.VolumeEffect, VolumeSample);
                config.BindWith(FrameworkSetting.VolumeMusic, VolumeTrack);
            }

            AudioDevice.ValueChanged += _ => scheduler.AddOnce(initCurrentDevice);
            UseExperimentalWasapi.ValueChanged += _ => scheduler.AddOnce(initCurrentDevice);
            UseExclusiveWasapi.ValueChanged += _ => scheduler.AddOnce(initCurrentDevice);
            ExclusiveUpdatePeriodMs.ValueChanged += _ => scheduler.AddOnce(initCurrentDevice);
            GlobalMixerHandle.ValueChanged += handle => usingGlobalMixer.Value = handle.NewValue.HasValue;

            AddItem(TrackMixer = createAudioMixer(null, nameof(TrackMixer)));
            AddItem(SampleMixer = createAudioMixer(null, nameof(SampleMixer)));

            globalTrackStore = new Lazy<TrackStore>(() =>
            {
                var store = new TrackStore(trackStore, TrackMixer);
                AddItem(store);
                store.AddAdjustment(AdjustableProperty.Volume, VolumeTrack);
                return store;
            });

            globalSampleStore = new Lazy<SampleStore>(() =>
            {
                var store = new SampleStore(sampleStore, SampleMixer);
                AddItem(store);
                store.AddAdjustment(AdjustableProperty.Volume, VolumeSample);
                return store;
            });

            syncAudioDevices();

            // check for changes in any audio devices every 1000ms
            CancellationToken token = cancelSource.Token;
            scheduler.AddDelayed(() =>
            {
                new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (CheckForDeviceChanges(audioDevices) || ToriiAudioNative.ta_device_is_lost(ToriiAudioEngine.Handle) != 0)
                                syncAudioDevices();
                            Thread.Sleep(1000);
                        }
                        catch
                        {
                        }
                    }
                })
                {
                    IsBackground = true
                }.Start();
            }, 1000);
        }

        protected override void Dispose(bool disposing)
        {
            cancelSource.Cancel();

            thread.UnregisterManager(this);

            OnNewDevice = null;
            OnLostDevice = null;

            base.Dispose(disposing);
        }

        private static int userMixerID;

        public AudioMixer CreateAudioMixer(string identifier = null) =>
            createAudioMixer(SampleMixer, !string.IsNullOrEmpty(identifier) ? identifier : $"user #{Interlocked.Increment(ref userMixerID)}");

        private AudioMixer createAudioMixer(AudioMixer fallbackMixer, string identifier)
        {
            var mixer = new BassAudioMixer(this, fallbackMixer, identifier);
            AddItem(mixer);
            return mixer;
        }

        protected override void ItemAdded(AudioComponent item)
        {
            base.ItemAdded(item);
            if (item is AudioMixer mixer)
                activeMixers.Add(mixer);
        }

        protected override void ItemRemoved(AudioComponent item)
        {
            base.ItemRemoved(item);
            if (item is AudioMixer mixer)
                activeMixers.Remove(mixer);
        }

        public ITrackStore GetTrackStore(IResourceStore<byte[]> store = null, AudioMixer mixer = null)
        {
            if (store == null) return globalTrackStore.Value;

            TrackStore tm = new TrackStore(store, mixer ?? TrackMixer);
            globalTrackStore.Value.AddItem(tm);
            return tm;
        }

        public ISampleStore GetSampleStore(IResourceStore<byte[]> store = null, AudioMixer mixer = null, bool applyGlobalAdjustments = true)
        {
            if (store == null && mixer == null && applyGlobalAdjustments)
                return globalSampleStore.Value;

            SampleStore sm = new SampleStore(store ?? sampleByteStore, mixer ?? SampleMixer);
            if (applyGlobalAdjustments)
                globalSampleStore.Value.AddItem(sm);
            else
                AddItem(sm);
            return sm;
        }

        private void initCurrentDevice()
        {
            string deviceName = AudioDevice.Value;

            // try using the specified device
            int deviceIndex = audioDeviceNames.FindIndex(d => d == deviceName);
            if (deviceIndex >= 0 && trySetDevice(BASS_INTERNAL_DEVICE_COUNT + deviceIndex)) return;

            // try using the system default if there is any device present (or on mobile).
            if ((audioDeviceNames.Count > 0 || RuntimeInfo.IsMobile) && trySetDevice(default_device_index)) return;

            // no audio devices can be used, so try using "No sound" software device as last resort.
            trySetDevice(ToriiAudioEngine.NO_SOUND_DEVICE);
            return;

            bool trySetDevice(int deviceId)
            {
                var device = audioDevices.ElementAtOrDefault(deviceId);

                if (!device.IsEnabled)
                    return false;

                // we don't want real audio hardware initialised on headless test runs.
                if (deviceId != ToriiAudioEngine.NO_SOUND_DEVICE && DebugUtils.IsNUnitRunning)
                    return false;

                if (!InitBass(deviceId))
                    return false;

                UpdateDevice(deviceId);
                return true;
            }
        }

        /// <summary>
        /// Initialises the native <c>torii_audio</c> output device.
        /// Virtual so unit tests (e.g. <c>AudioManagerWithDeviceLoss</c>) can override device init.
        /// </summary>
        protected virtual bool InitBass(int device)
        {
            bool success = attemptInit();

            if (success || !UseExperimentalWasapi.Value)
                return success;

            // Busy means another app holds exclusive mode right now — keep user's config intact!
            bool deviceBusy = ToriiAudioEngine.LastError == TaError.Busy;

            if (UseExclusiveWasapi.Value && !deviceBusy)
            {
                Logger.Log($"Audio device {device} failed to initialise with exclusive WASAPI, falling back to low-latency shared", level: LogLevel.Important);
                UseExclusiveWasapi.Value = false;

                if (attemptInit())
                    return true;
            }

            if (deviceBusy)
                return false;

            Logger.Log($"Audio device {device} failed to initialise with low-latency WASAPI, falling back to legacy shared", level: LogLevel.Error);
            UseExperimentalWasapi.Value = false;
            return attemptInit();

            bool attemptInit()
            {
                thread.ExclusiveUpdatePeriodMs = ExclusiveUpdatePeriodMs.Value;
                bool innerSuccess = thread.InitDevice(device, UseExperimentalWasapi.Value, UseExclusiveWasapi.Value);

                if (!innerSuccess)
                {
                    if (ToriiAudioEngine.LastError != TaError.Ok && ToriiAudioEngine.LastError == lastFaultedError && device == lastFaultedDevice)
                        return false;

                    if (BassUtils.CheckFaulted(false))
                    {
                        lastFaultedError = ToriiAudioEngine.LastError;
                        lastFaultedDevice = device;
                        return false;
                    }

                    Logger.Log("torii_audio failed to initialize without an error code", level: LogLevel.Error);
                    return false;
                }

                lastFaultedError = TaError.Ok;
                lastFaultedDevice = -1;

                var deviceInfo = audioDevices.ElementAtOrDefault(device);
                var caps = ToriiAudioEngine.ActiveCaps;

                Logger.Log($@"🔈 torii_audio initialised (miniaudio + Signalsmith Stretch)
                          Backend:       {caps.GetBackendName()}
                          Device:        {deviceInfo.Name}
                          Mode:          {(TaDeviceMode)caps.ActualMode}
                          Format:        {caps.SampleRate}Hz {caps.Channels}ch {(caps.IsFloatOutput != 0 ? "Float32" : $"Int{caps.BitDepth}")}
                          Period:        {caps.ActivePeriodMs:F2} ms
                          Est. Latency:  {caps.OutputLatencyMs:F2} ms");

                return true;
            }
        }

        private void syncAudioDevices()
        {
            audioDevices = GetAllDevices();

            Trace.Assert(audioDevices.Length >= BASS_INTERNAL_DEVICE_COUNT, "Audio backend did not provide synthetic NoSound and Default devices.");

            var oldDeviceNames = audioDeviceNames;
            var newDeviceNames = audioDeviceNames = audioDevices.Skip(BASS_INTERNAL_DEVICE_COUNT).Where(d => d.IsEnabled).Select(d => d.Name).ToImmutableList();

            scheduler.Add(() =>
            {
                if (cancelSource.IsCancellationRequested)
                    return;

                if (!IsCurrentDeviceValid())
                    initCurrentDevice();
            }, false);

            var newDevices = newDeviceNames.Except(oldDeviceNames).ToList();
            var lostDevices = oldDeviceNames.Except(newDeviceNames).ToList();

            if (newDevices.Count > 0 || lostDevices.Count > 0)
            {
                eventScheduler.Add(delegate
                {
                    foreach (string d in newDevices)
                        OnNewDevice?.Invoke(d);
                    foreach (string d in lostDevices)
                        OnLostDevice?.Invoke(d);
                });
            }
        }

        protected virtual bool CheckForDeviceChanges(ImmutableArray<AudioDeviceInfo> previousDevices)
        {
            var current = GetAllDevices();

            if (previousDevices.Length != current.Length)
                return true;

            for (int i = 0; i < current.Length; i++)
            {
                if (current[i].IsEnabled != previousDevices[i].IsEnabled)
                    return true;

                if (current[i].IsDefault != previousDevices[i].IsDefault)
                    return true;

                if (current[i].Name != previousDevices[i].Name)
                    return true;
            }

            return false;
        }

        protected virtual unsafe ImmutableArray<AudioDeviceInfo> GetAllDevices()
        {
            const int max_devices = 128;
            TaDeviceInfo* raw = stackalloc TaDeviceInfo[max_devices];
            int count = Math.Min(max_devices, ToriiAudioNative.ta_enumerate_devices(ToriiAudioEngine.Handle, raw, max_devices));

            var devices = ImmutableArray.CreateBuilder<AudioDeviceInfo>(BASS_INTERNAL_DEVICE_COUNT + count);

            // [0] No sound
            devices.Add(new AudioDeviceInfo
            {
                Name = "No sound",
                Driver = "NoSound",
                IsDefault = false,
                IsEnabled = true,
                IsInitialized = ToriiAudioEngine.CurrentDevice == ToriiAudioEngine.NO_SOUND_DEVICE,
            });

            // [1] Default
            devices.Add(new AudioDeviceInfo
            {
                Name = "Default",
                Driver = "Default",
                IsDefault = true,
                IsEnabled = true,
                IsInitialized = ToriiAudioEngine.CurrentDevice == ToriiAudioEngine.DEFAULT_DEVICE,
            });

            // [2..N+1] Physical playback devices
            for (int i = 0; i < count; i++)
            {
                int devIdx = BASS_INTERNAL_DEVICE_COUNT + i;
                devices.Add(new AudioDeviceInfo
                {
                    Name = raw[i].GetName(),
                    Driver = "miniaudio",
                    IsDefault = raw[i].IsDefault != 0,
                    IsEnabled = raw[i].IsEnabled != 0,
                    IsInitialized = ToriiAudioEngine.CurrentDevice == devIdx,
                });
            }

            return devices.MoveToImmutable();
        }

        protected virtual bool IsCurrentDeviceValid()
        {
            if (ToriiAudioNative.ta_device_is_lost(ToriiAudioEngine.Handle) != 0)
                return false;

            int current = ToriiAudioEngine.CurrentDevice;
            if (current < 0 || current >= audioDevices.Length)
                return false;

            var device = audioDevices[current];
            bool isFallback = string.IsNullOrEmpty(AudioDevice.Value) ? !device.IsDefault : device.Name != AudioDevice.Value;
            return device.IsEnabled && device.IsInitialized && !isFallback;
        }

        public override string ToString()
        {
            int cur = ToriiAudioEngine.CurrentDevice;
            string deviceName = cur >= 0 && cur < audioDevices.Length ? audioDevices[cur].Name : null;
            return $@"{GetType().ReadableName()} ({deviceName ?? "Unknown"})";
        }
    }
}
