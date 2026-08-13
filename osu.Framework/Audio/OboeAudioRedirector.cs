// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Bindables;

namespace osu.Framework.Audio
{
    /// <summary>
    /// Redirects all of the framework's BASS audio output through a single decode-only
    /// "global" mixer that we own, so the unmanaged Oboe audio callback can pull frames
    /// from it (instead of BASS playing audio directly via its own AudioTrack/AAudio
    /// backend, which double-buffers on top of Oboe and defeats the whole low-latency
    /// goal).
    ///
    /// <para>
    /// Implementation rests on the framework's public <c>AudioManager.GlobalMixerHandle</c>
    /// hook (see <c>osu.Framework/Audio/AudioManager.cs</c> —exposed publicly in
    /// winnerspiros/osu-framework PR #20 specifically for this redirector). When this
    /// <see cref="Nullable{Int32}"/> bindable holds a non-null mixer handle, every
    /// <c>BassAudioMixer.createMixer</c> call (TrackMixer, SampleMixer, every per-store user mixer)
    /// recreates itself with the <see cref="BassFlags.Decode"/> flag and auto-attaches itself to the
    /// global mixer —i.e. the framework stops driving the audio device itself and produces decoded
    /// PCM only. The global mixer's owner is then responsible for actually feeding that
    /// PCM to whatever output backend is in use.
    /// </para>
    ///
    /// <para>
    /// On Windows the framework wires this up itself for experimental WASAPI (see
    /// <c>osu.Framework/Threading/AudioThread.cs</c> <c>initWasapi</c> —its
    /// <c>wasapiProcedure</c> callback is literally <c>Bass.ChannelGetData(globalMixerHandle.Value, …)</c>).
    /// We do exactly the same thing here for Oboe on Android: <see cref="provideAudio"/>
    /// is the unmanaged callback Oboe invokes from its real-time audio thread and it
    /// just pulls float frames from our decode mixer.
    /// </para>
    ///
    /// <para>
    /// The previous implementation tried to attach the framework's existing playback
    /// mixers to a master mixer at runtime via <c>BassMix.MixerAddChannel</c> + manual
    /// <c>Bass.ChannelSetDevice</c> calls. That approach is fundamentally unsupported by
    /// BASS —non-decode mixers cannot be added as sources to another mixer —and is
    /// the reason the status surface read <c>[No Redirect]</c> in user logs even though
    /// the Oboe stream itself was up and running. Routing through the framework's
    /// official hook removes all of that brittle reflection / device juggling.
    /// </para>
    /// </summary>
    public class OboeAudioRedirector : IDisposable
    {
        public bool IsRedirecting => ActiveMasterMixer != 0 && globalMixerHandleSet;

        private readonly AudioManager audioManager;
        private int masterMixer;
        private int sampleRate = 48000;
        private bool globalMixerHandleSet;

        // Cached writable handle to AudioManager.GlobalMixerHandle. The framework exposes
        // the bindable as IBindable<int?> (read-only interface), but the underlying field
        // is a Bindable<int?> —so we down-cast once and reuse it. Method-level reflection
        // for EnqueueAction / UpdateDevice is kept (those remain internal/protected).
        private Bindable<int?>? globalMixerHandleWritable;
        private MethodInfo? cachedUpdateDeviceMethod;
        private MethodInfo? cachedEnqueueActionMethod;

        public OboeAudioRedirector(AudioManager audioManager)
        {
            this.audioManager = audioManager;
        }

        /// <summary>
        /// Returns an unmanaged function pointer to <see cref="provideAudio"/> in a
        /// shape Oboe's native bridge can store and invoke from its real-time callback
        /// without needing P/Invoke marshalling per call.
        /// </summary>
        public unsafe nint Provider => (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&provideAudio;

        /// <summary>
        /// Establishes (or re-establishes after a sample-rate change) Oboe redirection.
        /// Idempotent —safe to call repeatedly; previous state is torn down first.
        /// </summary>
        /// <param name="hardwareSampleRate">
        /// The sample rate Oboe actually opened the AAudio stream at, as reported by
        /// the native bridge. Falls through to the previous value if zero or negative.
        /// </param>
        public void RefreshMixers(int hardwareSampleRate)
        {
            if (hardwareSampleRate > 0)
                sampleRate = hardwareSampleRate;

            Console.WriteLine($"[torii] Oboe redirector: refreshing master mixer @ {sampleRate}Hz");

            // All BASS mixer calls + framework-mixer recreation MUST run on the audio
            // thread —BassAudioMixer.activeChannels and friends are only safe to read
            // from there, and the framework's own WASAPI redirection (the reference
            // implementation we mirror) is invoked from initCurrentDevice on the audio
            // thread for exactly this reason. RefreshMixers itself is typically called
            // from the Update thread (it's invoked from the Oboe `onStarted` callback
            // which is marshalled there), so we have to hop.
            //
            // EnqueueAction on AudioComponent queues onto the audio thread and is the
            // cleanest available primitive —but it's protected, hence the reflection.
            if (!enqueueOnAudioThread(refreshMixersOnAudioThread))
            {
                // Fall back to running synchronously if reflection failed for any reason.
                // The audio-thread requirement is best-effort; running off-thread will
                // succeed in the common case (BASS is largely thread-safe) at the cost
                // of a remote chance of a transient mixer-state race.
                refreshMixersOnAudioThread();
            }
        }

        private void refreshMixersOnAudioThread()
        {
            teardown();

            // Decode + Float so ChannelGetData returns interleaved 32-bit float frames in
            // exactly the format Oboe wants. MixerNonStop avoids ChannelGetData returning
            // BASS_ERROR_ENDED (which Oboe would interpret as a stream error) when no
            // upstream channel is currently producing data —the global mixer just
            // emits silence in that case, matching the framework's wasapiProcedure.
            int handle = BassMix.CreateMixerStream(sampleRate, 2, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);

            if (handle == 0)
            {
                Console.WriteLine($"[torii] Oboe redirector: CreateMixerStream failed: {Bass.LastError}");
                return;
            }

            // Match the framework's per-mixer policy of zero playback buffer for lowest
            // possible BASS-side latency (see BassAudioMixer.createMixer).
            Bass.ChannelSetAttribute(handle, ChannelAttribute.Buffer, 0);

            masterMixer = handle;
            ActiveMasterMixer = handle;

            if (!setGlobalMixerHandle(handle))
            {
                Console.WriteLine("[torii] Oboe redirector: failed to set GlobalMixerHandle, aborting redirection");
                Bass.StreamFree(handle);
                masterMixer = 0;
                ActiveMasterMixer = 0;
                return;
            }

            globalMixerHandleSet = true;

            // The framework's existing mixers were created in playback mode (without
            // BassFlags.Decode) —they will not auto-attach to our global mixer until
            // they are recreated. Triggering AudioCollectionManager<T>.UpdateDevice on
            // the AudioManager iterates every IBassAudio child (the TrackMixer, the
            // SampleMixer, and any per-store user mixers added via AddItem) and calls
            // BassAudioMixer.UpdateDevice, which re-runs createMixer —at which point
            // GlobalMixerHandle.Value is non-null and the new handles are decode mixers
            // attached to our master. From that moment on, Oboe is the only thing
            // actually emitting audio.
            triggerMixerRecreation();

            Console.WriteLine($"[torii] Oboe redirector active: GlobalMixerHandle={handle} sampleRate={sampleRate}Hz");
        }

        /// <summary>
        /// Schedules <paramref name="action"/> onto the framework's audio thread via
        /// the inherited (and protected) <c>AudioComponent.EnqueueAction</c> method.
        /// Returns false if the reflective lookup couldn't be resolved.
        /// </summary>
        private bool enqueueOnAudioThread(Action action)
        {
            try
            {
                if (cachedEnqueueActionMethod == null)
                {
                    var t = audioManager.GetType();

                    while (t != null && cachedEnqueueActionMethod == null)
                    {
                        cachedEnqueueActionMethod = t.GetMethod(
                            "EnqueueAction",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                            binder: null,
                            types: new[] { typeof(Action) },
                            modifiers: null);
                        t = t.BaseType;
                    }
                }

                if (cachedEnqueueActionMethod == null)
                    return false;

                cachedEnqueueActionMethod.Invoke(audioManager, new object[] { action });
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[torii] enqueueOnAudioThread failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets the framework's <see cref="AudioManager.GlobalMixerHandle"/> bindable to the
        /// given handle (or <see langword="null"/> to detach). The bindable is exposed
        /// publicly by the framework (winnerspiros/osu-framework PR #20) as
        /// <c>IBindable&lt;int?&gt;</c>; the underlying instance is a writable
        /// <c>Bindable&lt;int?&gt;</c>, so a one-time down-cast lets us set the value
        /// without any reflection.
        /// </summary>
        private bool setGlobalMixerHandle(int? handle)
        {
            try
            {
                if (globalMixerHandleWritable == null)
                {
                    globalMixerHandleWritable = audioManager.GlobalMixerHandle as Bindable<int?>;

                    if (globalMixerHandleWritable == null)
                    {
                        Console.WriteLine("[torii] AudioManager.GlobalMixerHandle is not a writable Bindable<int?> —framework version mismatch?");
                        return false;
                    }
                }

                globalMixerHandleWritable.Value = handle;
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[torii] setGlobalMixerHandle failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Triggers <c>AudioCollectionManager&lt;AudioManager&gt;.UpdateDevice(int)</c>
        /// on the <see cref="audioManager"/> instance. This walks every child IBassAudio
        /// component (most importantly every <c>BassAudioMixer</c>) and calls its
        /// <c>UpdateDevice</c> method, which in turn calls <c>createMixer</c> —picking
        /// up the new <c>GlobalMixerHandle.Value</c> we just set.
        /// </summary>
        private void triggerMixerRecreation()
        {
            try
            {
                if (cachedUpdateDeviceMethod == null)
                {
                    var t = typeof(AudioManager);

                    while (t != null && cachedUpdateDeviceMethod == null)
                    {
                        cachedUpdateDeviceMethod = t.GetMethod(
                            "UpdateDevice",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                            binder: null,
                            types: new[] { typeof(int) },
                            modifiers: null);
                        t = t.BaseType;
                    }
                }

                if (cachedUpdateDeviceMethod == null)
                {
                    Console.WriteLine("[torii] AudioCollectionManager.UpdateDevice(int) not found via reflection —cannot force mixer recreation");
                    return;
                }

                cachedUpdateDeviceMethod.Invoke(audioManager, new object[] { Bass.CurrentDevice });
            }
            catch (Exception e)
            {
                Console.WriteLine($"[torii] triggerMixerRecreation failed: {e.Message}");
            }
        }

        /// <summary>
        /// Reverses <see cref="RefreshMixers"/>: detaches the global mixer hook so the
        /// framework's mixers go back to driving BASS playback themselves, then frees
        /// our master mixer. Order matters —the global-mixer detach + recreation must
        /// happen first so no framework mixer holds a child reference into the handle
        /// we are about to free.
        /// </summary>
        private void teardown()
        {
            if (globalMixerHandleSet)
            {
                setGlobalMixerHandle(null);
                globalMixerHandleSet = false;

                // Recreate framework mixers in playback mode so audio resumes via BASS's
                // own device output (the user is either disabling Oboe or we're tearing
                // down because Oboe is being re-initialised at a new sample rate —in
                // both cases we want a clean detach).
                try { triggerMixerRecreation(); }
                catch { /* best-effort; Dispose path must not throw */ }
            }

            ActiveMasterMixer = 0;

            if (masterMixer != 0)
            {
                Bass.StreamFree(masterMixer);
                masterMixer = 0;
            }
        }

        /// <summary>
        /// Unmanaged callback handed to the Oboe native bridge. Pulls <paramref name="numFrames"/>
        /// stereo float frames from the global mixer into <paramref name="audioData"/>.
        /// Mirrors <c>AudioThread.wasapiProcedure</c> in osu-framework verbatim —the
        /// only thing different is the calling backend (Oboe instead of BassWasapi).
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "provideAudio", CallConvs = new[] { typeof(CallConvCdecl) })]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int provideAudio(nint audioData, int numFrames)
        {
            int mixer = ActiveMasterMixer;

            if (mixer == 0) return 0;

            // 2 channels ﾃ・4 bytes per float = 8 bytes per frame.
            int bytesToRead = numFrames * 8;
            int bytesRead = Bass.ChannelGetData(mixer, audioData, bytesToRead);

            if (bytesRead <= 0) return 0;

            return bytesRead / 8;
        }

        /// <summary>
        /// Volatile because <see cref="provideAudio"/> runs on Oboe's real-time audio
        /// thread while management writes happen on the AudioThread / Update thread.
        /// A torn read of an int isn't possible on any platform osu! supports, but
        /// volatile makes the cross-thread visibility explicit.
        /// </summary>
        internal static volatile int ActiveMasterMixer;

        public void Dispose()
        {
            // Tear down on the audio thread for the same reason RefreshMixers does
            // its work there. Falling back to inline execution if the queue lookup
            // failed mirrors the RefreshMixers fallback policy and keeps Dispose
            // best-effort instead of throwing during shutdown.
            if (!enqueueOnAudioThread(teardown))
                teardown();
        }
    }
}
