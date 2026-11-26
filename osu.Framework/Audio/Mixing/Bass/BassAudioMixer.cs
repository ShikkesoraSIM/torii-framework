// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using osu.Framework.Audio.Native;
using osu.Framework.Statistics;

namespace osu.Framework.Audio.Mixing.Bass
{
    /// <summary>
    /// Mixes together multiple <see cref="IAudioChannel"/>s into one sub-mixer bus via <c>torii_audio</c>.
    /// Survives output device hot-swaps with all active channels AND DSP effects intact!
    /// </summary>
    internal class BassAudioMixer : AudioMixer, IBassAudio
    {
        private readonly AudioManager? manager;

        /// <summary>
        /// The native <c>torii_audio</c> mixer bus handle.
        /// </summary>
        public int Handle { get; private set; }

        private readonly List<IBassAudioChannel> activeChannels = new List<IBassAudioChannel>();

        private readonly Dictionary<IEffectParameter, (int fxHandle, int priority)> activeEffects = new Dictionary<IEffectParameter, (int, int)>();

        public BassAudioMixer(AudioManager? manager, AudioMixer? fallbackMixer, string identifier)
            : base(fallbackMixer, identifier)
        {
            this.manager = manager;
            EnqueueAction(ensureMixerCreated);
        }

        public override void AddEffect(IEffectParameter effect, int priority = 0) => EnqueueAction(() =>
        {
            ensureMixerCreated();

            if (Handle == 0 || activeEffects.ContainsKey(effect))
                return;

            TaFxParams nativeParams = effect.ToNativeParams();
            int fxHandle = ToriiAudioNative.ta_mixer_fx_add(ToriiAudioEngine.Handle, Handle, ref nativeParams, priority);

            if (fxHandle != 0)
                activeEffects[effect] = (fxHandle, priority);
        });

        public override void RemoveEffect(IEffectParameter effect) => EnqueueAction(() =>
        {
            if (!activeEffects.Remove(effect, out var entry))
                return;

            if (Handle != 0 && entry.fxHandle != 0)
                ToriiAudioNative.ta_mixer_fx_remove(ToriiAudioEngine.Handle, Handle, entry.fxHandle);
        });

        public override void UpdateEffect(IEffectParameter effect) => EnqueueAction(() =>
        {
            if (Handle == 0 || !activeEffects.TryGetValue(effect, out var entry))
                return;

            TaFxParams nativeParams = effect.ToNativeParams();
            ToriiAudioNative.ta_mixer_fx_update(ToriiAudioEngine.Handle, Handle, entry.fxHandle, ref nativeParams);
        });

        protected override void AddInternal(IAudioChannel channel)
        {
            Debug.Assert(CanPerformInline);

            if (!(channel is IBassAudioChannel audioChannel))
                return;

            ensureMixerCreated();

            if (Handle == 0 || audioChannel.Handle == 0)
                return;

            AddChannelToBassMix(audioChannel);
        }

        protected override void RemoveInternal(IAudioChannel channel)
        {
            Debug.Assert(CanPerformInline);

            if (!(channel is IBassAudioChannel audioChannel))
                return;

            if (Handle == 0 || audioChannel.Handle == 0)
                return;

            if (activeChannels.Remove(audioChannel))
                removeChannelFromBassMix(audioChannel);
        }

        public bool ChannelPlay(IBassAudioChannel channel, bool restart = false)
        {
            ensureMixerCreated();

            if (Handle == 0 || channel.Handle == 0)
                return false;

            if (!activeChannels.Contains(channel))
                AddChannelToBassMix(channel);

            channel.MixerChannelPaused = false;
            return true;
        }

        public bool ChannelPause(IBassAudioChannel channel, bool flushMixer = false)
        {
            channel.MixerChannelPaused = true;

            if (flushMixer && Handle != 0)
                ToriiAudioNative.ta_mixer_flush(ToriiAudioEngine.Handle, Handle);

            return true;
        }

        public TaChannelState ChannelIsActive(IBassAudioChannel channel)
        {
            if (channel.Handle == 0)
                return TaChannelState.Stopped;

            return (TaChannelState)ToriiAudioNative.ta_channel_get_state(ToriiAudioEngine.Handle, channel.Handle);
        }

        public bool StreamFree(IBassAudioChannel channel)
        {
            Remove(channel, false);
            return true;
        }

        public void UpdateDevice(int deviceIndex)
        {
            // Our native torii_audio mixer bus lives inside ta_engine independent of ma_device,
            // so active channels AND DSP effects survive device switches seamlessly without recreation!
            ensureMixerCreated();
        }

        protected override void UpdateState()
        {
            for (int i = 0; i < activeChannels.Count; i++)
            {
                var channel = activeChannels[i];
                if (channel.IsActive)
                    continue;

                activeChannels.RemoveAt(i--);
                removeChannelFromBassMix(channel);
            }

            FrameStatistics.Add(StatisticsCounterType.MixChannels, activeChannels.Count);
            base.UpdateState();
        }

        private void ensureMixerCreated()
        {
            if (Handle != 0 || IsDisposed)
                return;

            Handle = ToriiAudioNative.ta_mixer_create(ToriiAudioEngine.Handle, 0);
        }

        public void AddChannelToBassMix(IBassAudioChannel channel)
        {
            ensureMixerCreated();

            if (Handle == 0 || channel.Handle == 0)
                return;

            if (ToriiAudioNative.ta_mixer_add_channel(ToriiAudioEngine.Handle, Handle, channel.Handle, channel.MixerChannelPaused ? 1 : 0) != 0)
            {
                if (!activeChannels.Contains(channel))
                    activeChannels.Add(channel);
            }
        }

        private void removeChannelFromBassMix(IBassAudioChannel channel)
        {
            if (Handle != 0 && channel.Handle != 0)
                ToriiAudioNative.ta_mixer_remove_channel(ToriiAudioEngine.Handle, Handle, channel.Handle);
        }

        internal unsafe int[] GetChannelHandles()
        {
            if (Handle == 0)
                return Array.Empty<int>();

            const int max = 256;
            int* buf = stackalloc int[max];
            int count = Math.Min(max, ToriiAudioNative.ta_mixer_get_channels(ToriiAudioEngine.Handle, Handle, buf, max));
            if (count <= 0)
                return Array.Empty<int>();

            int[] result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = buf[i];
            return result;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            foreach (var channel in activeChannels.ToArray())
                Remove(channel);

            activeEffects.Clear();

            if (Handle != 0)
            {
                ToriiAudioNative.ta_mixer_destroy(ToriiAudioEngine.Handle, Handle);
                Handle = 0;
            }
        }
    }
}
