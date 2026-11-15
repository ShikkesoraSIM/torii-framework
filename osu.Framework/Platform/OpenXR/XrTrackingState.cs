// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Numerics;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Where the viewer's head, the controllers and the game panel were for one frame, all in the session's LOCAL reference space
    /// (metres; +Y up). See <see cref="OpenXRHostController.GetTrackingState"/>.
    /// </summary>
    public struct XrTrackingState
    {
        /// <summary>
        /// Counts the frames tracking was sampled for: unchanged since the last read means there is nothing new.
        /// Zero until the first frame.
        /// </summary>
        public long Frame;

        /// <summary>
        /// Whether <see cref="HeadPosition"/> and <see cref="HeadOrientation"/> were tracked this frame (they keep their last values otherwise).
        /// </summary>
        public bool HeadValid;

        public Vector3 HeadPosition;

        /// <summary>
        /// The head looks along -Z of this orientation.
        /// </summary>
        public Quaternion HeadOrientation;

        public XrHandState LeftHand;
        public XrHandState RightHand;

        /// <summary>
        /// The controller driving the pointer (<see cref="OpenXRInput.LEFT"/> or <see cref="OpenXRInput.RIGHT"/>).
        /// </summary>
        public int ActiveHand;

        /// <summary>
        /// The anchor the panel's placement is relative to (see <see cref="OpenXRHostController.PanelOffset"/> and
        /// <see cref="OpenXRHostController.PanelRotation"/>): the head when last recentred, facing where the viewer looked.
        /// </summary>
        public Vector3 AnchorPosition;

        public Quaternion AnchorOrientation;

        /// <summary>
        /// See <see cref="PanelGeometry.Centre"/>.
        /// </summary>
        public Vector3 PanelCentre;

        /// <summary>
        /// See <see cref="PanelGeometry.Orientation"/>.
        /// </summary>
        public Quaternion PanelOrientation;

        /// <summary>
        /// See <see cref="PanelGeometry.Radius"/>.
        /// </summary>
        public float PanelRadius;

        /// <summary>
        /// See <see cref="PanelGeometry.HorizontalAngle"/>.
        /// </summary>
        public float PanelHorizontalAngle;

        /// <summary>
        /// See <see cref="PanelGeometry.Aspect"/>.
        /// </summary>
        public float PanelAspect;

        /// <summary>
        /// See <see cref="PanelGeometry.Cylinder"/>.
        /// </summary>
        public bool PanelCylinder;
    }
}
