// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Audio
{
    /// <summary>
    /// Legacy Android Oboe redirector compatibility shim.
    /// With <c>torii_audio</c> (miniaudio), Android natively outputs directly via AAudio / OpenSL ES
    /// in low-latency mode without needing BASS decode-mixer redirection.
    /// </summary>
    public class OboeAudioRedirector : IDisposable
    {
        public bool IsRedirecting => false;

        public nint Provider => 0;

        internal static volatile int ActiveMasterMixer;

        public OboeAudioRedirector(AudioManager audioManager)
        {
        }

        public void RefreshMixers(int hardwareSampleRate)
        {
        }

        public void Dispose()
        {
        }
    }
}
