// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Native;
using osu.Framework.Audio.Track;

namespace osu.Framework.Audio
{
    /// <summary>
    /// Computes and caches stereo levels &amp; 256-bin FFT amplitudes for a native audio channel.
    /// Zero per-frame managed allocations.
    /// </summary>
    internal class BassAmplitudeProcessor
    {
        public ChannelAmplitudes CurrentAmplitudes { get; private set; } = ChannelAmplitudes.Empty;

        private readonly IBassAudioChannel channel;
        private float[]? frequencyData;

        public BassAmplitudeProcessor(IBassAudioChannel channel)
        {
            this.channel = channel;
        }

        public unsafe void Update()
        {
            int handle = channel.Handle;
            if (handle == 0)
            {
                CurrentAmplitudes = ChannelAmplitudes.Empty;
                return;
            }

            frequencyData ??= new float[ChannelAmplitudes.AMPLITUDES_SIZE];

            fixed (float* fftPtr = frequencyData)
            {
                int active = ToriiAudioNative.ta_channel_get_amplitudes(
                    ToriiAudioEngine.Handle,
                    handle,
                    out float leftChannel,
                    out float rightChannel,
                    fftPtr);

                if (active != 0 && leftChannel >= 0 && rightChannel >= 0)
                    CurrentAmplitudes = new ChannelAmplitudes(leftChannel, rightChannel, frequencyData);
                else
                    CurrentAmplitudes = ChannelAmplitudes.Empty;
            }
        }
    }
}
