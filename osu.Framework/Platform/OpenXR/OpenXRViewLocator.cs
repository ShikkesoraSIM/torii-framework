// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Platform.OpenXR.Native;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Locates the eye views once per frame, for every projection layer of that frame to be rendered and submitted with the same poses.
    /// </summary>
    /// <remarks>
    /// A runtime's prediction for the same display time changes from one call to the next as tracking data comes in. Layers rendered
    /// with different poses should each be reprojected from their own, but SteamVR reprojects them all alike: a second layer located
    /// separately visibly wobbles against the first as the head moves.
    /// </remarks>
    internal sealed unsafe class OpenXRViewLocator
    {
        private readonly XrFunctions xr;
        private readonly ulong session;

        private readonly XrView[] cached = new XrView[2];
        private ulong cachedSpace;
        private long cachedTime;
        private bool cachedValid;

        public OpenXRViewLocator(XrFunctions xr, ulong session)
        {
            this.xr = xr;
            this.session = session;
        }

        /// <summary>
        /// Gets both eye views in <paramref name="space"/> at <paramref name="displayTime"/>.
        /// </summary>
        /// <returns>Whether the views are tracked. <paramref name="views"/> is left untouched otherwise.</returns>
        public bool Locate(ulong space, long displayTime, XrView[] views)
        {
            if (space != cachedSpace || displayTime != cachedTime)
            {
                var locateInfo = new XrViewLocateInfo
                {
                    type = XR_TYPE_VIEW_LOCATE_INFO,
                    viewConfigurationType = XrViewConfigurationType.XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                    displayTime = displayTime,
                    space = space,
                };

                var viewState = new XrViewState { type = XR_TYPE_VIEW_STATE };
                uint count;

                fixed (XrView* v = cached)
                {
                    v[0].type = XR_TYPE_VIEW;
                    v[1].type = XR_TYPE_VIEW;
                    xr.xrLocateViews(session, &locateInfo, &viewState, 2, &count, v).ThrowIfFailed(nameof(xr.xrLocateViews));
                }

                const ulong valid = XrViewStateFlags.XR_VIEW_STATE_POSITION_VALID_BIT | XrViewStateFlags.XR_VIEW_STATE_ORIENTATION_VALID_BIT;

                cachedSpace = space;
                cachedTime = displayTime;
                cachedValid = count == 2 && (viewState.viewStateFlags & valid) == valid;
            }

            if (!cachedValid)
                return false;

            Array.Copy(cached, views, 2);
            return true;
        }
    }
}
