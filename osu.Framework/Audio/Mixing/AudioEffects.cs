// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Audio.Native;

namespace osu.Framework.Audio.Mixing
{
    /// <summary>
    /// Marker interface for real-time DSP effect parameters attachable to an <see cref="IAudioMixer"/>.
    /// </summary>
    public interface IEffectParameter
    {
        internal TaFxParams ToNativeParams();
    }

    public enum BQFType
    {
        LowPass = 0,
        HighPass = 1,
        BandPass = 2,
        Notch = 3,
        PeakingEQ = 4,
        LowShelf = 5,
        HighShelf = 6,
        AllPass = 7,
    }

    /// <summary>
    /// RBJ biquad filter parameters (low-pass, high-pass, band-pass, notch, peaking EQ, shelves, all-pass).
    /// </summary>
    public class BQFParameters : IEffectParameter
    {
        public BQFType lFilter { get; set; } = BQFType.LowPass;
        public float fCenter { get; set; } = 1000f;
        public float fGain { get; set; } = 0f;
        public float fBandwidth { get; set; } = 0f;
        public float fQ { get; set; } = 0.7f;
        public float fS { get; set; } = 0f;

        TaFxParams IEffectParameter.ToNativeParams() => new TaFxParams
        {
            Type = (int)lFilter,
            CenterHz = fCenter,
            Q = fQ,
            Bandwidth = fBandwidth,
            GainDb = fGain,
        };
    }

    public class ChorusParameters : IEffectParameter
    {
        public float fDryMix { get; set; } = 0.5f;
        public float fWetMix { get; set; } = 0.5f;
        public float fFeedback { get; set; } = 0.25f;
        public float fMinSweep { get; set; } = 1f;
        public float fMaxSweep { get; set; } = 20f;
        public float fRate { get; set; } = 1.5f;

        TaFxParams IEffectParameter.ToNativeParams() => new TaFxParams
        {
            Type = (int)TaFxType.Chorus,
            DryMix = fDryMix,
            WetMix = fWetMix,
            Feedback = fFeedback,
            RateHz = fRate,
            Depth = 0.35f,
            DelayMs = (fMinSweep + fMaxSweep) * 0.5f,
        };
    }

    public class PhaserParameters : IEffectParameter
    {
        public float fDryMix { get; set; } = 0.5f;
        public float fWetMix { get; set; } = 0.5f;
        public float fFeedback { get; set; } = 0.0f;
        public float fRate { get; set; } = 0.7f;
        public float fRange { get; set; } = 4.0f;
        public float fFreq { get; set; } = 1000f;

        TaFxParams IEffectParameter.ToNativeParams() => new TaFxParams
        {
            Type = (int)TaFxType.Phaser,
            DryMix = fDryMix,
            WetMix = fWetMix,
            Feedback = fFeedback,
            RateHz = fRate,
            RangeOctaves = fRange,
            CenterHz = fFreq,
        };
    }

    public class AutoWahParameters : IEffectParameter
    {
        public float fDryMix { get; set; } = 0.5f;
        public float fWetMix { get; set; } = 0.5f;
        public float fFeedback { get; set; } = 0.5f;
        public float fRate { get; set; } = 2.0f;
        public float fRange { get; set; } = 4.3f;
        public float fFreq { get; set; } = 50f;

        TaFxParams IEffectParameter.ToNativeParams() => new TaFxParams
        {
            Type = (int)TaFxType.AutoWah,
            DryMix = fDryMix,
            WetMix = fWetMix,
            Feedback = fFeedback,
            RateHz = fRate,
            RangeOctaves = fRange,
            CenterHz = fFreq,
        };
    }
}
