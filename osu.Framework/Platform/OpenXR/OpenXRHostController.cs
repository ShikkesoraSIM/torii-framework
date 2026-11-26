// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using osu.Framework.Bindables;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using osuTK.Graphics;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// A <see cref="GameHost"/> that presents through OpenXR.
    /// </summary>
    public interface IOpenXRHost
    {
        OpenXRHostController OpenXR { get; }
    }

    /// <summary>
    /// The platform-independent part of an immersive OpenXR <see cref="GameHost"/>: the frame loop, layers, panel and input.
    /// </summary>
    /// <remarks>
    /// The game renders as usual, but into a virtual 2D panel (an OpenXR swapchain image) instead of the window,
    /// and frames are paced and presented by the OpenXR runtime (xrWaitFrame / xrEndFrame) instead of a buffer swap.
    /// Requires the GL renderer. Everything here that talks to the runtime happens on the thread drawing the game.
    /// <list type="bullet">
    /// <item>
    /// In single-threaded execution the host calls <see cref="BeforeUpdate"/> ahead of each update: the frame is waited for there, so
    /// that the update consumes controller input sampled for the very frame it produces.
    /// </item>
    /// <item>
    /// In multi-threaded execution (see <see cref="ScheduleToUpdate"/>) the frame is waited for in <see cref="Draw"/>, pacing the draw
    /// thread alone: the update thread runs at its own rate, so input from other devices (mouse, tablet, keyboard) is processed far
    /// more often than the headset refreshes, and each frame shows the latest update.
    /// </item>
    /// </list>
    /// </remarks>
    public class OpenXRHostController : IDisposable
    {
        /// <summary>
        /// The resolution of the virtual panel the game renders into.
        /// </summary>
        public Size PanelSize { get; }

        private readonly IOpenXRPlatform platform;
        private readonly string? debugDirectory;

        private OpenXRSession? xr;

        /// <summary>
        /// Logs frame timing when the "debug-frame-timing" flag is present.
        /// </summary>
        private FrameTimer? frameTimer;

        private readonly BindableBool hasInputFocus = new BindableBool(true);
        private readonly BindableBool isDisplayed = new BindableBool(true);

        /// <summary>
        /// Whether the app has input focus. False while e.g. the system menu or keyboard overlay is shown, even though the app is still
        /// displayed (see <see cref="IsDisplayed"/>).
        /// Games should pause time-sensitive activity (gameplay) when this becomes false.
        /// </summary>
        public IBindable<bool> HasInputFocus => hasInputFocus;

        /// <summary>
        /// Whether the app is being displayed in the headset. Hosts should report this as the window being active: throttling while
        /// displayed (e.g. under the keyboard overlay) would make content judder.
        /// </summary>
        public IBindable<bool> IsDisplayed => isDisplayed;

        private readonly BindableBool supportsPassthrough = new BindableBool(true);

        /// <summary>
        /// Whether the runtime can show the room (<see cref="XrEnvironmentMode.Passthrough"/>). Assumed until the session has started.
        /// </summary>
        public IBindable<bool> SupportsPassthrough => supportsPassthrough;

        /// <summary>
        /// The name the runtime reports (e.g. "SteamVR/OpenXR"). Empty until the session has started.
        /// </summary>
        public string RuntimeName { get; private set; } = string.Empty;

        /// <summary>
        /// Invoked when the runtime asks the app to exit (e.g. quit from the system menu).
        /// </summary>
        public event Action? ExitRequested;

        /// <summary>
        /// Whether a desktop host exits when the runtime ends the session (see <see cref="ExitRequested"/>). A game which carries on
        /// outside VR instead (e.g. when the runtime is closed) turns this off and handles <see cref="ExitRequested"/> itself.
        /// </summary>
        public bool ExitWhenSessionEnds { get; set; } = true;

        /// <summary>
        /// Invoked (on the draw thread) when OpenXR failed, at startup or later (e.g. the runtime was closed without ending the session
        /// first): nothing is displayed in the headset from then on. See <see cref="Failed"/>.
        /// </summary>
        public event Action? SessionFailed;

        /// <summary>
        /// How many times in a row the session is thrown away and started again when OpenXR fails, before giving up (see
        /// <see cref="Failed"/>). For hosts with nothing else to show: a failure which isn't recovered from leaves the app running
        /// unseen. 0 (the default) gives up at the first failure. Set before running the host.
        /// </summary>
        public int SessionRecoveryAttempts { get; set; }

        /// <summary>
        /// The time between a failure and starting the session again, in milliseconds.
        /// </summary>
        private const double session_recovery_delay = 500;

        /// <summary>
        /// The number of frames a restarted session must display before the failure counts as recovered from.
        /// </summary>
        private const int session_recovery_frames = 600;

        private int recoveriesUsed;
        private int framesSinceRecovery;
        private bool recovering;

        /// <summary>
        /// Runs an action on the update thread. Set by a host running multi-threaded, before it runs: the session then lives on the draw
        /// thread, and what the game binds to (<see cref="HasInputFocus"/>, <see cref="IsDisplayed"/>, <see cref="SupportsPassthrough"/>)
        /// changes on the update thread still. The events are invoked on the draw thread regardless.
        /// </summary>
        public Action<Action>? ScheduleToUpdate { get; set; }

        private void onUpdateThread(Action action)
        {
            if (ScheduleToUpdate is { } schedule)
                schedule(action);
            else
                action();
        }

        private long updateFrames;

        /// <summary>
        /// Call after each of the host's update frames when running multi-threaded (see <see cref="ScheduleToUpdate"/>).
        /// </summary>
        public void AfterUpdate() => Interlocked.Increment(ref updateFrames);

        /// <summary>
        /// The longest the draw thread waits for the update thread to process controller input, in milliseconds.
        /// </summary>
        private const double controller_input_wait = 2.5;

        /// <summary>
        /// Multi-threaded: gives the update thread a moment to process the controller input just sampled, so that it's in the frame
        /// about to be drawn rather than the next (the pointer would trail the controller by another frame).
        /// </summary>
        private void waitForUpdateThread()
        {
            // An update frame in progress may have collected its input already: the one after it has for sure.
            long target = Interlocked.Read(ref updateFrames) + 2;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            while (Interlocked.Read(ref updateFrames) < target
                   && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < controller_input_wait)
                Thread.SpinWait(20);
        }

        /// <summary>
        /// When set, a headset which isn't there yet (e.g. a wireless headset not connected to the runtime) is waited for rather than
        /// being a failure: starting the session is attempted again every couple of seconds, for as long as this returns true (called
        /// on the draw thread before each further attempt, e.g. checking that the runtime is still running). Once it returns false,
        /// OpenXR fails as it would have. Set before running the host.
        /// </summary>
        public Func<bool>? WaitForHeadset { get; set; }

        /// <summary>
        /// Whether the session hasn't started because there's no headset yet (see <see cref="WaitForHeadset"/>). Nothing is displayed in
        /// the meantime, other than the desktop view. Changes on the draw thread.
        /// </summary>
        public bool WaitingForHeadset { get; private set; }

        /// <summary>
        /// The time between two attempts at starting the session while waiting for the headset, in milliseconds.
        /// </summary>
        private const double headset_wait_interval = 2000;

        private long lastSessionAttemptTimestamp;
        private Exception? headsetWaitException;

        /// <summary>
        /// The name of the external render target shown in the host's desktop window, if it has one (see
        /// <see cref="PresentsDesktopView"/>): what's redirected into it is drawn to the window rather than into the headset, every few
        /// frames. The target is the size of the panel; it's scaled to fit the window.
        /// </summary>
        public const string DESKTOP_VIEW = "desktop";

        /// <summary>
        /// Whether the host has a desktop window to show the <see cref="DESKTOP_VIEW"/> target in. Set by the host before running.
        /// Otherwise nothing redirected into that target is drawn at all.
        /// </summary>
        public bool PresentsDesktopView { get; set; }

        /// <summary>
        /// The least time between two frames of the desktop view, in milliseconds: it only shows status, and mustn't take time
        /// from the headset's frames.
        /// </summary>
        private const double desktop_view_interval = 1000.0 / 30;

        private readonly ExternalRenderTarget desktopView = new ExternalRenderTarget(DESKTOP_VIEW) { Hidden = true };
        private OffscreenTarget? desktopViewImage;

        // Stands in for the panel while nothing is drawn for the headset (e.g. it's idle), so the desktop view is still drawn.
        private OffscreenTarget? idlePanelImage;
        private long lastDesktopViewTimestamp;

        private readonly OpenXRInputHandler xrInput = new OpenXRInputHandler();
        private readonly PanelManipulator panelManipulator = new PanelManipulator();

        /// <summary>
        /// Controller input, for consumers that need per-controller state (e.g. mapping each trigger to its own gameplay key).
        /// Hosts add it to their input handlers.
        /// </summary>
        public OpenXRInputHandler ControllerInput => xrInput;

        private readonly object trackingLock = new object();
        private XrTrackingState trackingState;

        /// <summary>
        /// Where the head, the controllers and the panel were for the latest frame, e.g. to record them. Can be called from any thread.
        /// </summary>
        /// <remarks>
        /// Poses are those predicted for the time the frame is displayed, as used for the pointer and for drawing the controllers.
        /// </remarks>
        public XrTrackingState GetTrackingState()
        {
            lock (trackingLock)
                return trackingState;
        }

        private void publishTrackingState(OpenXRSession xr)
        {
            lock (trackingLock)
            {
                trackingState.Frame++;
                trackingState.HeadValid = xr.HeadTracked;
                trackingState.HeadPosition = xr.HeadPosition;
                trackingState.HeadOrientation = xr.HeadOrientation;
                trackingState.LeftHand = xr.Input!.Hands[OpenXRInput.LEFT];
                trackingState.RightHand = xr.Input.Hands[OpenXRInput.RIGHT];
                trackingState.ActiveHand = xrInput.ActiveHand;
                trackingState.AnchorPosition = xr.AnchorPosition;
                trackingState.AnchorOrientation = xr.AnchorOrientation;
                trackingState.PanelCentre = xr.Panel.Centre;
                trackingState.PanelOrientation = xr.Panel.Orientation;
                trackingState.PanelRadius = xr.Panel.Radius;
                trackingState.PanelHorizontalAngle = xr.Panel.HorizontalAngle;
                trackingState.PanelAspect = xr.Panel.Aspect;
                trackingState.PanelCylinder = xr.Panel.Cylinder;
            }
        }

        // The panel's placement is set by the game (update thread) and changed by the user grabbing the panel (on the thread running
        // the session): the session's own copy is only touched by the latter, and exchanged with this one once per frame.
        private readonly object panelLock = new object();
        private float panelDistance = 1.5f;
        private float panelAngle = 60 * MathF.PI / 180;
        private System.Numerics.Vector3 panelOffset;
        private System.Numerics.Quaternion panelRotation = OpenXRSession.DefaultPanelRotation;
        private bool panelPlacementSet;

        /// <summary>
        /// Distance of the game panel from its centre of curvature (the viewer, unless the panel was moved), in metres. Also changed by the
        /// user pushing/pulling the grabbed panel.
        /// </summary>
        public float PanelDistance
        {
            get
            {
                lock (panelLock) return panelDistance;
            }
            set
            {
                lock (panelLock)
                {
                    panelDistance = value;
                    panelPlacementSet = true;
                }
            }
        }

        /// <summary>
        /// Horizontal angle covered by the game panel, in radians. Also changed by the user resizing the grabbed panel.
        /// </summary>
        public float PanelHorizontalAngle
        {
            get
            {
                lock (panelLock) return panelAngle;
            }
            set
            {
                lock (panelLock)
                {
                    panelAngle = value;
                    panelPlacementSet = true;
                }
            }
        }

        /// <summary>
        /// Where the panel's centre of curvature is relative to where the viewer was facing when last recentered (in that frame: +X right,
        /// +Y up, -Z forward; metres). Zero by default: at the viewer. Also changed by the user moving the grabbed panel.
        /// </summary>
        public System.Numerics.Vector3 PanelOffset
        {
            get
            {
                lock (panelLock) return panelOffset;
            }
            set
            {
                lock (panelLock)
                {
                    panelOffset = value;
                    panelPlacementSet = true;
                }
            }
        }

        /// <summary>
        /// The panel's orientation relative to where the viewer was facing when last recentered (see <see cref="PanelOffset"/>).
        /// By default straight ahead, slightly below the gaze. Also changed by the user turning the grabbed panel.
        /// </summary>
        public System.Numerics.Quaternion PanelRotation
        {
            get
            {
                lock (panelLock) return panelRotation;
            }
            set
            {
                lock (panelLock)
                {
                    panelRotation = value;
                    panelPlacementSet = true;
                }
            }
        }

        /// <summary>
        /// Puts the panel back straight ahead of where the viewer faced when last recentered (keeping its size and distance).
        /// </summary>
        public void ResetPanelPlacement()
        {
            lock (panelLock)
            {
                panelOffset = System.Numerics.Vector3.Zero;
                panelRotation = OpenXRSession.DefaultPanelRotation;
                panelPlacementSet = true;
            }
        }

        /// <summary>
        /// Gives the session a placement set since the last frame. Call before anything uses the session's placement.
        /// </summary>
        private void applyPanelPlacement(OpenXRSession session)
        {
            lock (panelLock)
            {
                if (!panelPlacementSet)
                    return;

                session.PanelDistance = panelDistance;
                session.PanelHorizontalAngle = panelAngle;
                session.PanelOffset = panelOffset;
                session.PanelRotation = panelRotation;
                panelPlacementSet = false;
            }
        }

        /// <summary>
        /// Takes over the session's placement, which the user may have changed by grabbing the panel.
        /// </summary>
        private void readPanelPlacement(OpenXRSession session)
        {
            lock (panelLock)
            {
                // Set again in the meantime: that wins, next frame.
                if (panelPlacementSet)
                    return;

                panelDistance = session.PanelDistance;
                panelAngle = session.PanelHorizontalAngle;
                panelOffset = session.PanelOffset;
                panelRotation = session.PanelRotation;
            }
        }

        /// <summary>
        /// Invoked when the user finishes moving, turning, resizing or pushing/pulling the panel, so the new <see cref="PanelDistance"/>,
        /// <see cref="PanelHorizontalAngle"/>, <see cref="PanelOffset"/> and <see cref="PanelRotation"/> can be persisted.
        /// </summary>
        /// <remarks>
        /// Also invoked when the user recentres through the system: the panel goes back straight ahead (see
        /// <see cref="ResetPanelPlacement"/>), so that a panel left somewhere out of sight can always be brought back.
        /// </remarks>
        public event Action? PanelLayoutChanged
        {
            add
            {
                panelManipulator.LayoutChanged += value;
                panelPlacementReset += value;
            }
            remove
            {
                panelManipulator.LayoutChanged -= value;
                panelPlacementReset -= value;
            }
        }

        private event Action? panelPlacementReset;

        // Set when the user recentred through the system, until the panel's placement has been reset for it.
        private bool systemRecentered;

        private XrEnvironmentMode environment;

        /// <summary>
        /// What surrounds the game panel. May be set before the session starts.
        /// </summary>
        public XrEnvironmentMode Environment
        {
            get => environment;
            set
            {
                environment = value;
                if (xr != null) xr.Environment = value;
            }
        }

        private float passthroughOpacity = 1;

        /// <summary>
        /// How strongly the room shows in <see cref="XrEnvironmentMode.Passthrough"/>: 1 = full, lower fades towards black.
        /// </summary>
        public float PassthroughOpacity
        {
            get => passthroughOpacity;
            set
            {
                passthroughOpacity = value;
                if (xr != null) xr.PassthroughOpacity = value;
            }
        }

        private bool suppressBoundaryInPassthrough;

        /// <inheritdoc cref="OpenXRSession.SuppressBoundaryInPassthrough"/>
        public bool SuppressBoundaryInPassthrough
        {
            get => suppressBoundaryInPassthrough;
            set
            {
                suppressBoundaryInPassthrough = value;
                if (xr != null) xr.SuppressBoundaryInPassthrough = value;
            }
        }

        private bool showControllers = true;

        /// <summary>
        /// Whether models of the controllers are shown where they're held.
        /// </summary>
        public bool ShowControllers
        {
            get => showControllers;
            set
            {
                showControllers = value;
                if (xr != null) xr.ShowControllers = value;
            }
        }

        private double idleControllerHideDelay = 5;

        /// <summary>
        /// How long a controller not driving the pointer must be left alone (not moved, turned or touched) before it's hidden, in seconds:
        /// its model, beam and reticle. It shows again as soon as it's used. 0 or less: never hidden.
        /// </summary>
        public double IdleControllerHideDelay
        {
            get => idleControllerHideDelay;
            set
            {
                idleControllerHideDelay = value;
                if (xr != null) xr.IdleControllerHideDelay = value;
            }
        }

        private byte[]? heldModel;

        /// <summary>
        /// A binary glTF (.glb) model drawn in both hands in place of the controllers, e.g. a pen, or null to show the controllers.
        /// </summary>
        /// <remarks>
        /// The model's origin is placed where the pointer ray starts, with its -Z axis along the ray and +Y up: use
        /// <see cref="OpenXRInputHandler.PointerTilt"/> and <see cref="OpenXRInputHandler.PointerOffset"/> to position it in the hand.
        /// </remarks>
        public byte[]? HeldModel
        {
            get => heldModel;
            set
            {
                heldModel = value;
                if (xr != null) xr.HeldModel = value;
            }
        }

        private (XrPlacedModel? left, XrPlacedModel? right) genericControllerModels;

        /// <summary>
        /// Models of a controller for the left and right hand, drawn where a controller is and nothing better is known of it: for the
        /// user's own controllers when the runtime has no model of them, and for <see cref="GhostHands"/>. Without them a plain
        /// built-in shape is drawn.
        /// </summary>
        public (XrPlacedModel? left, XrPlacedModel? right) GenericControllerModels
        {
            get => genericControllerModels;
            set
            {
                genericControllerModels = value;
                GhostHands.ControllerModels = value;
                if (xr != null) xr.GenericControllerModels = value;
            }
        }

        /// <summary>
        /// A second pair of controllers to draw where the game says, e.g. those of the player of a replay being watched.
        /// </summary>
        public OpenXRGhostHands GhostHands { get; } = new OpenXRGhostHands();

        private float flashReduction = 0.5f;

        /// <summary>
        /// Flash reduction strength for the custom field's wall, 0 (off) to 1 (strongest).
        /// </summary>
        public float FlashReduction
        {
            get => flashReduction;
            set
            {
                flashReduction = value;
                if (xr != null) xr.FlashReduction = value;
            }
        }

        /// <summary>
        /// Feeds music data to the custom field environment. Call once per update.
        /// </summary>
        /// <param name="bands">Per-band amplitudes (0..1), low to high frequencies.</param>
        /// <param name="beatPulse">1 on the beat, decaying to 0 before the next.</param>
        /// <param name="kiai">Whether kiai time is active.</param>
        public void SetEnvironmentAudio(ReadOnlySpan<float> bands, float beatPulse, bool kiai) => xr?.SetEnvironmentAudio(bands, beatPulse, kiai);

        /// <summary>
        /// Whether OpenXR failed, in which case nothing is displayed.
        /// </summary>
        public bool Failed { get; private set; }

        private bool presentedThisFrame;
        private int undrawnFrames;

        // Garbage allocated per frame by the XR loop itself (excluding the game's update and draw), logged periodically at debug level.
        private long xrAllocatedBytes;
        private long gameDrawAllocatedBytes;
        private int allocationFrames;

        private readonly Dictionary<string, ExternalRenderTarget> externalTargets = new Dictionary<string, ExternalRenderTarget>();

        /// <param name="platform">The platform's graphics binding.</param>
        /// <param name="panelSize">The resolution of the virtual panel the game renders into.</param>
        /// <param name="debugDirectory">
        /// A directory holding debug flag files (e.g. "debug-headlock-panel"), whose presence enables development aids. Null to disable.
        /// </param>
        public OpenXRHostController(IOpenXRPlatform platform, Size panelSize, string? debugDirectory = null)
        {
            this.platform = platform;
            this.debugDirectory = debugDirectory;
            PanelSize = panelSize;
        }

        /// <summary>
        /// See <see cref="GameHost.GetExternalRenderTarget"/>. Each target becomes its own OpenXR layer composited behind the panel
        /// (e.g. "background", placed far away).
        /// </summary>
        public ExternalRenderTarget GetExternalRenderTarget(string name)
        {
            if (name == DESKTOP_VIEW)
                return desktopView;

            lock (externalTargets)
            {
                if (!externalTargets.TryGetValue(name, out var target))
                    externalTargets[name] = target = new ExternalRenderTarget(name);

                return target;
            }
        }

        /// <summary>
        /// Single-threaded execution only: call at the start of the host's update frame, before the game updates.
        /// </summary>
        /// <remarks>
        /// In single-threaded execution the loop runs input, audio, update then draw. Waiting for the XR frame here (rather than in
        /// <see cref="Draw"/>) lets controller poses be sampled at the predicted display time of the frame this update produces,
        /// and consumed by this same update.
        /// </remarks>
        public void BeforeUpdate()
        {
            if (xr == null || Failed)
                return;

            // The session may have been stopped or started since the last frame (e.g. the headset was taken off): find out before
            // waiting for a frame of a session the runtime no longer runs.
            try
            {
                xr.PollEvents();
            }
            catch (Exception e)
            {
                fail(e);
                return;
            }

            if (xr is not { IsSessionRunning: true, FrameWaited: false })
                return;

            waitFrame();
        }

        /// <summary>
        /// Waits for the runtime to want the next frame, then samples the controllers for it and turns that into input.
        /// </summary>
        /// <returns>Whether controller input was processed.</returns>
        private bool waitFrame()
        {
            Debug.Assert(xr != null);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            bool processed = false;

            try
            {
                if (xr.Input != null)
                {
                    xr.Input.AimRotationOffset = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, -xrInput.PointerTilt);
                    xr.Input.AimPositionOffset = xrInput.PointerOffset;
                }

                bool resetPlacement = systemRecentered;

                if (resetPlacement)
                {
                    systemRecentered = false;
                    ResetPanelPlacement();
                    Logger.Log("OpenXR: recentred by the user, panel placement reset.");
                }

                applyPanelPlacement(xr);

                frameTimer?.Enter(FrameTimer.Phase.Wait);
                bool waited = xr.WaitFrame();
                frameTimer?.Enter(FrameTimer.Phase.Update);

                // In the head-locked debug mode the controller rays don't match the visible panel, so pointer input is disabled
                // (this also keeps injected touches from being overridden by idle controllers).
                if (waited && !xr.DebugHeadLockedPanel)
                {
                    // Grabbing comes first, so the pointer is computed against where the panel ends up this frame.
                    panelManipulator.Update(xr.Input!.Hands, xr);
                    xrInput.ScrollExcludedHand = panelManipulator.GrabbingHand;

                    xrInput.Process(xr.Input!.Hands, xr);
                    processed = true;

                    for (int hand = 0; hand < 2; hand++)
                    {
                        if (xrInput.TakeHaptic(hand) is { } pulse)
                            xr.Input.Vibrate(hand, pulse);
                    }

                    xr.PointerActiveHand = xrInput.ActiveHand;
                    xr.PointerEdgeTolerance = xrInput.EdgeTolerance;

                    for (int hand = 0; hand < 2; hand++)
                        xr.PointerOutOfReach[hand] = xrInput.IsOutOfReach(hand);
                }

                // After grabbing, so the panel is where this frame shows it.
                if (waited)
                    publishTrackingState(xr);

                readPanelPlacement(xr);

                // For the new placement to be persisted.
                if (resetPlacement)
                    panelPlacementReset?.Invoke();
            }
            catch (Exception e)
            {
                fail(e);
            }

            xrAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            return processed;
        }

        /// <summary>
        /// Renders and submits an XR frame. Call in place of the host's draw frame.
        /// </summary>
        /// <param name="renderer">The host's renderer, which must be the GL renderer with its context current on this thread.</param>
        /// <param name="drawGame">Draws the game (the host's base draw frame), into the panel.</param>
        public void Draw(IRenderer renderer, Action drawGame)
        {
            if (Failed)
                return;

            if (renderer is not GLRenderer glRenderer)
            {
                fail(new InvalidOperationException($"OpenXR requires the GL renderer (got {renderer.GetType().Name})."));
                return;
            }

            // The context may have been taken off this thread since the last frame: SDL does on Android when the activity is paused,
            // and what it restores on resume is the context the framework itself had already released for the suspension, i.e. none.
            // The renderer only makes it current again when the game is drawn, which is after the session's own GL work below.
            glRenderer.MakeCurrent();

            if (xr == null)
            {
                if (recovering && System.Diagnostics.Stopwatch.GetElapsedTime(lastSessionAttemptTimestamp).TotalMilliseconds < session_recovery_delay)
                {
                    idle();
                    return;
                }

                bool attempt = !WaitingForHeadset
                               || System.Diagnostics.Stopwatch.GetElapsedTime(lastSessionAttemptTimestamp).TotalMilliseconds >= headset_wait_interval;

                if (attempt && WaitingForHeadset && WaitForHeadset?.Invoke() != true)
                {
                    WaitingForHeadset = false;
                    fail(headsetWaitException ?? new InvalidOperationException("Gave up waiting for the headset."));
                    return;
                }

                if (!attempt || !tryCreateSession(glRenderer))
                {
                    // The desktop view says what's being waited for.
                    if (WaitingForHeadset)
                    {
                        drawIdle(glRenderer, drawGame);
                        idle();
                    }

                    return;
                }
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long gameDrawBytes = 0;

            try
            {
                xr!.PollEvents();

                if (!xr.IsSessionRunning)
                {
                    applyPanelPlacement(xr);
                    drawIdle(glRenderer, drawGame);
                    idle();
                    return;
                }

                // Multi-threaded: the frame wasn't waited for ahead of an update (see BeforeUpdate), as updates aren't tied to frames.
                if (!xr.FrameWaited)
                {
                    bool processedInput = waitFrame();

                    if (Failed || xr == null)
                        return;

                    if (processedInput && ScheduleToUpdate != null && xrInput.HadInput)
                        waitForUpdateThread();
                }

                lock (externalTargets)
                {
                    foreach (var target in externalTargets.Values)
                        xr.AddLayer(target);

                    // With layers behind the panel, the panel must be transparent where the game doesn't draw.
                    // (Transparent black: the compositor treats layers as premultiplied alpha; osuTK's Color4.Transparent is transparent white.)
                    glRenderer.BackbufferClearColour = externalTargets.Count > 0 ? new Color4(0, 0, 0, 0) : Color4.Black;
                }

                bool headsetFrame = xr.BeginFrame(out int framebuffer);

                if (!headsetFrame)
                    drawIdle(glRenderer, drawGame);

                if (headsetFrame)
                {
                    glRenderer.BackbufferFramebuffer = framebuffer;
                    presentedThisFrame = false;
                    prepareDesktopView();

                    long beforeGame = GC.GetAllocatedBytesForCurrentThread();
                    frameTimer?.Enter(FrameTimer.Phase.Draw);
                    drawGame();
                    frameTimer?.Enter(FrameTimer.Phase.XrRender);
                    gameDrawBytes = GC.GetAllocatedBytesForCurrentThread() - beforeGame;

                    // The environment samples the background the game just drew.
                    xr.RenderEnvironment();
                    xr.RenderScene();

                    // The framework may skip drawing (e.g. no new frame from the update thread), leaving stale content in the acquired image.
                    if (!presentedThisFrame && ++undrawnFrames % 600 == 1)
                        Logger.Log($"OpenXR: submitted {undrawnFrames} frame(s) without drawing new content.", level: LogLevel.Debug);
                }

                frameTimer?.Enter(FrameTimer.Phase.Submit);
                xr.EndFrame();
                frameTimer?.Enter(FrameTimer.Phase.Other);

                if (headsetFrame && recoveriesUsed > 0 && ++framesSinceRecovery >= session_recovery_frames)
                {
                    Logger.Log("OpenXR: the restarted session is running.");
                    recoveriesUsed = 0;
                }
            }
            catch (Exception e)
            {
                fail(e);
            }

            xrAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore - gameDrawBytes;
            gameDrawAllocatedBytes += gameDrawBytes;

            if (++allocationFrames == 1200)
            {
                Logger.Log($"OpenXR: allocations per frame: XR loop {xrAllocatedBytes / allocationFrames} B, game draw {gameDrawAllocatedBytes / allocationFrames} B.",
                    level: LogLevel.Debug);
                xrAllocatedBytes = gameDrawAllocatedBytes = 0;
                allocationFrames = 0;
            }
        }

        /// <summary>
        /// Call in place of the host's buffer swap: presentation happens when the XR frame ends.
        /// </summary>
        public void OnSwap() => presentedThisFrame = true;

        /// <summary>
        /// Makes the desktop view's target active for the frame about to be drawn if it's due, or hidden (not drawn at all) otherwise.
        /// </summary>
        /// <returns>Whether the desktop view is drawn this frame.</returns>
        private bool prepareDesktopView()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (!PresentsDesktopView
                || (lastDesktopViewTimestamp != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastDesktopViewTimestamp, now).TotalMilliseconds < desktop_view_interval))
            {
                desktopView.Hidden = true;
                return false;
            }

            lastDesktopViewTimestamp = now;
            desktopViewImage ??= new OffscreenTarget(PanelSize);
            desktopView.Hidden = false;
            desktopView.NativeFramebuffer = desktopViewImage.Framebuffer;
            return true;
        }

        /// <summary>
        /// Draws the game for the sake of its desktop view alone, while nothing is drawn for the headset.
        /// </summary>
        private void drawIdle(GLRenderer glRenderer, Action drawGame)
        {
            if (!prepareDesktopView())
                return;

            idlePanelImage ??= new OffscreenTarget(PanelSize);
            glRenderer.BackbufferFramebuffer = idlePanelImage.Framebuffer;
            drawGame();
        }

        /// <summary>
        /// Copies the desktop view drawn this frame, if any, to the window's back buffer (scaled to fit), for the host to present.
        /// Call from the host's buffer swap, on the draw thread.
        /// </summary>
        /// <param name="windowSize">The size of the window's back buffer in pixels.</param>
        /// <returns>Whether there's a new image to present.</returns>
        public bool BlitDesktopView(Size windowSize)
        {
            if (desktopViewImage == null || desktopView.Hidden || !desktopView.HasContent || windowSize.Width <= 0 || windowSize.Height <= 0)
                return false;

            // Drawn once per activation.
            desktopView.Hidden = true;

            desktopViewImage.BlitToBackbuffer(windowSize);
            return true;
        }

        /// <summary>
        /// Call when a frame went by without waiting on the runtime, which is what paces the thread otherwise: nothing else may be
        /// limiting it (see the hosts' frame limiters), and it shouldn't spin.
        /// </summary>
        private static void idle() => Thread.Sleep(2);

        private bool debugFlag(string name) => debugDirectory != null && File.Exists(Path.Combine(debugDirectory, name));

        private bool tryCreateSession(GLRenderer glRenderer)
        {
            try
            {
                xr = new OpenXRSession(platform, PanelSize)
                {
                    DebugHeadLockedPanel = debugFlag("debug-headlock-panel"),
                    DebugDirectory = debugDirectory,
                };
                xr.CreateSession();
                RuntimeName = xr.RuntimeName;
                bool passthrough = xr.EnabledExtensions.Contains("XR_FB_passthrough");
                onUpdateThread(() => supportsPassthrough.Value = passthrough);

                if (debugFlag("debug-frame-timing"))
                    frameTimer = new FrameTimer();
                xr.Environment = environment;
                xr.PassthroughOpacity = passthroughOpacity;
                xr.SuppressBoundaryInPassthrough = suppressBoundaryInPassthrough;
                xr.FlashReduction = flashReduction;
                xr.ShowControllers = showControllers;
                xr.IdleControllerHideDelay = idleControllerHideDelay;
                xr.HeldModel = heldModel;
                xr.GhostHands = GhostHands;
                xr.GenericControllerModels = genericControllerModels;

                lock (panelLock)
                    panelPlacementSet = true;

                applyPanelPlacement(xr);

                xr.StateChanged += state =>
                {
                    bool focused = state == XrSessionState.XR_SESSION_STATE_FOCUSED;
                    bool displayed = focused || state == XrSessionState.XR_SESSION_STATE_VISIBLE;

                    onUpdateThread(() =>
                    {
                        isDisplayed.Value = displayed;
                        hasInputFocus.Value = focused;
                    });
                };

                xr.ExitRequested += () => ExitRequested?.Invoke();
                xr.SystemRecentered += () => systemRecentered = true;

                if (WaitingForHeadset)
                    Logger.Log("OpenXR: the headset is available now.");

                WaitingForHeadset = false;
                headsetWaitException = null;
                recovering = false;
                return true;
            }
            catch (Exception e) when (WaitForHeadset != null && isHeadsetUnavailable(e))
            {
                if (!WaitingForHeadset)
                    Logger.Log($"OpenXR: no headset yet ({e.Message}) Waiting for it.");

                xr?.Dispose();
                xr = null;

                WaitingForHeadset = true;
                headsetWaitException = e;
                lastSessionAttemptTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                return false;
            }
            catch (Exception e)
            {
                WaitingForHeadset = false;
                fail(e);
                return false;
            }
        }

        /// <summary>
        /// Whether starting the session failed for want of a headset, which may yet turn up: the runtime is there but has no headset
        /// (SteamVR refuses the instance with a runtime failure while a wireless headset isn't connected), or isn't up yet.
        /// </summary>
        private static bool isHeadsetUnavailable(Exception e)
            => e is XrException
            {
                Result: XrResult.XR_ERROR_RUNTIME_FAILURE
                or XrResult.XR_ERROR_RUNTIME_UNAVAILABLE
                or XrResult.XR_ERROR_FORM_FACTOR_UNAVAILABLE
                or XrResult.XR_ERROR_INSTANCE_LOST
            };

        private void fail(Exception e)
        {
            if (recoveriesUsed < SessionRecoveryAttempts)
            {
                recoveriesUsed++;
                framesSinceRecovery = 0;
                Logger.Error(e, $"OpenXR failed; starting the session again (attempt {recoveriesUsed} of {SessionRecoveryAttempts}).");

                try
                {
                    xr?.Dispose(true);
                }
                catch (Exception disposeException)
                {
                    Logger.Error(disposeException, "OpenXR: the failed session couldn't be closed cleanly.");
                }

                xr = null;
                frameTimer = null;
                recovering = true;
                lastSessionAttemptTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

                // Nothing is displayed until the new session is (its state changes say when).
                onUpdateThread(() =>
                {
                    isDisplayed.Value = false;
                    hasInputFocus.Value = false;
                });

                return;
            }

            Failed = true;
            Logger.Error(e, "OpenXR failed; the game can't be displayed in the headset.");
            SessionFailed?.Invoke();
        }

        public void Dispose()
        {
            xr?.Dispose();
            xr = null;
            GC.SuppressFinalize(this);
        }
    }
}
