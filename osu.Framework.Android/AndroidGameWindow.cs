// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Drawing;
using osu.Framework.Bindables;
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
    }
}
