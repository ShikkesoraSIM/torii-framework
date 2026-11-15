// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Drawing;
using Android.Content;
using Android.Views.InputMethods;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;

namespace osu.Framework.Android
{
    internal class AndroidGameWindow : SDL3MobileWindow
    {
        public override IntPtr SurfaceHandle => AndroidGameActivity.Surface.Holder?.Surface?.Handle ?? IntPtr.Zero;

        /// <summary>
        /// Torii: when set, the window reports this as its client size instead of the Android surface size.
        /// Used by immersive XR hosts, where the game renders to a virtual panel rather than the (invisible) Android surface.
        /// </summary>
        public Size? ClientSizeOverride { get; set; }

        public override Size ClientSize => ClientSizeOverride ?? base.ClientSize;

        private readonly BindableBool? externalIsActive;

        /// <summary>
        /// Torii: when the activity state is managed externally (e.g. by an OpenXR session), this controls <see cref="IsActive"/>
        /// instead of Android window focus. Null otherwise.
        /// </summary>
        public BindableBool? ExternalIsActive => externalIsActive;

        public override IBindable<bool> IsActive => externalIsActive ?? base.IsActive;

        /// <param name="surfaceType">The graphics surface type.</param>
        /// <param name="appName">The application name.</param>
        /// <param name="externallyManagedActivity">
        /// Whether <see cref="IsActive"/> is driven through <see cref="ExternalIsActive"/> rather than Android window focus.
        /// In immersive VR, Android window focus does not reflect whether the app is in use.
        /// </param>
        public AndroidGameWindow(GraphicsSurfaceType surfaceType, string appName, bool externallyManagedActivity = false)
            : base(surfaceType, appName)
        {
            if (externallyManagedActivity)
                externalIsActive = new BindableBool(true);
        }

        public override void Create()
        {
            base.Create();

            SafeAreaPadding.BindTo(AndroidGameActivity.Surface.SafeAreaPadding);
        }

        #region Clearing the soft keyboard's text

        /*
         * Torii: SDL takes soft keyboard input through a hidden text field, whose connection to the keyboard keeps everything typed
         * into it (in an EditText) for as long as the field stays focused. Text boxes the game moves focus between don't stop text
         * input in between, so the field is shown again with all of that still in it, and keyboards which show the field's text
         * (such as the Quest's) bring it up there: what was typed into an earlier text box, a password included, in plain text.
         * Restarting the field's connection whenever text input is started or stopped gives the keyboard a new, empty one.
         */

        public override void StartTextInput(TextInputProperties properties)
        {
            base.StartTextInput(properties);
            ScheduleCommand(restartTextFieldConnection);
        }

        public override void StopTextInput()
        {
            base.StopTextInput();
            ScheduleCommand(restartTextFieldConnection);
        }

        public override void ResetIme()
        {
            base.ResetIme();
            ScheduleCommand(restartTextFieldConnection);
        }

        /// <summary>
        /// Gives SDL's hidden text field a new connection to the soft keyboard, which leaves behind the text the old one held.
        /// </summary>
        /// <remarks>
        /// Called on the SDL thread after SDL has been told to show or hide the text field, which SDL does on the UI thread,
        /// so posting there runs this after it.
        /// </remarks>
        private static void restartTextFieldConnection()
        {
            var surface = AndroidGameActivity.Surface;

            surface.Post(() =>
            {
                try
                {
                    // While text input is active, the focused view is SDL's text field. Once it's stopped, focus goes back to the
                    // surface and the keyboard has let go of the field's connection, so there is nothing to restart.
                    var field = surface.RootView?.FindFocus();

                    if (field == null || field == surface || !field.OnCheckIsTextEditor())
                        return;

                    var inputMethodManager = (InputMethodManager?)surface.Context?.GetSystemService(Context.InputMethodService);
                    inputMethodManager?.RestartInput(field);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to clear the soft keyboard's text.");
                }
            });
        }

        #endregion
    }
}
