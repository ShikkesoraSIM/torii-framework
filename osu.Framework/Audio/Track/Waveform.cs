// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Audio.Native;
using osu.Framework.Extensions;
using osu.Framework.Logging;
using osu.Framework.Utils;

namespace osu.Framework.Audio.Track
{
    /// <summary>
    /// Processes audio sample data such that it can then be consumed to generate waveform plots of the audio.
    /// Backed by single-pass deviceless native <c>torii_audio</c> decoding + FFT1024.
    /// </summary>
    public class Waveform : IDisposable
    {
        private int channels;
        private Point[] points = Array.Empty<Point>();

        private readonly CancellationTokenSource cancelSource = new CancellationTokenSource();

        private readonly Task readTask;

        private Stream? data;

        public Waveform(Stream? data)
        {
            this.data = data;

            var token = cancelSource.Token;

            readTask = Task.Run(() =>
            {
                if (data == null)
                    return;

                try
                {
                    token.ThrowIfCancellationRequested();

                    byte[] rawBytes = ToriiAudioEngine.ReadStreamBytes(data);
                    if (rawBytes.Length < 4)
                        return;

                    token.ThrowIfCancellationRequested();

                    generateNative(rawBytes);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    Logger.Log($"torii_audio failure while reading waveform: {e.Message}");
                }
                finally
                {
                    data.Dispose();
                    this.data = data = null;
                }
            }, token);
        }

        private unsafe void generateNative(byte[] rawBytes)
        {
            fixed (byte* ptr = rawBytes)
            {
                if (ToriiAudioNative.ta_waveform_generate(ptr, (nuint)rawBytes.Length, out TaWaveformResult res) == 0)
                    return;

                try
                {
                    channels = res.Channels;

                    if (res.PointCount > 0 && res.Points != IntPtr.Zero)
                    {
                        var dst = new Point[res.PointCount];
                        long byteCount = (long)res.PointCount * sizeof(Point);
                        fixed (Point* dstPtr = dst)
                            Buffer.MemoryCopy(res.Points.ToPointer(), dstPtr, byteCount, byteCount);
                        points = dst;
                    }
                }
                finally
                {
                    ToriiAudioNative.ta_waveform_free(ref res);
                }
            }
        }

        public async Task<Waveform> GenerateResampledAsync(int pointCount, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(pointCount);

            if (pointCount == 0)
                return new Waveform(null);

            await readTask.ConfigureAwait(false);

            return await Task.Run(() =>
            {
                var generatedPoints = new Point[pointCount];

                float pointsPerGeneratedPoint = (float)points.Length / pointCount;

                const int kernel_width_factor = 3;

                int kernelWidth = (int)(pointsPerGeneratedPoint * kernel_width_factor) + 1;

                float[] filter = new float[kernelWidth + 1];

                for (int i = 0; i < filter.Length; ++i)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return new Waveform(null);

                    filter[i] = (float)Blur.EvalGaussian(i, pointsPerGeneratedPoint);
                }

                float originalPointIndex = 0;
                int generatedPointIndex = 0;

                while (generatedPointIndex < pointCount)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return new Waveform(null);

                    int startIndex = (int)originalPointIndex - kernelWidth;
                    int endIndex = (int)originalPointIndex + kernelWidth;

                    var point = new Point();
                    float totalWeight = 0;

                    for (int j = startIndex; j < endIndex; j++)
                    {
                        if (j < 0 || j >= points.Length) continue;

                        float weight = filter[Math.Abs(j - startIndex - kernelWidth)];
                        totalWeight += weight;

                        point.AmplitudeLeft += weight * points[j].AmplitudeLeft;
                        point.AmplitudeRight += weight * points[j].AmplitudeRight;
                        point.LowIntensity += weight * points[j].LowIntensity;
                        point.MidIntensity += weight * points[j].MidIntensity;
                        point.HighIntensity += weight * points[j].HighIntensity;
                    }

                    if (totalWeight > 0)
                    {
                        point.AmplitudeLeft /= totalWeight;
                        point.AmplitudeRight /= totalWeight;
                        point.LowIntensity /= totalWeight;
                        point.MidIntensity /= totalWeight;
                        point.HighIntensity /= totalWeight;
                    }

                    generatedPoints[generatedPointIndex] = point;

                    generatedPointIndex += 1;
                    originalPointIndex = generatedPointIndex * pointsPerGeneratedPoint;
                }

                return new Waveform(null)
                {
                    points = generatedPoints,
                    channels = channels
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        public Point[] GetPoints() => GetPointsAsync().GetResultSafely();

        public async Task<Point[]> GetPointsAsync()
        {
            await readTask.ConfigureAwait(false);
            return points;
        }

        public int GetChannels() => GetChannelsAsync().GetResultSafely();

        public async Task<int> GetChannelsAsync()
        {
            await readTask.ConfigureAwait(false);
            return channels;
        }

        #region Disposal

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool isDisposed;

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed)
                return;

            isDisposed = true;

            cancelSource.Cancel();
            cancelSource.Dispose();

            data?.Dispose();
            data = null;
        }

        #endregion

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public float AmplitudeLeft;
            public float AmplitudeRight;
            public float LowIntensity;
            public float MidIntensity;
            public float HighIntensity;
        }
    }
}
