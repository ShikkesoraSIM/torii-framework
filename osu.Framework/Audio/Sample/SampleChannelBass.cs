// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Native;
using osu.Framework.Audio.Track;
using osu.Framework.Extensions.ObjectExtensions;

namespace osu.Framework.Audio.Sample
{
    internal sealed class SampleChannelBass : SampleChannel, IBassAudioChannel
    {
        private readonly SampleBass sample;
        private volatile int channel;
        private bool hasStartedOnce;

        public override bool Playing
        {
            get
            {
                if (Looping && userRequestedPlay)
                    return true;

                return playing || enqueuedPlaybackStart;
            }
        }

        private volatile bool playing;
        private volatile bool userRequestedPlay;
        private volatile bool enqueuedPlaybackStart;

        public override bool Looping
        {
            get => base.Looping;
            set
            {
                base.Looping = value;
                EnqueueAction(pushParamsToNative);
            }
        }

        private bool hasChannel => channel != 0;

        public override ChannelAmplitudes CurrentAmplitudes => (bassAmplitudeProcessor ??= new BassAmplitudeProcessor(this)).CurrentAmplitudes;

        private BassAmplitudeProcessor? bassAmplitudeProcessor;

        public SampleChannelBass(SampleBass sample)
            : base(sample.Name)
        {
            this.sample = sample;
            ensureChannel();
        }

        protected override void UpdateState()
        {
            if (hasChannel)
            {
                var state = (TaChannelState)ToriiAudioNative.ta_sample_channel_get_state(ToriiAudioEngine.Handle, channel);
                playing = state == TaChannelState.Playing || (state == TaChannelState.Paused && userRequestedPlay);
            }
            else
            {
                playing = false;
            }

            base.UpdateState();
            bassAmplitudeProcessor?.Update();
        }

        public override void Play()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            userRequestedPlay = true;
            enqueuedPlaybackStart = true;

            base.Play();

            EnqueueAction(() =>
            {
                if (playInternal())
                    playing = true;

                enqueuedPlaybackStart = false;
            });
        }

        public override void Stop()
        {
            userRequestedPlay = false;

            base.Stop();

            EnqueueAction(() =>
            {
                stopInternal();
                playing = false;
            });
        }

        internal override void OnStateChanged()
        {
            base.OnStateChanged();
            pushParamsToNative();
        }

        private void pushParamsToNative()
        {
            if (!hasChannel)
                return;

            ToriiAudioNative.ta_sample_channel_set_params(
                ToriiAudioEngine.Handle,
                channel,
                (float)AggregateVolume.Value,
                (float)AggregateBalance.Value,
                AggregateFrequency.Value,
                Looping ? 1 : 0);
        }

        private bool playInternal()
        {
            ensureChannel();

            if (!hasChannel)
                return false;

            InvalidateState();

            // Do not restart a sample channel that has already played and finished naturally.
            if (hasStartedOnce && (TaChannelState)ToriiAudioNative.ta_sample_channel_get_state(ToriiAudioEngine.Handle, channel) == TaChannelState.Stopped)
                return false;

            pushParamsToNative();

            if (Mixer is BassAudioMixer bm)
                bm.ChannelPlay(this);

            hasStartedOnce = true;
            return ToriiAudioNative.ta_sample_channel_play(ToriiAudioEngine.Handle, channel) != 0;
        }

        private void stopInternal()
        {
            if (hasChannel)
                ToriiAudioNative.ta_sample_channel_stop(ToriiAudioEngine.Handle, channel);
        }

        private void ensureChannel() => EnqueueAction(() =>
        {
            if (hasChannel || sample.SampleId == 0)
                return;

            int mixerHandle = Mixer is BassAudioMixer bm ? bm.Handle : 0;
            channel = ToriiAudioNative.ta_sample_channel_create(ToriiAudioEngine.Handle, sample.SampleId, mixerHandle);

            if (!hasChannel)
                return;

            pushParamsToNative();

            if (Mixer is BassAudioMixer mixer)
                mixer.AddChannelToBassMix(this);
        });

        #region Mixing

        private BassAudioMixer bassMixer => (BassAudioMixer)Mixer.AsNonNull();

        bool IBassAudioChannel.IsActive => IsAlive;

        int IBassAudioChannel.Handle => channel;

        bool IBassAudioChannel.MixerChannelPaused { get; set; } = true;

        BassAudioMixer IBassAudioChannel.Mixer => bassMixer;

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (hasChannel)
            {
                if (Mixer is BassAudioMixer bm)
                    bm.StreamFree(this);
                ToriiAudioNative.ta_sample_channel_destroy(ToriiAudioEngine.Handle, channel);
                channel = 0;
            }

            playing = false;

            base.Dispose(disposing);
        }
    }
}
