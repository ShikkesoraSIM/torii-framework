// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Input.Handlers;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Platform.OpenXR;

namespace osu.Framework.Android.XR
{
    /// <summary>
    /// A host for immersive OpenXR applications on Android (e.g. Meta Quest). See <see cref="OpenXRHostController"/>.
    /// </summary>
    public class OpenXRAndroidGameHost : AndroidGameHost, IOpenXRHost
    {
        public OpenXRHostController OpenXR { get; }

        // Cached: converting a method group to a delegate allocates, and this is passed every frame.
        private readonly Action drawGame;

        private AndroidGameWindow? xrWindow;

        public OpenXRAndroidGameHost(AndroidGameActivity activity, Size? panelSize = null)
            : base(activity)
        {
            // Debug aids for development, enabled by creating files with `adb shell touch <external files dir>/<name>`.
            OpenXR = new OpenXRHostController(new AndroidOpenXRPlatform(activity), panelSize ?? new Size(1920, 1080),
                activity.GetExternalFilesDir(null)?.AbsolutePath);

            OpenXR.ExitRequested += () => Activity.RunOnUiThread(() => Activity.FinishAndRemoveTask());

            // There's nothing but the headset to show the game in: a failed session is started again, and if that doesn't help the app
            // closes rather than run on unseen (and heard), which can only be ended from the system menu.
            OpenXR.SessionRecoveryAttempts = 3;
            OpenXR.SessionFailed += () =>
            {
                Logger.Log("OpenXR couldn't be recovered; closing the app.", level: LogLevel.Error);
                Activity.RunOnUiThread(() => Activity.FinishAndRemoveTask());
            };
            drawGame = () => base.DrawFrame();
        }

        // The XR frame is waited for before the update and rendered in the draw, so both must run on one thread.
        protected override ExecutionMode? ForcedExecutionMode => ExecutionMode.SingleThread;

        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface)
        {
            xrWindow = new AndroidGameWindow(preferredSurface, Options.FriendlyGameName, externallyManagedActivity: true)
            {
                ClientSizeOverride = OpenXR.PanelSize,
            };

            OpenXR.IsDisplayed.BindValueChanged(d => xrWindow.ExternalIsActive!.Value = d.NewValue, true);
            return xrWindow;
        }

        public override ExternalRenderTarget GetExternalRenderTarget(string name) => OpenXR.GetExternalRenderTarget(name);

        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers()
            => base.CreateAvailableInputHandlers().Prepend(OpenXR.ControllerInput);

        protected override void UpdateFrame()
        {
            OpenXR.BeforeUpdate();
            base.UpdateFrame();
        }

        protected override void DrawFrame()
        {
            if (!AndroidGameActivity.Surface.IsSurfaceReady)
                return;

            OpenXR.Draw(Renderer, drawGame);
        }

        protected override void Swap() => OpenXR.OnSwap();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                OpenXR.Dispose();

            base.Dispose(disposing);
        }
    }
}
