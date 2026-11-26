// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using Org.Libsdl.App;
using osu.Framework.Audio.Native;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Logging;
using Debug = System.Diagnostics.Debug;
using Uri = Android.Net.Uri;

namespace osu.Framework.Android
{
    // since `ActivityAttribute` can't be inherited, the below is only provided as an illustrative example of how to setup an activity for best compatibility.
    [Activity(ConfigurationChanges = DEFAULT_CONFIG_CHANGES, Exported = true, LaunchMode = DEFAULT_LAUNCH_MODE, MainLauncher = true)]
    public abstract class AndroidGameActivity : SDLActivity
    {
        protected const ConfigChanges DEFAULT_CONFIG_CHANGES = ConfigChanges.Keyboard
                                                               | ConfigChanges.KeyboardHidden
                                                               | ConfigChanges.Navigation
                                                               | ConfigChanges.Orientation
                                                               | ConfigChanges.ScreenLayout
                                                               | ConfigChanges.ScreenSize
                                                               | ConfigChanges.SmallestScreenSize
                                                               | ConfigChanges.Touchscreen
                                                               | ConfigChanges.UiMode;

        protected const LaunchMode DEFAULT_LAUNCH_MODE = LaunchMode.SingleInstance;

        internal static AndroidGameSurface Surface => (AndroidGameSurface)MSurface!;

        protected abstract Game CreateGame();

        protected override string[] GetLibraries() => new string[] { "SDL3" };

        protected override SDLSurface CreateSDLSurface(Context? context) => new AndroidGameSurface(this, context);

        protected override void Main()
        {
            var host = CreateHost();
            host.Run(CreateGame());
        }

        /// <summary>
        /// Creates the host which runs the game. Override to use a specialised host (e.g. an immersive OpenXR host).
        /// </summary>
        protected virtual AndroidGameHost CreateHost() => new AndroidGameHost(this);

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            Debug.Assert(RuntimeInfo.EntryAssembly.IsNull(), "RuntimeInfo.EntryAssembly should be null on Android and therefore needs to be manually updated.");
            RuntimeInfo.EntryAssembly = GetType().Assembly;

            // The default current directory on android is '/'.
            // On some devices '/' maps to the app data directory. On others it maps to the root of the internal storage.
            // In order to have a consistent current directory on all devices the full path of the app data directory is set as the current directory.
            System.Environment.CurrentDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

            recycleTempContentDirectory();

            base.OnCreate(savedInstanceState);
        }

        /// <summary>
        /// Whether the game is paused while the activity's window doesn't have focus (and isn't started before it has), as SDL does.
        /// </summary>
        /// <remarks>
        /// Not for immersive VR: the window isn't what's looked at there, and whether the app is displayed and has input is the
        /// OpenXR session's to say. A window which loses focus to something of the system's (or is never given it at launch) would
        /// otherwise stop the game, frames for the headset included, until focus comes back.
        /// </remarks>
        protected virtual bool PauseOnWindowFocusLoss => true;

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            if (!hasFocus && !PauseOnWindowFocusLoss)
            {
                Logger.Log("Window focus lost; carrying on regardless.");
                hasFocus = true;
            }

            base.OnWindowFocusChanged(hasFocus);
        }

        protected override void OnStop()
        {
            base.OnStop();
            ToriiAudioEngine.Pause();
        }

        /// <summary>
        /// How long the game is given to wind down once the activity is finishing, before the process is ended, in milliseconds.
        /// </summary>
        private const int finishing_grace_period = 1500;

        private int processExitScheduled;

        protected override void OnPause()
        {
            base.OnPause();

            // Android may hold back onDestroy for seconds, in which the app can be started again.
            if (IsFinishing && !IsChangingConfigurations)
                scheduleProcessExit(finishing_grace_period);
        }

        protected override void OnDestroy()
        {
            bool finishing = IsFinishing && !IsChangingConfigurations;

            // Asks the game to quit, and gives it a second to.
            base.OnDestroy();

            if (finishing)
                scheduleProcessExit(0);
        }

        /// <summary>
        /// Ends the process of an activity which is finishing.
        /// </summary>
        /// <remarks>
        /// Android keeps the process of a finished activity around, with the game in it stopped for good (or stuck in SDL's pause).
        /// The game can't be run a second time in a process, so starting the app again while that process lives fails: SDL exits the
        /// process on finding itself there, and the launch shows nothing. Only the launch after that one gets a new process.
        /// </remarks>
        private void scheduleProcessExit(int delay)
        {
            if (Interlocked.Exchange(ref processExitScheduled, 1) != 0 && delay > 0)
                return;

            var thread = new Thread(() =>
            {
                if (delay > 0)
                    Thread.Sleep(delay);

                Logger.Log("The activity has finished; ending the process.");
                Logger.Flush();
                global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
            })
            {
                IsBackground = true,
                Name = "ProcessExit",
            };

            thread.Start();
        }

        protected override void OnRestart()
        {
            base.OnRestart();
            ToriiAudioEngine.Resume();
        }

        #region Activity result handling

        internal const int OPEN_DOCUMENT = 2;

        internal event Action<int, Result, Intent?>? ActivityResultReceived;

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            ActivityResultReceived?.Invoke(requestCode, resultCode, data);
        }

        #endregion

        #region Handling files

        /*
         * Android is unique among all platforms in that it somehow constrains storage even worse than iOS and iPadOS do.
         * It does so in a particular way, namely by *obscuring the actual physical paths* under which files live.
         * Instead, Android exposes "content URIs" (https://developer.android.com/guide/topics/providers/content-provider-basics#ContentURIs)
         * which only work with Android-bespoke APIs.
         * There's a very strong smell of "you're not supposed to know where this file even is, because it doesn't matter
         * if you use our functions and the magic URI to access it" here.
         *
         * This does not work well with the rest of framework, which expects, you know, *files* and *directories* and *paths*,
         * not magic tokens that only get you what you want when redirected through Android APIs.
         * To avoid redirecting all other platforms through Byzantine abstractions just to accomodate Android,
         * we employ a dirty hack wherein files identified by content URIs are *temporarily* copied to a location the path of which we *can* divine.
         *
         * This will only work with games using this framework as long as said games use the file paths provided to them as temporary pointers to a piece of data
         * and not as persistent identifiers which should work forever.
         * That assumption happens to hold with osu!; not so much with other potential games.
         * To this end, the temporary hack directory is purged on every game launch in order to rather loudly fail if someone starts to lean on these hack paths.
         */

        private string tempContentDirectory => Path.Combine(CacheDir!.AbsolutePath, "temp-content");

        private void recycleTempContentDirectory()
        {
            try
            {
                if (Directory.Exists(tempContentDirectory))
                    Directory.Delete(tempContentDirectory, true);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to purge temporary content: {ex}");
            }

            Directory.CreateDirectory(tempContentDirectory);
        }

        public FileInfo CreateTemporaryFileFromContentUri(Uri contentUri)
        {
            // while content URIs are not real paths, in practice they *appear* to at least *sometimes* contain real filenames at the end.
            // try using that first, since it's the least likely thing to fail later.
            // the reason why this is important is that downstream consumers may depend on the *extension* of the file in particular,
            // and there's no guarantee that we can recover it safely from anywhere else.
            string? filename = Path.GetFileName(contentUri.Path);

            // if the content URI fails generate something else.
            if (string.IsNullOrEmpty(filename))
                filename = Path.GetRandomFileName();

            // check if we have an extension to use.
            // there are two cases where we might not:
            // - the content-providing app gave something resembling a filename but not an extension (known to happen in the wild)
            // - there was no actual filename to use at any point, and we just generated our own above
            // in both cases, attempt to synthesise an extension ourselves since there's nothing left to use anyway.
            if (string.IsNullOrEmpty(Path.GetExtension(filename)))
            {
                string? mimeType = ContentResolver?.GetType(contentUri);
                string? extension = MimeTypeMap.Singleton?.GetExtensionFromMimeType(mimeType);

                if (extension != null)
                    filename = Path.ChangeExtension(filename, extension);
            }

            string filePath = Path.Combine(tempContentDirectory, filename);

            using (var inStream = ContentResolver?.OpenInputStream(contentUri))
            using (var outStream = File.OpenWrite(filePath))
            {
                inStream?.CopyTo(outStream);
            }

            return new FileInfo(filePath);
        }

        #endregion
    }
}
