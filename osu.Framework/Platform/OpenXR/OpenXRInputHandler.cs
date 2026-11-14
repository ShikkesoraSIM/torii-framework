// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osuTK.Input;
using NVector2 = System.Numerics.Vector2;
using Vector2 = osuTK.Vector2;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Turns tracked controllers into pointer input on the virtual panel.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Each controller casts a ray at the panel. The pointer follows the "active" controller without any button held (hover).</item>
    /// <item>Pulling a trigger makes that controller active and presses the left mouse button, until the same trigger is released.</item>
    /// <item>Thumbsticks scroll. B / Y / Menu act as Escape (back / pause). A / X act as Space.</item>
    /// </list>
    /// </remarks>
    public class OpenXRInputHandler : InputHandler
    {
        public override bool IsActive => true;

        /// <summary>
        /// Trigger value at which a press registers.
        /// </summary>
        public float TriggerPressThreshold { get; set; } = 0.5f;

        /// <summary>
        /// Trigger value below which a press is released (hysteresis, to avoid chatter around the press threshold).
        /// </summary>
        public float TriggerReleaseThreshold { get; set; } = 0.35f;

        /// <summary>
        /// How far outside the panel (as a fraction of its size) a ray still counts as pointing at its edge.
        /// </summary>
        public float EdgeTolerance { get; set; } = 0.05f;

        private const float thumbstick_deadzone = 0.2f;

        /// <summary>
        /// Scroll speed in precise scroll units per second, at full thumbstick deflection.
        /// </summary>
        private const float scroll_speed = 15;

        private const int left = OpenXRInput.LEFT;
        private const int right = OpenXRInput.RIGHT;

        private readonly bool[] triggerDown = new bool[2];

        private int activeHand = right;

        /// <summary>
        /// The controller currently driving the pointer (<see cref="OpenXRInput.LEFT"/> or <see cref="OpenXRInput.RIGHT"/>).
        /// </summary>
        public int ActiveHand => activeHand;

        /// <summary>
        /// When set, triggers no longer press the mouse button (the pointer still follows the controller that pressed last).
        /// Lets a consumer map each trigger itself, e.g. to separate gameplay keys, via <see cref="IsTriggerDown"/>.
        /// </summary>
        /// <remarks>
        /// This is a request which lapses: it must be set again on every update it should keep applying, and stops applying shortly
        /// after the last time it was. That way a consumer which stops updating (e.g. a suspended screen) can never leave the triggers
        /// disabled. (Updates and controller input aren't in step when the host runs multi-threaded, so it doesn't lapse with either.)
        /// </remarks>
        public bool SuppressPointerButtons
        {
            get
            {
                long requested = Interlocked.Read(ref suppressRequestTimestamp);
                return requested != 0 && Stopwatch.GetElapsedTime(requested).TotalMilliseconds < suppress_request_lifetime;
            }
            set => Interlocked.Exchange(ref suppressRequestTimestamp, value ? Stopwatch.GetTimestamp() : 0);
        }

        /// <summary>
        /// How long a <see cref="SuppressPointerButtons"/> request lasts, in milliseconds: several frames of either kind.
        /// </summary>
        private const double suppress_request_lifetime = 100;

        private long suppressRequestTimestamp;

        /// <summary>
        /// Whether the latest controller frame produced any input for the game.
        /// </summary>
        internal bool HadInput { get; private set; }

        /// <summary>
        /// How far the pointer ray is tilted down from the controller's aim direction, in radians (negative tilts it up).
        /// </summary>
        /// <remarks>
        /// Together with <see cref="PointerOffset"/>, places the ray where something held in the hand points, e.g. out of the tip of a pen
        /// drawn in place of the controller (see <see cref="OpenXRHostController.HeldModel"/>).
        /// </remarks>
        public float PointerTilt { get; set; }

        /// <summary>
        /// Where the pointer ray starts relative to the controller's aim pose, in metres, after <see cref="PointerTilt"/>: -Z is along the ray,
        /// +Y is up.
        /// </summary>
        public System.Numerics.Vector3 PointerOffset { get; set; }

        /// <summary>
        /// A controller whose thumbstick shouldn't scroll (e.g. it's grabbing the panel and uses the thumbstick to push/pull it), or -1.
        /// </summary>
        internal int ScrollExcludedHand { get; set; } = -1;

        /// <summary>
        /// Whether a controller's trigger is held (with hysteresis), as of the latest frame.
        /// </summary>
        public bool IsTriggerDown(int hand) => triggerDown[hand];

        private readonly HapticPulse?[] pendingHaptics = new HapticPulse?[2];

        /// <summary>
        /// Vibrates a controller, starting with the next frame. A later request in the same frame replaces an earlier, weaker one;
        /// a new pulse replaces one still playing.
        /// </summary>
        /// <param name="hand"><see cref="OpenXRInput.LEFT"/> or <see cref="OpenXRInput.RIGHT"/>.</param>
        /// <param name="amplitude">Strength, 0 to 1.</param>
        /// <param name="duration">Duration in milliseconds. The runtime may clamp very short pulses to its minimum.</param>
        /// <param name="frequency">Frequency in Hz, or 0 for the runtime's default.</param>
        public void Vibrate(int hand, float amplitude, double duration, float frequency = 0)
        {
            if (hand is < 0 or > 1 || amplitude <= 0)
                return;

            var pulse = new HapticPulse(Math.Clamp(amplitude, 0, 1), duration, frequency);

            lock (pendingHaptics)
            {
                if (pendingHaptics[hand] is not { } existing || existing.Amplitude <= pulse.Amplitude)
                    pendingHaptics[hand] = pulse;
            }
        }

        internal HapticPulse? TakeHaptic(int hand)
        {
            lock (pendingHaptics)
            {
                var pulse = pendingHaptics[hand];
                pendingHaptics[hand] = null;
                return pulse;
            }
        }

        /// <summary>
        /// Movement below this (in metres) isn't counted until it adds up, so tracking jitter of a controller at rest doesn't accumulate.
        /// </summary>
        private const float movement_threshold = 0.002f;

        /// <summary>
        /// A larger jump than this (in metres) between two frames is tracking being re-acquired or the space being recentred, not movement.
        /// </summary>
        private const float movement_jump_limit = 0.5f;

        private readonly System.Numerics.Vector3?[] movementAnchor = new System.Numerics.Vector3?[2];
        private readonly double[] distanceMoved = new double[2];

        /// <summary>
        /// How far a controller has physically moved while tracked since startup, in metres. Only ever increases.
        /// </summary>
        /// <param name="hand"><see cref="OpenXRInput.LEFT"/> or <see cref="OpenXRInput.RIGHT"/>.</param>
        public double GetDistanceMoved(int hand) => Volatile.Read(ref distanceMoved[hand]);

        private void updateDistanceMoved(int hand, in XrHandState state)
        {
            if (!state.GripValid)
            {
                movementAnchor[hand] = null;
                return;
            }

            if (movementAnchor[hand] is not { } anchor)
            {
                movementAnchor[hand] = state.GripPosition;
                return;
            }

            float distance = System.Numerics.Vector3.Distance(anchor, state.GripPosition);

            if (distance < movement_threshold)
                return;

            if (distance <= movement_jump_limit)
                Volatile.Write(ref distanceMoved[hand], distanceMoved[hand] + distance);

            movementAnchor[hand] = state.GripPosition;
        }
        private bool mouseDown;
        private bool escapeDown;
        private bool spaceDown;
        private Vector2? lastPosition;

        private long lastTimestamp;

        public override bool Initialize(GameHost host) => base.Initialize(host);

        internal void Process(XrHandState[] hands, OpenXRSession panel)
        {
            long now = Stopwatch.GetTimestamp();
            float dt = lastTimestamp == 0 ? 0 : (float)Stopwatch.GetElapsedTime(lastTimestamp, now).TotalSeconds;
            lastTimestamp = now;

            updateDistanceMoved(left, hands[left]);
            updateDistanceMoved(right, hands[right]);

            if (!Enabled.Value)
                return;

            Vector2? leftHit = hands[left].AimValid ? raycast(hands[left], panel) : null;
            Vector2? rightHit = hands[right].AimValid ? raycast(hands[right], panel) : null;
            Vector2? hitFor(int hand) => hand == left ? leftHit : rightHit;

            bool suppressButtons = SuppressPointerButtons;
            int inputsBefore = PendingInputs.Count;

            bool leftPressed = updateTrigger(left, hands[left].Trigger);
            bool rightPressed = updateTrigger(right, hands[right].Trigger);

            if (!mouseDown)
            {
                // A new press takes over the pointer; otherwise fall back to whichever controller is actually pointing at the panel.
                if (rightPressed && rightHit != null)
                    activeHand = right;
                else if (leftPressed && leftHit != null)
                    activeHand = left;
                else if (hitFor(activeHand) == null && hitFor(1 - activeHand) != null)
                    activeHand = 1 - activeHand;
            }

            // Position must be applied before a press so the click lands where the user is pointing.
            if (hitFor(activeHand) is Vector2 position && position != lastPosition)
            {
                PendingInputs.Enqueue(new MousePositionAbsoluteInput { Position = position });
                lastPosition = position;
            }

            bool wantMouseDown = !suppressButtons
                                 && (mouseDown ? triggerDown[activeHand] : triggerDown[activeHand] && hitFor(activeHand) != null);

            if (wantMouseDown != mouseDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, wantMouseDown));
                mouseDown = wantMouseDown;
            }

            processScroll(hands, dt);

            bool wantEscape = hands[left].SecondaryButton || hands[right].SecondaryButton || hands[left].MenuButton;

            if (wantEscape != escapeDown)
            {
                PendingInputs.Enqueue(new KeyboardKeyInput(Key.Escape, wantEscape));
                escapeDown = wantEscape;
            }

            // A / X act as Space (e.g. skipping intros during gameplay, where triggers are gameplay keys).
            bool wantSpace = hands[left].PrimaryButton || hands[right].PrimaryButton;

            if (wantSpace != spaceDown)
            {
                PendingInputs.Enqueue(new KeyboardKeyInput(Key.Space, wantSpace));
                spaceDown = wantSpace;
            }

            // (The count only ever falls in between, by the update thread collecting the inputs.)
            HadInput = PendingInputs.Count > inputsBefore || triggerChanged;
            triggerChanged = false;
        }

        // Triggers taken over by the game (see SuppressPointerButtons) are input too, though they produce none here.
        private bool triggerChanged;

        /// <summary>
        /// Updates the pressed state of a trigger with hysteresis.
        /// </summary>
        /// <returns>Whether the trigger was newly pressed.</returns>
        private bool updateTrigger(int hand, float value)
        {
            bool wasDown = triggerDown[hand];
            triggerDown[hand] = wasDown ? value > TriggerReleaseThreshold : value >= TriggerPressThreshold;
            triggerChanged |= triggerDown[hand] != wasDown;
            return triggerDown[hand] && !wasDown;
        }

        private void processScroll(XrHandState[] hands, float dt)
        {
            // Use whichever stick is deflected more, so either hand can scroll.
            NVector2 leftStick = ScrollExcludedHand == left ? NVector2.Zero : hands[left].Thumbstick;
            NVector2 rightStick = ScrollExcludedHand == right ? NVector2.Zero : hands[right].Thumbstick;
            NVector2 stick = leftStick.LengthSquared() > rightStick.LengthSquared() ? leftStick : rightStick;

            if (stick.Length() < thumbstick_deadzone || dt <= 0)
                return;

            // Rescale past the deadzone so scrolling starts gently.
            NVector2 scaled = stick * ((stick.Length() - thumbstick_deadzone) / (1 - thumbstick_deadzone) / stick.Length());

            PendingInputs.Enqueue(new MouseScrollRelativeInput
            {
                Delta = new Vector2(-scaled.X, scaled.Y) * scroll_speed * dt,
                IsPrecise = true,
            });
        }

        /// <summary>
        /// Intersects a controller's aim ray with the panel.
        /// </summary>
        /// <returns>The hit position in panel pixels, or null if the ray doesn't point at the panel.</returns>
        private Vector2? raycast(in XrHandState hand, OpenXRSession panel)
        {
            if (!panel.Panel.Intersect(hand.AimPosition, hand.AimDirection, out _, out NVector2 uv) || !PanelGeometry.IsWithin(uv, EdgeTolerance))
                return null;

            return new Vector2(Math.Clamp(uv.X, 0, 1) * panel.PanelSize.Width, Math.Clamp(uv.Y, 0, 1) * panel.PanelSize.Height);
        }
    }
}
