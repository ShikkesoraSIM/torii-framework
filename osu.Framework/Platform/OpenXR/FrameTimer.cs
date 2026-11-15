// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text;
using osu.Framework.Statistics;
using osu.Framework.Logging;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Measures where the time of each XR frame goes (waiting for the runtime, updating, drawing the game, XR rendering, submitting),
    /// and periodically logs averages and percentiles. A development aid for finding out whether, and where, a frame misses its deadline.
    /// </summary>
    /// <remarks>Allocation-free while recording; allocates only when logging.</remarks>
    internal class FrameTimer
    {
        public enum Phase
        {
            /// <summary>Everything outside the XR frame (single-threaded: input and audio threads' frames).</summary>
            Other,

            /// <summary>Blocked in xrWaitFrame.</summary>
            Wait,

            /// <summary>The game's update frame.</summary>
            Update,

            /// <summary>The game's draw frame (CPU side of rendering the panel).</summary>
            Draw,

            /// <summary>Rendering the environment and controllers.</summary>
            XrRender,

            /// <summary>xrEndFrame.</summary>
            Submit,
        }

        private static readonly int phase_count = Enum.GetValues<Phase>().Length;

        private readonly double logIntervalSeconds;
        private readonly double[][] samples;
        private readonly double[] frameTimes;
        private readonly double[] sortBuffer;
        private readonly long[] current;

        private int frames;
        private long phaseStart;
        private Phase phase = Phase.Other;
        private long frameStart;
        private long windowStart;
        private long lockContentions;
        private int gen0Collections;

        public FrameTimer(double logIntervalSeconds = 2, int maxFrames = 1024)
        {
            this.logIntervalSeconds = logIntervalSeconds;
            samples = new double[phase_count][];
            for (int i = 0; i < phase_count; i++)
                samples[i] = new double[maxFrames];
            frameTimes = new double[maxFrames];
            sortBuffer = new double[maxFrames];
            current = new long[phase_count];
        }

        /// <summary>
        /// Ends the current phase and starts <paramref name="next"/>. Entering <see cref="Phase.Wait"/> starts a new frame.
        /// </summary>
        public void Enter(Phase next)
        {
            long now = Stopwatch.GetTimestamp();

            if (phaseStart != 0)
                current[(int)phase] += now - phaseStart;

            if (next == Phase.Wait)
                endFrame(now);

            phase = next;
            phaseStart = now;
        }

        // What the frames were spent on: per-frame counters the framework keeps (published a frame late, which averages out).
        private static readonly (string group, string name)[] counters =
        {
            ("Draw", "DrawCalls"), ("Draw", "FBORedraw"), ("Draw", "TextureBinds"), ("Draw", "VerticesUpl"),
            ("Update", "Invalidations"), ("Update", "DrawNodeAppl"), ("Update", "DrawNodeCtor"), ("Update", "ScheduleInvk"),
        };

        private readonly GlobalStatistic<long>[] counterStatistics = new GlobalStatistic<long>[counters.Length];
        private readonly double[] counterSums = new double[counters.Length];
        private GlobalStatistic<int> textureUploads;
        private double textureUploadSum;
        private long allocatedBytes;
        private TimeSpan gcPauseTotal;

        // The same, for the frames that took over 1.5 frames at 120 Hz (the counters are a frame late: the frame after a slow one is counted).
        private const double slow_frame_ms = 12.5;
        private readonly double[] slowCounterSums = new double[counters.Length];
        private double slowTextureUploadSum, slowUpdate, slowDraw;
        private int slowFrames;
        private bool lastFrameSlow;

        private void sampleCounters(double update, double draw, double work)
        {
            textureUploads ??= GlobalStatistics.GetStatistics().OfType<GlobalStatistic<int>>().FirstOrDefault(stat => stat.Name == "Texture uploads performed");

            for (int i = 0; i < counters.Length; i++)
            {
                counterStatistics[i] ??= GlobalStatistics.Get<long>(counters[i].group, counters[i].name);
                counterSums[i] += counterStatistics[i].Value;
                if (lastFrameSlow)
                    slowCounterSums[i] += counterStatistics[i].Value;
            }

            textureUploadSum += textureUploads?.Value ?? 0;
            if (lastFrameSlow)
                slowTextureUploadSum += textureUploads?.Value ?? 0;

            lastFrameSlow = work > slow_frame_ms;

            if (lastFrameSlow)
            {
                slowFrames++;
                slowUpdate += update;
                slowDraw += draw;
            }
        }

        private void endFrame(long now)
        {
            if (frameStart != 0 && frames < frameTimes.Length)
            {

                for (int i = 0; i < phase_count; i++)
                    samples[i][frames] = current[i] * 1000.0 / Stopwatch.Frequency;
                frameTimes[frames] = (now - frameStart) * 1000.0 / Stopwatch.Frequency;
                sampleCounters(samples[(int)Phase.Update][frames], samples[(int)Phase.Draw][frames], frameTimes[frames] - samples[(int)Phase.Wait][frames]);
                frames++;
            }

            Array.Clear(current);
            frameStart = now;

            if (windowStart == 0)
                windowStart = now;
            else if ((now - windowStart) >= logIntervalSeconds * Stopwatch.Frequency || frames == frameTimes.Length)
            {
                log((now - windowStart) / (double)Stopwatch.Frequency);
                frames = 0;
                windowStart = now;
            }
        }

        private void log(double seconds)
        {
            if (frames == 0)
                return;

            var sb = new StringBuilder();
            sb.Append($"OpenXR frame timing: {frames / seconds:0.0} fps; frame {stats(frameTimes)}");

            for (int i = 0; i < phase_count; i++)
                sb.Append($"; {(Phase)i} {stats(samples[i])}");

            // Work = everything except waiting for the runtime: what has to fit in the frame budget.
            for (int f = 0; f < frames; f++)
                sortBuffer[f] = frameTimes[f] - samples[(int)Phase.Wait][f];
            Array.Sort(sortBuffer, 0, frames);
            sb.Append($"; work avg {average(frameTimes) - average(samples[(int)Phase.Wait]):0.00} p50 {percentile(0.5):0.00} p95 {percentile(0.95):0.00} max {sortBuffer[frames - 1]:0.00} ms");

            long contentions = System.Threading.Monitor.LockContentionCount;
            int gen0 = GC.CollectionCount(0);
            sb.Append($"; lock contentions {contentions - lockContentions}, gen0 GCs {gen0 - gen0Collections}");
            lockContentions = contentions;
            gen0Collections = gen0;

            sb.Append("; per frame:");
            for (int i = 0; i < counters.Length; i++)
                sb.Append($" {counters[i].name} {counterSums[i] / frames:0.#}");
            sb.Append($" TexUploads {textureUploadSum / frames:0.##}");

            long allocated = GC.GetTotalAllocatedBytes();
            sb.Append($" allocated {(allocated - allocatedBytes) / 1024.0 / frames:0} KB");

            allocatedBytes = allocated;

            TimeSpan gcPause = GC.GetTotalPauseDuration();
            sb.Append($"; GC pauses {(gcPause - gcPauseTotal).TotalMilliseconds:0.0} ms");
            gcPauseTotal = gcPause;
            Array.Clear(counterSums);
            textureUploadSum = 0;

            if (slowFrames > 0)
            {
                sb.Append($"; {slowFrames} slow frames: Update {slowUpdate / slowFrames:0.0} Draw {slowDraw / slowFrames:0.0} ms,");
                for (int i = 0; i < counters.Length; i++)
                    sb.Append($" {counters[i].name} {slowCounterSums[i] / slowFrames:0.#}");
                sb.Append($" TexUploads {slowTextureUploadSum / slowFrames:0.##}");
                Array.Clear(slowCounterSums);
                slowTextureUploadSum = slowUpdate = slowDraw = 0;
                slowFrames = 0;
            }

            Logger.Log(sb.ToString());
        }

        private string stats(double[] values)
        {
            Array.Copy(values, sortBuffer, frames);
            Array.Sort(sortBuffer, 0, frames);
            return $"{average(values):0.00}/{percentile(0.95):0.00}/{sortBuffer[frames - 1]:0.00}";
        }

        private double average(double[] values)
        {
            double sum = 0;
            for (int i = 0; i < frames; i++)
                sum += values[i];
            return sum / frames;
        }

        private double percentile(double p) => sortBuffer[Math.Min(frames - 1, (int)(p * frames))];
    }
}
