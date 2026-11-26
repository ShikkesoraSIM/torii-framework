// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Native;
using osu.Framework.Bindables;
using osu.Framework.Platform;

namespace osu.Framework.Audio.Sample
{
    /// <summary>
    /// A factory for <see cref="SampleBass"/> objects sharing a pre-decoded native PCM sample asset
    /// (and thus playback concurrency).
    /// </summary>
    internal class SampleBassFactory : AudioCollectionManager<AdjustableAudioComponent>
    {
        public string Name { get; }

        public int SampleId { get; private set; }

        public override bool IsLoaded => SampleId != 0;

        public double Length { get; private set; }

        internal readonly Bindable<int> PlaybackConcurrency = new Bindable<int>(Sample.DEFAULT_CONCURRENCY);

        private readonly BassAudioMixer mixer;

        private NativeMemoryTracker.NativeMemoryLease? memoryLease;
        private byte[]? data;

        public SampleBassFactory(byte[] data, string name, BassAudioMixer mixer)
        {
            this.data = data;
            this.mixer = mixer;

            Name = name;

            EnqueueAction(loadSample);

            PlaybackConcurrency.BindValueChanged(updatePlaybackConcurrency);
        }

        private void updatePlaybackConcurrency(ValueChangedEvent<int> concurrency)
        {
            EnqueueAction(() =>
            {
                if (!IsLoaded)
                    return;

                ToriiAudioNative.ta_sample_set_concurrency(ToriiAudioEngine.Handle, SampleId, concurrency.NewValue);
            });
        }

        internal override void UpdateDevice(int deviceIndex)
        {
            if (!IsLoaded)
                loadSample();
        }

        private unsafe void loadSample()
        {
            Debug.Assert(CanPerformInline);
            Debug.Assert(!IsLoaded);

            if (data == null)
                return;

            byte[] raw = data;
            data = null;

            if (raw.Length < 4)
                return;

            fixed (byte* ptr = raw)
            {
                SampleId = ToriiAudioNative.ta_sample_create(ToriiAudioEngine.Handle, ptr, (nuint)raw.Length, PlaybackConcurrency.Value, out TaSampleInfo info);

                if (SampleId != 0)
                {
                    Length = info.LengthMs;
                    if (info.PcmBytes > 0)
                        memoryLease = NativeMemoryTracker.AddMemory(this, info.PcmBytes);
                }
            }
        }

        public Sample CreateSample() => new SampleBass(this, mixer) { OnPlay = onPlay };

        private void onPlay(Sample sample)
        {
            AddItem(sample);
        }

        ~SampleBassFactory()
        {
            Dispose(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (IsLoaded)
            {
                ToriiAudioNative.ta_sample_destroy(ToriiAudioEngine.Handle, SampleId);
                SampleId = 0;
                memoryLease?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
