// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Audio;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace osu.Framework.Tests.Visual.Audio
{
    public partial class TestSceneOboeBridge : FrameworkTestScene
    {
        private OboeAudioRedirector? audioRedirector;
        private OboeBridgeManager? nativeBridges;
        private Box? leftChannel;
        private Box? rightChannel;
        private Container? amplitudeBoxes;
        private AudioManager? audio;
        private TrackBass? bassTrack;
        private DrawableTrack? track;
        private SpriteText? statusText;

        [BackgroundDependencyLoader]
        private void load(Game game, ITrackStore tracks)
        {
            audio = game.Audio;

            bassTrack = (TrackBass)tracks.Get("sample-track.mp3");
            int length = bassTrack.CurrentAmplitudes.FrequencyAmplitudes.Length;

            Children = new Drawable[]
            {
                track = new DrawableTrack(bassTrack),
                new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            statusText = new SpriteText(),
                        },
                        new Drawable[]
                        {
                            new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Children = new Drawable[]
                                {
                                    leftChannel = new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.CentreRight,
                                    },
                                    rightChannel = new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.CentreLeft,
                                    }
                                }
                            },
                        },
                        new Drawable[]
                        {
                            amplitudeBoxes = new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                ChildrenEnumerable =
                                    Enumerable.Range(0, length)
                                              .Select(i => new Box
                                              {
                                                  RelativeSizeAxes = Axes.Both,
                                                  RelativePositionAxes = Axes.X,
                                                  Anchor = Anchor.BottomLeft,
                                                  Origin = Anchor.BottomLeft,
                                                  Width = 1f / length,
                                                  X = (float)i / length
                                              })
                            },
                        }
                    }
                },
            };

            audioRedirector = new OboeAudioRedirector(game.Audio);

        }
        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (track != null)
                track.Looping = true;
            AddStep("start track", () => track?.Start());
            AddStep("stop track", () => track?.Stop());

            AddStep("start bridge", () =>
            {
                startOboeBridge(audioRedirector != null ? audioRedirector.Provider : IntPtr.Zero, sampleRate =>
                {
                    audioRedirector?.RefreshMixers(sampleRate);
                    Logger.Log("[osu!] Audio redirector refreshed with hardware sample rate: " + sampleRate);
                });
            });

            AddStep("stop bridge", () => stopOboeBridge());
        }


        protected override void Update()
        {
            base.Update();

            if (bassTrack == null || rightChannel == null || leftChannel == null || amplitudeBoxes == null || statusText == null)
                return;

            var amplitudes = bassTrack.CurrentAmplitudes;

            rightChannel.Width = amplitudes.RightChannel * 0.5f;
            leftChannel.Width = amplitudes.LeftChannel * 0.5f;

            var freqAmplitudes = amplitudes.FrequencyAmplitudes.Span;

            for (int i = 0; i < freqAmplitudes.Length; i++)
                amplitudeBoxes[i].Height = freqAmplitudes[i];

            statusText.Text = nativeBridges?.GetOboeStatus() ?? "No bridge";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void startOboeBridge(IntPtr provider, Action<int>? onStarted = null)
        {
            int hardwareSampleRate = 48000;  // Ideally we should fetch the device native rate (AudioManager.PROPERTY_OUTPUT_SAMPLE_RATE on Android), but that's Android-specific so the framework test scene just uses a fixed value. sry

            nativeBridges ??= new OboeBridgeManager();

            if (nativeBridges is OboeBridgeManager mgr)
                mgr.StartOboeBridge(provider, hardwareSampleRate, onStarted);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void stopOboeBridge()
        {
            nativeBridges?.StopOboeBridge();
            audioRedirector?.Dispose();

            if (audio == null)
                return;

            audioRedirector = new OboeAudioRedirector(audio);
        }

    }
}
