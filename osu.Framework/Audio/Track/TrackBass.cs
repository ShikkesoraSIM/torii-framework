// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Native;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ObjectExtensions;

namespace osu.Framework.Audio.Track
{
    public sealed class TrackBass : Track, IBassAudio, IBassAudioChannel
    {
        private Stream? dataStream;

        /// <summary>
        /// Should this track only be used for preview purposes?
        /// </summary>
        public bool Preview { get; private set; }

        /// <summary>
        /// Native <c>torii_audio</c> track handle.
        /// </summary>
        private int activeStream;

        /// <summary>
        /// Marks whether playback was requested by the user (and not stopped).
        /// </summary>
        private bool isPlayed;

        /// <summary>
        /// The last position that a seek will succeed for (1 frame before EOF).
        /// </summary>
        private double lastSeekablePosition;

        private volatile bool isLoaded;

        public override bool IsLoaded => isLoaded;

        private uint lastObservedEndEventCount;
        private double lastPushedRestartPoint;

        private BassAmplitudeProcessor? bassAmplitudeProcessor;

        internal TrackBass(Stream data, string name, bool quick = false)
            : base(name)
        {
            ArgumentNullException.ThrowIfNull(data);

            const float tempo_minimum_supported = 0.05f;

            AggregateTempo.ValueChanged += t =>
            {
                if (t.NewValue < tempo_minimum_supported)
                    throw new ArgumentException($"{nameof(TrackBass)} does not support {nameof(Tempo)} specifications below {tempo_minimum_supported}. Use {nameof(Frequency)} instead.");
            };

            EnqueueAction(() =>
            {
                Preview = quick;
                dataStream = data;

                activeStream = prepareStream(data, quick, out TaTrackInfo info);

                // We've copied the stream bytes into native memory — release managed stream immediately!
                dataStream?.Dispose();
                dataStream = null;

                if (activeStream != 0)
                {
                    Length = info.LengthMs;
                    lastSeekablePosition = info.LastSeekableMs;
                    bitrate = info.BitrateKbps;
                    isLoaded = true;

                    pushParamsToNative();

                    if (Mixer is BassAudioMixer bm)
                        bm.AddChannelToBassMix(this);
                }
            });

            InvalidateState();
        }

        private unsafe int prepareStream(Stream data, bool quick, out TaTrackInfo info)
        {
            info = default;

            byte* nativePtr;
            nuint length;
            try
            {
                if (!ToriiAudioEngine.ReadStreamToNative(data, out nativePtr, out length))
                    return 0;
            }
            catch
            {
                return 0;
            }

            if (length < 4 || nativePtr == null)
            {
                if (nativePtr != null)
                    ToriiAudioNative.ta_free_buffer(nativePtr);
                return 0;
            }

            // Ownership of nativePtr transfers to ta_track_create_owned (freed internally on failure or destroy)
            return ToriiAudioNative.ta_track_create_owned(ToriiAudioEngine.Handle, nativePtr, length, quick ? 1 : 0, out info);
        }

        void IBassAudio.UpdateDevice(int deviceIndex)
        {
            // Native track & mixer live inside ta_engine across device switches;
            // just re-ensure desired play state if needed.
            if (isPlayed && !hasCompleted && activeStream != 0)
                ToriiAudioNative.ta_track_play(ToriiAudioEngine.Handle, activeStream);
        }

        protected override void UpdateState()
        {
            base.UpdateState();

            if (activeStream != 0 && RestartPoint != lastPushedRestartPoint)
                pushParamsToNative();

            if (activeStream != 0 && ToriiAudioNative.ta_track_get_clock(ToriiAudioEngine.Handle, activeStream, out TaTrackClock clock) != 0)
            {
                Interlocked.Exchange(ref currentTime, clock.CurrentTimeMs);

                if (clock.EndEventCount != lastObservedEndEventCount)
                {
                    lastObservedEndEventCount = clock.EndEventCount;

                    if (!Looping)
                    {
                        hasCompleted = true;
                        isRunning = false;
                        RaiseCompleted();
                    }
                }
                else
                {
                    hasCompleted = clock.HasCompleted != 0;
                    bool running = clock.IsPlaying != 0;
                    isRunning = running || (isPlayed && !hasCompleted);
                }
            }
            else
            {
                isRunning = isPlayed && !hasCompleted && isLoaded;
            }

            bassAmplitudeProcessor?.Update();
        }

        public override bool IsDummyDevice => false;

        public override void Stop() => StopAsync().WaitSafely();

        public override Task StopAsync()
        {
            return EnqueueAction(() =>
            {
                stopInternal();
                isRunning = isPlayed = false;
            });
        }

        private void stopInternal()
        {
            if (activeStream == 0)
                return;

            ToriiAudioNative.ta_track_pause(ToriiAudioEngine.Handle, activeStream, 1);
            if (Mixer is BassAudioMixer bm)
                bm.ChannelPause(this, true);
        }

        public override void Start()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            StartAsync().WaitSafely();
        }

        public override Task StartAsync() => EnqueueAction(() =>
        {
            if (startInternal())
                isRunning = isPlayed = true;
        });

        private bool startInternal()
        {
            InvalidateState();

            if (hasCompleted || activeStream == 0)
                return false;

            pushParamsToNative();

            if (Mixer is BassAudioMixer bm)
                bm.ChannelPlay(this);

            return ToriiAudioNative.ta_track_play(ToriiAudioEngine.Handle, activeStream) != 0;
        }

        public override bool Looping
        {
            get => base.Looping;
            set
            {
                base.Looping = value;
                EnqueueAction(pushParamsToNative);
            }
        }

        public override bool Seek(double seek) => SeekAsync(seek).GetResultSafely();

        public override async Task<bool> SeekAsync(double seek)
        {
            double conservativeLength = Length == 0 ? double.MaxValue : lastSeekablePosition;
            double conservativeClamped = Math.Clamp(seek, 0, conservativeLength);

            await EnqueueAction(() => seekInternal(seek)).ConfigureAwait(false);

            return conservativeClamped == seek;
        }

        private void seekInternal(double seek)
        {
            if (activeStream == 0)
                return;

            // Match BASS contract (TrackBassTest.TestSeekToEndFails):
            // seeking to/beyond Track.Length fails without altering CurrentTime or HasCompleted.
            if (Length > 0 && seek > lastSeekablePosition)
                return;

            double clamped = Math.Clamp(seek, 0, lastSeekablePosition);

            if (clamped < Length)
                hasCompleted = false;

            ToriiAudioNative.ta_track_seek_ms(ToriiAudioEngine.Handle, activeStream, clamped, out double actualMs);
            Interlocked.Exchange(ref currentTime, actualMs);
        }

        private double currentTime;

        public override double CurrentTime => currentTime;

        private volatile bool isRunning;

        public override bool IsRunning => isRunning;

        private volatile bool hasCompleted;

        public override bool HasCompleted => hasCompleted;

        internal override void OnStateChanged()
        {
            base.OnStateChanged();
            pushParamsToNative();
        }

        private void pushParamsToNative()
        {
            if (activeStream == 0)
                return;

            lastPushedRestartPoint = RestartPoint;

            ToriiAudioNative.ta_track_set_params(
                ToriiAudioEngine.Handle,
                activeStream,
                (float)AggregateVolume.Value,
                (float)AggregateBalance.Value,
                AggregateFrequency.Value,
                Math.Max(0.05, Math.Abs(AggregateTempo.Value)),
                Looping ? 1 : 0,
                RestartPoint);
        }

        private volatile int bitrate;

        public override int? Bitrate => bitrate;

        public override ChannelAmplitudes CurrentAmplitudes => (bassAmplitudeProcessor ??= new BassAmplitudeProcessor(this)).CurrentAmplitudes;

        #region Mixing

        protected override AudioMixer? Mixer
        {
            get => base.Mixer;
            set
            {
                base.Mixer = value;

                if (Mixer != null && activeStream != 0)
                    bassMixer.AddChannelToBassMix(this);
            }
        }

        private BassAudioMixer bassMixer => (BassAudioMixer)Mixer.AsNonNull();

        bool IBassAudioChannel.IsActive => !IsDisposed;

        int IBassAudioChannel.Handle => activeStream;

        bool IBassAudioChannel.MixerChannelPaused { get; set; } = true;

        BassAudioMixer IBassAudioChannel.Mixer => bassMixer;

        #endregion

        ~TrackBass()
        {
            Dispose(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (activeStream != 0)
            {
                isRunning = false;
                if (Mixer is BassAudioMixer bm)
                    bm.StreamFree(this);
                ToriiAudioNative.ta_track_destroy(ToriiAudioEngine.Handle, activeStream);
                activeStream = 0;
            }

            dataStream?.Dispose();
            dataStream = null;

            base.Dispose(disposing);
        }
    }
}
