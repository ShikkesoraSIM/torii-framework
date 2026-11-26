// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Tells which controllers have been left alone for a while (e.g. the one not used for playing, put down nearby), so they can be hidden.
    /// </summary>
    /// <remarks>
    /// A controller counts as used when it moves or turns noticeably, or any of its buttons, trigger, grip or thumbstick is touched. The
    /// controller driving the pointer is never idle, so the one in use doesn't disappear while held still.
    /// </remarks>
    public sealed class ControllerIdleTracker
    {
        /// <summary>
        /// Moving further than this (in metres) from where it last settled counts as using a controller.
        /// </summary>
        private const float move_threshold = 0.03f;

        /// <summary>
        /// Turning further than this (in radians) from how it last settled counts as using a controller.
        /// </summary>
        private const float turn_threshold = 15 * MathF.PI / 180;

        private const float analog_threshold = 0.1f;
        private const float thumbstick_threshold = 0.2f;

        /// <summary>
        /// How long a controller must be left alone before it's idle, in seconds. 0 or less: never.
        /// </summary>
        public double HideDelay { get; set; } = 5;

        private readonly Vector3[] anchorPosition = new Vector3[2];
        private readonly Quaternion[] anchorOrientation = new Quaternion[2];
        private readonly bool[] anchored = new bool[2];
        private readonly double[] lastUsed = new double[2];
        private readonly bool[] idle = new bool[2];

        /// <summary>
        /// Whether a controller has been left alone for <see cref="HideDelay"/>, as of the latest <see cref="Update"/>.
        /// </summary>
        public bool IsIdle(int hand) => idle[hand];

        /// <summary>
        /// Whether each controller is idle (see <see cref="IsIdle"/>), indexed by hand.
        /// </summary>
        public ReadOnlySpan<bool> Idle => idle;

        /// <param name="hands">Controller states sampled for this frame.</param>
        /// <param name="activeHand">The controller driving the pointer.</param>
        /// <param name="time">The current time, in seconds.</param>
        public void Update(ReadOnlySpan<XrHandState> hands, int activeHand, double time)
        {
            for (int hand = 0; hand < 2 && hand < hands.Length; hand++)
            {
                ref readonly var state = ref hands[hand];

                bool touched = state.Trigger > analog_threshold || state.Squeeze > analog_threshold || state.Thumbstick.Length() > thumbstick_threshold
                               || state.PrimaryButton || state.SecondaryButton || state.MenuButton;

                Update(hand, state.GripValid, state.GripPosition, state.GripOrientation, touched, activeHand, time);
            }
        }

        /// <summary>
        /// Updates one controller, e.g. from a recording rather than live tracking.
        /// </summary>
        /// <param name="hand"><see cref="OpenXRInput.LEFT"/> or <see cref="OpenXRInput.RIGHT"/>.</param>
        /// <param name="tracked">Whether its grip pose is known.</param>
        /// <param name="gripPosition">Where it's held.</param>
        /// <param name="gripOrientation">How it's held.</param>
        /// <param name="touched">Whether any of its buttons, trigger, grip or thumbstick is in use.</param>
        /// <param name="activeHand">The controller driving the pointer.</param>
        /// <param name="time">The current time, in seconds.</param>
        public void Update(int hand, bool tracked, Vector3 gripPosition, Quaternion gripOrientation, bool touched, int activeHand, double time)
        {
            if (!tracked)
            {
                // Coming back into tracking counts as being picked up.
                anchored[hand] = false;
                idle[hand] = false;
                return;
            }

            // The controller in use counts as used for as long as it is, so it isn't hidden right as another takes over.
            if (!anchored[hand] || hand == activeHand || touched || moved(hand, gripPosition, gripOrientation))
            {
                anchorPosition[hand] = gripPosition;
                anchorOrientation[hand] = gripOrientation;
                anchored[hand] = true;
                lastUsed[hand] = time;
            }

            idle[hand] = HideDelay > 0 && hand != activeHand && time - lastUsed[hand] >= HideDelay;
        }

        /// <summary>
        /// Whether a trigger or grip pulled this far (0 to 1) counts as using a controller.
        /// </summary>
        public static bool IsPulled(float value) => value > analog_threshold;

        private bool moved(int hand, Vector3 position, Quaternion orientation)
        {
            if (Vector3.Distance(position, anchorPosition[hand]) > move_threshold)
                return true;

            float dot = MathF.Min(1, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(orientation), Quaternion.Normalize(anchorOrientation[hand]))));
            return 2 * MathF.Acos(dot) > turn_threshold;
        }
    }
}
