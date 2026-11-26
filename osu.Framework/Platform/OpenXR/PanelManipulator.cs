// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Numerics;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// What <see cref="PanelManipulator"/> manipulates: the panel's placement and shape.
    /// </summary>
    internal interface IPanelHost
    {
        /// <summary>
        /// The panel's current placement and shape, in the base space. Up to date after every change made through this interface.
        /// </summary>
        PanelGeometry Panel { get; }

        /// <summary>
        /// Places the panel: its centre of curvature and orientation (see <see cref="PanelGeometry.Centre"/> and
        /// <see cref="PanelGeometry.Orientation"/>), in the base space.
        /// </summary>
        void SetPanelPose(Vector3 centre, Quaternion orientation);

        void SetPanelShape(float radius, float horizontalAngle);
    }

    /// <summary>
    /// Lets the user grab the game panel with a controller's grip as if it were skewered on the end of the controller's ray: the point of the
    /// panel the ray hit when grabbing stays on the ray, at the same distance from the controller, and the panel moves and turns rigidly with
    /// the controller. The thumbstick pushes it away or pulls it closer along the ray, and grabbing near a corner resizes it instead.
    /// </summary>
    internal sealed class PanelManipulator
    {
        private const float grip_press = 0.6f;
        private const float grip_release = 0.4f;

        /// <summary>
        /// How close (in panel coordinates, 0..1) to both a horizontal and a vertical edge a grab must be to resize rather than move.
        /// </summary>
        private const float corner_size = 0.1f;

        /// <summary>
        /// How far outside the panel (in panel coordinates) a grab still catches it, so corners are easy to hit.
        /// </summary>
        private const float grab_tolerance = 0.06f;

        private const float min_angle = 30 * MathF.PI / 180;
        private const float max_angle = 140 * MathF.PI / 180;
        private const float min_distance = 0.5f;
        private const float max_distance = 4f;

        /// <summary>
        /// Metres per second of push/pull at full thumbstick deflection.
        /// </summary>
        private const float distance_speed = 1.2f;

        /// <summary>
        /// Invoked when a grab that changed the panel's placement, size or distance ends (e.g. to persist them).
        /// </summary>
        public event Action? LayoutChanged;

        /// <summary>
        /// The controller grabbing the panel, or -1.
        /// </summary>
        public int GrabbingHand { get; private set; } = -1;

        private enum Mode
        {
            Move,
            Resize,
        }

        private readonly bool[] gripDown = new bool[2];
        private readonly bool[] pressed = new bool[2];
        private Mode mode;
        private long lastTimestamp;

        // At the start of the grab.
        private Quaternion startOrientation;
        private float startRadius, startAngle;
        private Vector3 startPanelCentre;

        // Moving: the point of the panel that was grabbed (panel coordinates), how far along the ray it's held, and the panel's orientation
        // relative to the controller's.
        private Vector2 grabUv;
        private float grabDistance;
        private Quaternion grabOrientation;

        // Resizing: the panel as it was when grabbed, and how far the grabbed point was from its centre (metres along the surface).
        private readonly PanelGeometry startPanel = new PanelGeometry();
        private float startCornerDistance;

        public void Update(XrHandState[] hands, IPanelHost host) => Update(hands, host, Stopwatch.GetTimestamp());

        /// <param name="hands">The controllers.</param>
        /// <param name="host">The panel's owner.</param>
        /// <param name="timestamp">The current time, as a <see cref="Stopwatch"/> timestamp.</param>
        public void Update(XrHandState[] hands, IPanelHost host, long timestamp)
        {
            float dt = lastTimestamp == 0 ? 0 : (float)((timestamp - lastTimestamp) / (double)Stopwatch.Frequency);
            lastTimestamp = timestamp;

            for (int hand = 0; hand < 2; hand++)
            {
                bool wasDown = gripDown[hand];
                gripDown[hand] = wasDown ? hands[hand].Squeeze > grip_release : hands[hand].Squeeze >= grip_press;
                pressed[hand] = gripDown[hand] && !wasDown;
            }

            if (GrabbingHand >= 0)
            {
                ref readonly var state = ref hands[GrabbingHand];

                if (!gripDown[GrabbingHand] || !state.AimValid)
                {
                    endGrab(host);
                    return;
                }

                drag(state, host, dt);
                return;
            }

            for (int hand = 0; hand < 2; hand++)
            {
                if (pressed[hand] && hands[hand].AimValid && tryStartGrab(hand, hands[hand], host))
                    return;
            }
        }

        private bool tryStartGrab(int hand, in XrHandState state, IPanelHost host)
        {
            var panel = host.Panel;

            if (!panel.Intersect(state.AimPosition, state.AimDirection, out Vector3 hit, out Vector2 uv) || !PanelGeometry.IsWithin(uv, grab_tolerance))
                return false;

            bool nearVerticalEdge = uv.X < corner_size || uv.X > 1 - corner_size;
            bool nearHorizontalEdge = uv.Y < corner_size || uv.Y > 1 - corner_size;

            mode = nearVerticalEdge && nearHorizontalEdge ? Mode.Resize : Mode.Move;
            GrabbingHand = hand;

            startOrientation = panel.Orientation;
            startRadius = panel.Radius;
            startAngle = panel.HorizontalAngle;
            startPanelCentre = panel.Centre;

            grabUv = uv;
            grabDistance = Vector3.Distance(hit, state.AimPosition);
            grabOrientation = Quaternion.Normalize(Quaternion.Inverse(rayOrientation(state)) * panel.Orientation);

            startPanel.CopyFrom(panel);
            startCornerDistance = MathF.Max(surfaceDistanceFromCentre(startPanel, uv), 0.01f);

            return true;
        }

        private void drag(in XrHandState state, IPanelHost host, float dt)
        {
            var panel = host.Panel;

            switch (mode)
            {
                case Mode.Move:
                {
                    // Push away / pull closer with the grabbing hand's thumbstick: the panel is scaled around the controller, so it slides
                    // along the ray keeping its apparent size (and the grabbed point stays on the ray).
                    float radius = panel.Radius;
                    float stick = state.Thumbstick.Y;

                    if (MathF.Abs(stick) > 0.2f)
                    {
                        radius = Math.Clamp(radius + stick * distance_speed * dt, min_distance, max_distance);
                        grabDistance *= radius / panel.Radius;
                    }

                    if (radius != panel.Radius)
                        host.SetPanelShape(radius, panel.HorizontalAngle);

                    // The panel is held rigidly by the ray: it turns with the controller, and the grabbed point stays where the ray holds it.
                    var orientation = Quaternion.Normalize(rayOrientation(state) * grabOrientation);
                    var grabbedPoint = state.AimPosition + state.AimDirection * grabDistance;

                    host.SetPanelPose(grabbedPoint - Vector3.Transform(host.Panel.SurfacePointLocal(grabUv), orientation), orientation);
                    break;
                }

                case Mode.Resize:
                {
                    // Scale with how far from the panel's centre the corner is dragged (the aspect ratio is fixed by the game's image), keeping the
                    // centre in place.
                    if (!startPanel.Intersect(state.AimPosition, state.AimDirection, out _, out Vector2 uv))
                        break;

                    float ratio = surfaceDistanceFromCentre(startPanel, uv) / startCornerDistance;
                    host.SetPanelShape(panel.Radius, Math.Clamp(startAngle * ratio, min_angle, max_angle));
                    break;
                }
            }
        }

        /// <summary>
        /// The controller's aim orientation, turned the little it takes to point along its (smoothed) ray, so that what it holds follows the
        /// ray exactly.
        /// </summary>
        private static Quaternion rayOrientation(in XrHandState state)
        {
            var orientation = Quaternion.Normalize(state.AimOrientation);
            var raw = Vector3.Transform(-Vector3.UnitZ, orientation);
            var axis = Vector3.Cross(raw, state.AimDirection);
            float w = 1 + Vector3.Dot(raw, state.AimDirection);

            // Already along the ray (or, never in practice, pointing the opposite way).
            if (axis.LengthSquared() < 1e-12f || w < 1e-6f)
                return orientation;

            return Quaternion.Normalize(Quaternion.Normalize(new Quaternion(axis, w)) * orientation);
        }

        /// <summary>
        /// The distance from the panel's centre to <paramref name="uv"/>, in metres along its surface.
        /// </summary>
        private static float surfaceDistanceFromCentre(PanelGeometry panel, Vector2 uv)
        {
            float width = panel.Cylinder ? panel.Radius * panel.HorizontalAngle : panel.FlatWidth;
            return ((uv - new Vector2(0.5f)) * new Vector2(width, panel.Height)).Length();
        }

        private void endGrab(IPanelHost host)
        {
            GrabbingHand = -1;

            var panel = host.Panel;

            if (MathF.Abs(panel.HorizontalAngle - startAngle) > 1e-4f
                || MathF.Abs(panel.Radius - startRadius) > 1e-4f
                || Vector3.Distance(panel.Centre, startPanelCentre) > 1e-4f
                || MathF.Abs(Quaternion.Dot(panel.Orientation, startOrientation)) < 1 - 1e-6f)
                LayoutChanged?.Invoke();
        }
    }
}
