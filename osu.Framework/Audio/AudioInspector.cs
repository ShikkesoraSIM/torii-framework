// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using osu.Framework.Audio.Native;

namespace osu.Framework.Audio
{
    /// <summary>
    /// Detected audio stream format (content-sniffed, independent of filename extension).
    /// </summary>
    public enum AudioFormat
    {
        Unknown = 0,
        Mp3 = 1,
        Ogg = 2,
        Wave = 3,
        Flac = 4,
    }

    public readonly struct AudioStreamInfo
    {
        public AudioFormat Format { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public int BitrateKbps { get; init; }
        public double DurationMs { get; init; }
    }

    /// <summary>
    /// Stateless, deviceless audio stream inspector backed by <c>torii_audio</c>.
    /// Works in headless/CI environments without an initialised audio output device.
    /// </summary>
    public static class AudioInspector
    {
        public static AudioFormat GetFormat(Stream? data)
        {
            if (data == null)
                return AudioFormat.Unknown;

            return TryInspect(data, out AudioStreamInfo info, prescanDuration: false)
                ? info.Format
                : AudioFormat.Unknown;
        }

        public static unsafe bool TryInspect(Stream? data, out AudioStreamInfo info, bool prescanDuration = false)
        {
            info = default;

            if (data == null)
                return false;

            if (data.CanSeek && data.Length <= 0)
                return false;

            byte[] bytes = ToriiAudioEngine.ReadStreamBytes(data);
            if (bytes.Length < 4)
                return false;

            fixed (byte* ptr = bytes)
            {
                if (ToriiAudioNative.ta_probe_memory(ptr, (nuint)bytes.Length, prescanDuration ? 1 : 0, out TaAudioInfo nativeInfo) == 0)
                    return false;

                info = new AudioStreamInfo
                {
                    Format = (AudioFormat)nativeInfo.Format,
                    SampleRate = nativeInfo.SampleRate,
                    Channels = nativeInfo.Channels,
                    BitrateKbps = nativeInfo.BitrateKbps,
                    DurationMs = nativeInfo.DurationMs,
                };
                return true;
            }
        }
    }
}
