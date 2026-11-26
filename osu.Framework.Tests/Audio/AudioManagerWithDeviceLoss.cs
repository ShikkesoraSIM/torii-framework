// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Immutable;
using System.Linq;
using osu.Framework.Audio;
using osu.Framework.Audio.Native;
using osu.Framework.IO.Stores;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Audio
{
    /// <summary>
    /// <see cref="AudioManager"/> that can simulate the loss of a device.
    /// </summary>
    internal class AudioManagerWithDeviceLoss : AudioManager
    {
        public AudioManagerWithDeviceLoss(AudioThread audioThread, ResourceStore<byte[]> trackStore, ResourceStore<byte[]> sampleStore)
            : base(audioThread, trackStore, sampleStore, null)
        {
        }

        public volatile int CurrentDevice = ToriiAudioEngine.UNINITIALISED_DEVICE;

        private volatile bool simulateLoss;

        protected override bool InitBass(int device)
        {
            try
            {
                if (simulateLoss)
                    return device == ToriiAudioEngine.NO_SOUND_DEVICE && base.InitBass(device);

                return base.InitBass(device);
            }
            finally
            {
                CurrentDevice = ToriiAudioEngine.CurrentDevice;
            }
        }

        protected override bool CheckForDeviceChanges(ImmutableArray<AudioDeviceInfo> previousDevices)
        {
            if (simulateLoss)
                return true;

            return base.CheckForDeviceChanges(previousDevices);
        }

        protected override ImmutableArray<AudioDeviceInfo> GetAllDevices()
        {
            var devices = base.GetAllDevices();

            if (simulateLoss)
                devices = devices.Take(BASS_INTERNAL_DEVICE_COUNT).ToImmutableArray();

            return devices;
        }

        protected override bool IsCurrentDeviceValid()
        {
            if (simulateLoss)
                return CurrentDevice == ToriiAudioEngine.NO_SOUND_DEVICE && base.IsCurrentDeviceValid();

            return CurrentDevice != ToriiAudioEngine.NO_SOUND_DEVICE && base.IsCurrentDeviceValid();
        }

        public void SimulateDeviceLoss()
        {
            int current = CurrentDevice;

            simulateLoss = true;

            if (current != ToriiAudioEngine.NO_SOUND_DEVICE)
                WaitForDeviceChange(current);
        }

        public void SimulateDeviceRestore()
        {
            int current = CurrentDevice;

            simulateLoss = false;

            if (current == ToriiAudioEngine.NO_SOUND_DEVICE)
                WaitForDeviceChange(current);
        }

        public void WaitForDeviceChange(int? current = null, int timeoutMs = 5000)
        {
            current ??= CurrentDevice;

            AudioThreadTest.WaitForOrAssert(() => CurrentDevice != current, $"Timed out while waiting for the device to change from {current}.", timeoutMs);
        }
    }
}
