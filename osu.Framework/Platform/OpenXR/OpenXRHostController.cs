// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
    /// Requires the GL renderer and single-threaded execution: the frame is waited for before the update and rendered in the draw.
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

        /// <summary>
        /// The title for a desktop host's window: says so when the game isn't being displayed in the headset (e.g. it's idle and the
        /// runtime is in standby), as the window itself shows nothing and the game would otherwise just look frozen.
        /// </summary>
        public static string WindowTitle(string gameTitle, bool displayed) => displayed ? gameTitle : $"{gameTitle} (VR: waiting for the headset)";

        private readonly BindableBool supportsPassthrough = new BindableBool(true);

        /// <summary>
        /// Whether the runtime can show the room (<see cref="XrEnvironmentMode.Passthrough"/>). Assumed until the session has started;
        /// changes on the draw thread.
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

        private readonly OpenXRInputHandler xrInput = new OpenXRInputHandler();
        private readonly PanelManipulator panelManipulator = new PanelManipulator();

        /// <summary>
        /// Controller input, for consumers that need per-controller state (e.g. mapping each trigger to its own gameplay key).
        /// Hosts add it to their input handlers.
        /// </summary>
        public OpenXRInputHandler ControllerInput => xrInput;

        private float panelDistance = 1.5f;
        private float panelAngle = 60 * MathF.PI / 180;

        /// <summary>
        /// Distance of the game panel from its centre of curvature (the viewer, unless the panel was moved), in metres. Also changed by the
        /// user pushing/pulling the grabbed panel.
        /// </summary>
        public float PanelDistance
        {
            get => xr?.PanelDistance ?? panelDistance;
            set
            {
                panelDistance = value;
                if (xr != null) xr.PanelDistance = value;
            }
        }

        /// <summary>
        /// Horizontal angle covered by the game panel, in radians. Also changed by the user resizing the grabbed panel.
        /// </summary>
        public float PanelHorizontalAngle
        {
            get => xr?.PanelHorizontalAngle ?? panelAngle;
            set
            {
                panelAngle = value;
                if (xr != null) xr.PanelHorizontalAngle = value;
            }
        }

        private System.Numerics.Vector3 panelOffset;
        private System.Numerics.Quaternion panelRotation = OpenXRSession.DefaultPanelRotation;

        /// <summary>
        /// Where the panel's centre of curvature is relative to where the viewer was facing when last recentered (in that frame: +X right,
        /// +Y up, -Z forward; metres). Zero by default: at the viewer. Also changed by the user moving the grabbed panel.
        /// </summary>
        public System.Numerics.Vector3 PanelOffset
        {
            get => xr?.PanelOffset ?? panelOffset;
            set
            {
                panelOffset = value;
                if (xr != null) xr.PanelOffset = value;
            }
        }

        /// <summary>
        /// The panel's orientation relative to where the viewer was facing when last recentered (see <see cref="PanelOffset"/>).
        /// By default straight ahead, slightly below the gaze. Also changed by the user turning the grabbed panel.
        /// </summary>
        public System.Numerics.Quaternion PanelRotation
        {
            get => xr?.PanelRotation ?? panelRotation;
            set
            {
                panelRotation = value;
                if (xr != null) xr.PanelRotation = value;
            }
        }

        /// <summary>
        /// Puts the panel back straight ahead of where the viewer faced when last recentered (keeping its size and distance).
        /// </summary>
        public void ResetPanelPlacement()
        {
            PanelOffset = System.Numerics.Vector3.Zero;
            PanelRotation = OpenXRSession.DefaultPanelRotation;
        }

        /// <summary>
        /// Invoked when the user finishes moving, turning, resizing or pushing/pulling the panel, so the new <see cref="PanelDistance"/>,
        /// <see cref="PanelHorizontalAngle"/>, <see cref="PanelOffset"/> and <see cref="PanelRotation"/> can be persisted.
        /// </summary>
        public event Action? PanelLayoutChanged
        {
            add => panelManipulator.LayoutChanged += value;
            remove => panelManipulator.LayoutChanged -= value;
        }

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
            lock (externalTargets)
            {
                if (!externalTargets.TryGetValue(name, out var target))
                    externalTargets[name] = target = new ExternalRenderTarget(name);

                return target;
            }
        }

        /// <summary>
        /// Call at the start of the host's update frame, before the game updates.
        /// </summary>
        /// <remarks>
        /// In single-threaded execution the loop runs input, audio, update then draw. Waiting for the XR frame here (rather than in
        /// <see cref="Draw"/>) lets controller poses be sampled at the predicted display time of the frame this update produces,
        /// and consumed by this same update.
        /// </remarks>
        public void BeforeUpdate()
        {
            if (xr is not { IsSessionRunning: true, FrameWaited: false } || Failed)
                return;

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            try
            {
                if (xr.Input != null)
                {
                    xr.Input.AimRotationOffset = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, -xrInput.PointerTilt);
                    xr.Input.AimPositionOffset = xrInput.PointerOffset;
                }

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

                    for (int hand = 0; hand < 2; hand++)
                    {
                        if (xrInput.TakeHaptic(hand) is { } pulse)
                            xr.Input.Vibrate(hand, pulse);
                    }
                    xr.PointerActiveHand = xrInput.ActiveHand;
                    xr.PointerEdgeTolerance = xrInput.EdgeTolerance;
                }
            }
            catch (Exception e)
            {
                fail(e);
            }

            xrAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
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

            if (xr == null && !tryCreateSession(glRenderer))
                return;

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long gameDrawBytes = 0;

            try
            {
                xr!.PollEvents();

                if (!xr.IsSessionRunning)
                    return;

                lock (externalTargets)
                {
                    foreach (var target in externalTargets.Values)
                        xr.AddLayer(target);

                    // With layers behind the panel, the panel must be transparent where the game doesn't draw.
                    // (Transparent black: the compositor treats layers as premultiplied alpha; osuTK's Color4.Transparent is transparent white.)
                    glRenderer.BackbufferClearColour = externalTargets.Count > 0 ? new Color4(0, 0, 0, 0) : Color4.Black;
                }

                if (xr.BeginFrame(out int framebuffer))
                {
                    glRenderer.BackbufferFramebuffer = framebuffer;
                    presentedThisFrame = false;

                    long beforeGame = GC.GetAllocatedBytesForCurrentThread();
                    frameTimer?.Enter(FrameTimer.Phase.Draw);
                    drawGame();
                    frameTimer?.Enter(FrameTimer.Phase.XrRender);
                    gameDrawBytes = GC.GetAllocatedBytesForCurrentThread() - beforeGame;

                    // The environment samples the background the game just drew.
                    xr.RenderEnvironment();
                    xr.RenderControllers();

                    // The framework may skip drawing (e.g. no new frame from the update thread), leaving stale content in the acquired image.
                    if (!presentedThisFrame && ++undrawnFrames % 600 == 1)
                        Logger.Log($"OpenXR: submitted {undrawnFrames} frame(s) without drawing new content.", level: LogLevel.Debug);
                }

                frameTimer?.Enter(FrameTimer.Phase.Submit);
                xr.EndFrame();
                frameTimer?.Enter(FrameTimer.Phase.Other);
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

        private bool debugFlag(string name) => debugDirectory != null && File.Exists(Path.Combine(debugDirectory, name));

        private bool tryCreateSession(GLRenderer glRenderer)
        {
            try
            {
                glRenderer.MakeCurrent();

                xr = new OpenXRSession(platform, PanelSize)
                {
                    DebugHeadLockedPanel = debugFlag("debug-headlock-panel"),
                    DebugDirectory = debugDirectory,
                };
                xr.CreateSession();
                RuntimeName = xr.RuntimeName;
                supportsPassthrough.Value = xr.EnabledExtensions.Contains("XR_FB_passthrough");

                if (debugFlag("debug-frame-timing"))
                    frameTimer = new FrameTimer();
                xr.Environment = environment;
                xr.PassthroughOpacity = passthroughOpacity;
                xr.SuppressBoundaryInPassthrough = suppressBoundaryInPassthrough;
                xr.FlashReduction = flashReduction;
                xr.ShowControllers = showControllers;
                xr.HeldModel = heldModel;
                xr.PanelDistance = panelDistance;
                xr.PanelHorizontalAngle = panelAngle;
                xr.PanelOffset = panelOffset;
                xr.PanelRotation = panelRotation;

                xr.StateChanged += state =>
                {
                    bool focused = state == XrSessionState.XR_SESSION_STATE_FOCUSED;

                    isDisplayed.Value = focused || state == XrSessionState.XR_SESSION_STATE_VISIBLE;
                    hasInputFocus.Value = focused;
                };

                xr.ExitRequested += () => ExitRequested?.Invoke();
                return true;
            }
            catch (Exception e)
            {
                fail(e);
                return false;
            }
        }

        private void fail(Exception e)
        {
            Failed = true;
            Logger.Error(e, "OpenXR failed; the game can't be displayed in the headset.");
        }

        public void Dispose()
        {
            xr?.Dispose();
            xr = null;
            GC.SuppressFinalize(this);
        }
    }
}
