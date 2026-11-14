// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using osuTK.Graphics.ES30;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Owns the OpenXR instance, session, reference space and the 2D panel swapchain for an immersive app
    /// rendering with OpenGL ES.
    /// </summary>
    /// <remarks>
    /// All methods must be called on the thread which owns the GL context (the draw thread).
    /// </remarks>
    internal sealed unsafe class OpenXRSession : IDisposable, IPanelHost
    {
        /// <summary>
        /// Enabled when available. Used by later stages (curved layers, refresh rate, environments, timing).
        /// </summary>
        private static readonly string[] optional_extensions =
        {
            "XR_KHR_composition_layer_equirect2",
            "XR_KHR_composition_layer_cylinder",
            "XR_KHR_convert_timespec_time",
            "XR_FB_display_refresh_rate",
            "XR_FB_passthrough",
            "XR_META_boundary_visibility",
            "XR_FB_foveation",
            "XR_FB_foveation_configuration",
            "XR_FB_swapchain_update_state",
            "XR_FB_composition_layer_settings",
            "XR_EXT_performance_settings",
            "XR_EXT_local_floor",
            "XR_META_touch_controller_plus",
            // Interaction profiles of other desktop controllers.
            "XR_EXT_hp_mixed_reality_controller",
            "XR_HTC_vive_cosmos_controller_interaction",
            "XR_HTC_vive_focus3_controller_interaction",
            // Required by XR_EXT_render_model: SteamVR fails instance creation (XR_ERROR_VALIDATION_FAILURE) if it isn't enabled along with it.
            "XR_EXT_uuid",
            "XR_EXT_render_model",
            "XR_EXT_interaction_render_model",
            "XR_FB_render_model",
        };

        // GL constants not exposed by osuTK's ES30 enums.
        private const long gl_rgba8 = 0x8058;
        private const long gl_srgb8_alpha8 = 0x8C43;
        private const EnableCap gl_framebuffer_srgb_ext = (EnableCap)0x8DB9;

        /// <summary>
        /// The size of the 2D panel the game renders into, in pixels.
        /// </summary>
        public Size PanelSize { get; }

        /// <summary>
        /// The panel's radius of curvature in metres: its distance from <see cref="PanelOffset"/> (the viewer, unless the panel was moved).
        /// </summary>
        public float PanelDistance { get; set; } = 1.5f;

        /// <summary>
        /// Horizontal angle covered by the panel, in radians (its height follows the image's aspect ratio).
        /// </summary>
        public float PanelHorizontalAngle { get; set; } = 60 * MathF.PI / 180;

        /// <summary>
        /// Where the panel's centre of curvature is, relative to the anchor (in the anchor's frame, metres). Zero puts it at the viewer.
        /// Changed by grabbing the panel; kept on recenter (the panel keeps its place relative to where the viewer faces).
        /// </summary>
        public Vector3 PanelOffset { get; set; }

        /// <summary>
        /// The panel's orientation relative to the anchor: -Z towards the panel's centre, +Y along the panel's vertical axis.
        /// Changed by grabbing the panel; kept on recenter.
        /// </summary>
        public Quaternion PanelRotation { get; set; } = DefaultPanelRotation;

        /// <summary>
        /// The panel's default orientation relative to the anchor: straight ahead, slightly below the gaze.
        /// </summary>
        public static Quaternion DefaultPanelRotation => Quaternion.CreateFromAxisAngle(Vector3.UnitX, -5 * MathF.PI / 180);

        /// <summary>
        /// The panel's current placement and shape, in the session's base space. Updated every frame.
        /// </summary>
        public PanelGeometry Panel { get; } = new PanelGeometry();

        /// <summary>
        /// Whether layers behind the panel are shown as sphere sections around the viewer (else flat quads).
        /// </summary>
        public bool CurvedBackground => enabledExtensions.Contains("XR_KHR_composition_layer_equirect2") || drawSurfacesInScene;

        /// <summary>
        /// Whether the game's images (the panel and the layers behind it) are drawn into the scene's projection layer by
        /// <see cref="OpenXRSceneRenderer"/> rather than submitted as composition layers of their own.
        /// </summary>
        /// <remarks>
        /// For runtimes without cylinder layers. That is SteamVR, which shows quad layers as overlays, in front of every projection
        /// layer whatever the order they're submitted in: the controllers would be hidden behind the panel. Drawing the panel here also
        /// keeps it curved.
        /// </remarks>
        private bool drawSurfacesInScene => !enabledExtensions.Contains("XR_KHR_composition_layer_cylinder") && !DebugHeadLockedPanel && !sceneRenderingFailed;

        private readonly OpenXRSceneRenderer.Surface[] sceneSurfaces = new OpenXRSceneRenderer.Surface[2 + OpenXRPointerVisuals.MAX_LAYERS + 4];

        /// <summary>
        /// Horizontal angle of the background layer as seen by the viewer, in radians.
        /// </summary>
        public float BackgroundAngle { get; set; } = 81 * MathF.PI / 180;

        private void updatePanelGeometry()
        {
            var anchorOrientation = DebugHeadLockedPanel ? Quaternion.Identity : AnchorOrientation;

            Panel.Cylinder = enabledExtensions.Contains("XR_KHR_composition_layer_cylinder") || drawSurfacesInScene;
            Panel.Aspect = (float)PanelSize.Width / PanelSize.Height;
            Panel.Radius = PanelDistance;
            Panel.HorizontalAngle = PanelHorizontalAngle;
            Panel.Centre = (DebugHeadLockedPanel ? Vector3.Zero : AnchorPosition) + Vector3.Transform(PanelOffset, anchorOrientation);
            Panel.Orientation = Quaternion.Normalize(anchorOrientation * PanelRotation);
        }

        void IPanelHost.SetPanelPose(Vector3 centre, Quaternion orientation)
        {
            var anchorPosition = DebugHeadLockedPanel ? Vector3.Zero : AnchorPosition;
            var inverseAnchor = Quaternion.Inverse(DebugHeadLockedPanel ? Quaternion.Identity : AnchorOrientation);

            PanelOffset = Vector3.Transform(centre - anchorPosition, inverseAnchor);
            PanelRotation = Quaternion.Normalize(inverseAnchor * orientation);
            updatePanelGeometry();
        }

        void IPanelHost.SetPanelShape(float radius, float horizontalAngle)
        {
            PanelDistance = radius;
            PanelHorizontalAngle = horizontalAngle;
            updatePanelGeometry();
        }

        /// <summary>
        /// Debug aid: when set, the panel is locked to the head (VIEW space) instead of the world, so it is always in view
        /// (e.g. for screenshots taken while the headset isn't worn). Pointer input and visuals don't account for this.
        /// </summary>
        public bool DebugHeadLockedPanel { get; set; }

        /// <summary>
        /// Controller input. Available once <see cref="CreateSession"/> has been called.
        /// </summary>
        public OpenXRInput? Input { get; private set; }

        /// <summary>
        /// The viewer's head position in the LOCAL space, sampled in <see cref="WaitFrame"/>.
        /// </summary>
        public Vector3 HeadPosition { get; private set; }

        /// <summary>
        /// The viewer's head orientation in the LOCAL space, sampled in <see cref="WaitFrame"/>.
        /// </summary>
        public Quaternion HeadOrientation { get; private set; } = Quaternion.Identity;

        /// <summary>
        /// Position of the anchor everything is placed relative to, in the LOCAL space: the viewer's head when last recentered, facing
        /// where they looked (-Z is "forward" in the anchor's frame).
        /// </summary>
        public Vector3 AnchorPosition { get; private set; }

        /// <summary>
        /// Orientation of the anchor in the LOCAL space.
        /// </summary>
        public Quaternion AnchorOrientation { get; private set; } = Quaternion.Identity;

        /// <summary>
        /// Head pitch within this angle of level is snapped to level when placing the anchor (a slight downward glance at launch
        /// shouldn't tilt everything), in radians.
        /// </summary>
        public float LevelPitchSnap { get; set; } = 15 * MathF.PI / 180;

        /// <summary>
        /// Invoked after the anchor is re-placed (session start or recenter).
        /// </summary>
        public event Action? AnchorChanged;

        private bool recenterPending = true;

        /// <summary>
        /// Re-places the anchor in front of the viewer on the next frame.
        /// </summary>
        public void Recenter() => recenterPending = true;

        public Vector3 FromAnchorSpace(Vector3 anchorPoint) => Vector3.Transform(anchorPoint, AnchorOrientation) + AnchorPosition;

        /// <summary>
        /// Expresses a direction in the base space in the anchor's frame (the VIEW space itself in the head-locked debug mode).
        /// </summary>
        public Vector3 ToAnchorDirection(Vector3 direction)
            => DebugHeadLockedPanel ? direction : Vector3.Transform(direction, Quaternion.Inverse(AnchorOrientation));

        /// <summary>
        /// Places the anchor at the head, facing where the head looks: yaw always, pitch beyond <see cref="LevelPitchSnap"/> (so lying on
        /// your back puts the panel overhead), and roll only when looking nearly straight up or down, where the world's up direction can't
        /// define the panel's up.
        /// </summary>
        private void placeAnchor(Vector3 headPosition, Quaternion headOrientation)
        {
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, headOrientation);
            Vector3 headUp = Vector3.Transform(Vector3.UnitY, headOrientation);

            float pitch = MathF.Asin(Math.Clamp(forward.Y, -1, 1));

            if (MathF.Abs(pitch) < LevelPitchSnap)
            {
                forward.Y = 0;
                forward = Vector3.Normalize(forward);
            }

            // Near vertical, world up is (almost) parallel to forward, so use the head's own up instead.
            Vector3 up = MathF.Abs(forward.Y) > MathF.Cos(30 * MathF.PI / 180) ? headUp : Vector3.UnitY;

            Vector3 z = -forward;
            Vector3 x = Vector3.Cross(up, z);
            x = x.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(x);
            Vector3 y = Vector3.Cross(z, x);

            var rotation = new Matrix4x4(
                x.X, x.Y, x.Z, 0,
                y.X, y.Y, y.Z, 0,
                z.X, z.Y, z.Z, 0,
                0, 0, 0, 1);

            AnchorPosition = headPosition;
            AnchorOrientation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));

            // The panel keeps its placement relative to the anchor (by default straight ahead, slightly below the gaze).
            Logger.Log($"OpenXR: anchor placed at head, pitch {pitch * 180 / MathF.PI:N0} deg");
            AnchorChanged?.Invoke();
        }

        /// <summary>
        /// The controller currently driving the pointer, used to style the pointer visuals.
        /// </summary>
        public int PointerActiveHand { get; set; } = OpenXRInput.RIGHT;

        /// <summary>
        /// See <see cref="OpenXRInputHandler.EdgeTolerance"/>.
        /// </summary>
        public float PointerEdgeTolerance { get; set; } = 0.05f;

        /// <summary>
        /// Whether <see cref="WaitFrame"/> has been called for a frame which hasn't been ended yet.
        /// </summary>
        public bool FrameWaited { get; private set; }

        /// <summary>
        /// Whether the session has begun and frames must be submitted.
        /// </summary>
        public bool IsSessionRunning { get; private set; }

        /// <summary>
        /// The set of instance extensions that were enabled.
        /// </summary>
        public IReadOnlySet<string> EnabledExtensions => enabledExtensions;

        /// <summary>
        /// The name the runtime reports (e.g. "SteamVR/OpenXR").
        /// </summary>
        public string RuntimeName { get; private set; } = string.Empty;

        /// <summary>
        /// Invoked when the session state changes. The new state is passed.
        /// </summary>
        /// <remarks>
        /// VISIBLE: frames are displayed but another app (system menu, keyboard overlay) has input focus.
        /// FOCUSED: displayed and receiving input.
        /// </remarks>
        public event Action<XrSessionState>? StateChanged;

        /// <summary>
        /// Invoked when the runtime wants the application to exit.
        /// </summary>
        public event Action? ExitRequested;

        private readonly XrFunctions xr;
        private readonly HashSet<string> enabledExtensions = new HashSet<string>();

        private ulong instance;
        private ulong systemId;
        private ulong session;
        private ulong localSpace;
        private ulong viewSpace;
        private OpenXRPointerVisuals? pointerVisuals;
        private OpenXRSwapchainTarget? panel;
        private OpenXRPassthrough? passthrough;
        private OpenXREnvironmentRenderer? environmentRenderer;

        /// <summary>
        /// Flash reduction strength for the custom field's wall, 0 (off) to 1 (strongest).
        /// </summary>
        public float FlashReduction { get; set; } = 0.5f;

        private readonly float[] environmentBands = new float[OpenXREnvironmentRenderer.BAND_COUNT];
        private float environmentPulse;
        private bool environmentKiai;

        /// <summary>
        /// Music data for the custom field: per-band amplitudes (0..1, low to high), a beat pulse (1 on the beat, decaying) and kiai.
        /// </summary>
        public void SetEnvironmentAudio(ReadOnlySpan<float> bands, float pulse, bool kiai)
        {
            bands[..Math.Min(bands.Length, environmentBands.Length)].CopyTo(environmentBands);
            environmentPulse = pulse;
            environmentKiai = kiai;
        }

        private bool useField => Environment == XrEnvironmentMode.Field;

        /// <summary>
        /// Whether the environment renderer draws this frame (ambient void or custom field; passthrough shows the room instead).
        /// </summary>
        private bool useEnvironmentRenderer => Environment == XrEnvironmentMode.Field || (Environment == XrEnvironmentMode.Void && !usePassthrough);

        /// <summary>
        /// Distance from the viewer to the custom field's wall, in metres.
        /// </summary>
        public float WallDistance { get; set; } = 10f;

        /// <summary>
        /// The custom field's wall's horizontal angle as seen by the viewer, in radians.
        /// </summary>
        public float WallAngle { get; set; } = 69 * MathF.PI / 180;

        /// <summary>
        /// Head height above the floor assumed when the real floor height isn't known, in metres.
        /// </summary>
        public float FallbackHeadHeight { get; set; } = 1.4f;

        private ulong localFloorSpace;

        /// <summary>
        /// Height of the real floor in the base space (from XR_EXT_local_floor when available).
        /// </summary>
        private float floorY(ulong space)
        {
            if (DebugHeadLockedPanel)
                return -FallbackHeadHeight;

            if (localFloorSpace != 0)
            {
                var location = new XrSpaceLocation { type = XR_TYPE_SPACE_LOCATION };

                if (xr.xrLocateSpace(localFloorSpace, space, frameState.predictedDisplayTime, &location).Succeeded()
                    && (location.locationFlags & XrSpaceLocationFlags.XR_SPACE_LOCATION_POSITION_VALID_BIT) != 0)
                    return location.pose.position.y;
            }

            return AnchorPosition.Y - FallbackHeadHeight;
        }

        /// <summary>
        /// Where the environment's floor, grid, wall and glow are this frame. The floor is always level at the real floor height; the grid
        /// faces the anchor's heading; the wall (custom field) is placed in the anchor's (gaze) frame, so it follows recentering, pitch
        /// included, while the floor stays put.
        /// </summary>
        private EnvironmentScene environmentScene(ulong space)
        {
            Vector3 anchorPosition = DebugHeadLockedPanel ? Vector3.Zero : AnchorPosition;
            Quaternion anchorOrientation = DebugHeadLockedPanel ? Quaternion.Identity : AnchorOrientation;

            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, anchorOrientation);
            Vector3 right = Vector3.Transform(Vector3.UnitX, anchorOrientation);
            Vector3 up = Vector3.Transform(Vector3.UnitY, anchorOrientation);

            float floor = floorY(space);
            float headHeight = Math.Max(anchorPosition.Y - floor, 0.2f);

            float wallWidth = 2 * WallDistance * MathF.Tan(WallAngle / 2);
            float wallHeight = wallWidth * PanelSize.Height / PanelSize.Width;

            // Heading for the grid: the forward direction flattened onto the floor (or the up direction when looking straight up/down).
            Vector3 heading = new Vector3(forward.X, 0, forward.Z);
            if (heading.LengthSquared() < 1e-4f)
                heading = new Vector3(-up.X, 0, -up.Z) * MathF.Sign(forward.Y);

            // Where the background is seen, for the halo around it: the wall (field), or the floating background layer (void).
            Vector4 halo;

            if (useField)
            {
                // Tangents (the wall is flat): it stands on the floor, wallHeight tall, WallDistance away.
                halo = new Vector4(0, (wallHeight / 2 - headHeight) / WallDistance, MathF.Tan(WallAngle / 2), wallHeight / 2 / WallDistance);
            }
            else
            {
                float aspect = (float)PanelSize.Height / PanelSize.Width;
                float halfHeight = CurvedBackground ? BackgroundAngle * aspect / 2 : MathF.Atan(MathF.Tan(BackgroundAngle / 2) * aspect);
                halo = new Vector4(0, 0, BackgroundAngle / 2, halfHeight);
            }

            return new EnvironmentScene
            {
                Field = useField,
                FloorY = floor,
                GridOrigin = new Vector2(anchorPosition.X, anchorPosition.Z),
                GridYaw = MathF.Atan2(heading.X, -heading.Z),
                // Like a cinema screen: when looking level, the wall stands on the floor.
                WallCentre = anchorPosition + forward * WallDistance + up * (wallHeight / 2 - headHeight),
                WallRight = right,
                WallUp = up,
                WallSize = new Vector2(wallWidth, wallHeight),
                GlowDirection = forward,
                Halo = halo,
            };
        }

        /// <summary>
        /// Renders the custom field environment for the current frame, after the game has drawn (so the wall shows this frame's
        /// background). Must be called between <see cref="BeginFrame"/> and <see cref="EndFrame"/>.
        /// </summary>
        public void RenderEnvironment()
        {
            if (!useEnvironmentRenderer)
                return;

            try
            {
                environmentRenderer ??= OpenXREnvironmentRenderer.Create(xr, instance, systemId, session, swapchainFormat, platform.SwapchainImageType, platform.EnvironmentResolutionScale, viewLocator!);

                // (A loop rather than LINQ: this runs every frame and shouldn't allocate.)
                OpenXRLayer? background = null;

                foreach (var layer in layers)
                {
                    if (layer.Target.Name == "background")
                        background = layer;
                }
                int backgroundTexture = background?.Target.HasContent == true ? background.Images?.CurrentTexture ?? 0 : 0;

                ulong space = DebugHeadLockedPanel ? viewSpace : localSpace;

                environmentRenderer.FlashReduction = FlashReduction;
                environmentRenderer.SetAudio(environmentBands, environmentPulse, environmentKiai);
                // With the game's surfaces drawn in the scene, so is the environment, by the scene renderer (see RenderScene).
                environmentRenderer.Render(space, frameState.predictedDisplayTime, environmentScene(space), backgroundTexture, ownLayer: !drawSurfacesInScene);
            }
            catch (Exception e)
            {
                Logger.Error(e, "OpenXR: environment rendering failed; using the void environment.");
                Environment = XrEnvironmentMode.Void;
            }
        }

        private OpenXRViewLocator? viewLocator;
        private OpenXRSceneRenderer? sceneRenderer;
        private OpenXRControllerModels? controllerModels;
        private bool sceneRenderingFailed;

        /// <summary>
        /// A directory holding development files (e.g. controller-left.glb / controller-right.glb, used as controller models when the
        /// runtime provides none), or null. Must be set before <see cref="CreateSession"/>.
        /// </summary>
        public string? DebugDirectory { get; set; }

        /// <summary>
        /// Whether models of the controllers are shown where they're held.
        /// </summary>
        public bool ShowControllers { get; set; } = true;

        /// <summary>
        /// See <see cref="OpenXRHostController.HeldModel"/>.
        /// </summary>
        public byte[]? HeldModel { get; set; }

        private bool holdingModel => ShowControllers && HeldModel != null && controllerModels != null && !sceneRenderingFailed;

        /// <summary>
        /// Renders the controller models (and, where the game draws the whole scene itself, everything behind them) for the current frame. Must be called between <see cref="BeginFrame"/> and <see cref="EndFrame"/>.
        /// </summary>
        public void RenderScene()
        {
            bool surfaces = drawSurfacesInScene;

            if ((!ShowControllers && !surfaces) || sceneRenderingFailed || Input == null || DebugHeadLockedPanel)
                return;

            try
            {
                if (sceneRenderer == null)
                {
                    sceneRenderer = OpenXRSceneRenderer.Create(xr, instance, systemId, session, swapchainFormat, platform.SwapchainImageType, platform.SceneResolutionScale, viewLocator!);

                    if (controllerModels != null)
                        controllerModels.ModelRemoved += m => sceneRenderer.Release(m.Model);
                }

                long time = frameState.predictedDisplayTime;

                if (controllerModels != null)
                {
                    controllerModels.HeldModel = HeldModel;
                    controllerModels.Update(localSpace, time, Input.Hands);
                }

                int surfaceCount = 0;

                if (surfaces)
                {
                    bool fieldRendered = useField && useEnvironmentRenderer && environmentRenderer?.HasRendered == true;

                    // The same layers, in the same order, as EndFrame submits to runtimes which composite them.
                    foreach (var layer in layers)
                    {
                        if (surfaceCount == sceneSurfaces.Length - 1 - OpenXRPointerVisuals.MAX_LAYERS)
                            break;

                        if (layer.Images == null || layer.Images.CurrentTexture == 0 || !layer.Target.HasContent)
                            continue;

                        if ((usePassthrough || fieldRendered) && layer.Target.Name == "background")
                            continue;

                        placeLayer(layer);

                        // A sphere section around the anchor, like the equirect layer other runtimes are given (see EndFrame).
                        sceneSurfaces[surfaceCount++] = new OpenXRSceneRenderer.Surface
                        {
                            Texture = layer.Images.CurrentTexture,
                            QuadCentre = AnchorPosition,
                            QuadOrientation = AnchorOrientation,
                            QuadSize = new System.Numerics.Vector2(layer.HorizontalAngle, layer.HorizontalAngle * PanelSize.Height / PanelSize.Width),
                            SphereRadius = layer.Radius,
                        };
                    }

                    if (panel != null && panel.CurrentTexture != 0)
                    {
                        updatePanelGeometry();
                        sceneSurfaces[surfaceCount++] = new OpenXRSceneRenderer.Surface { Texture = panel.CurrentTexture, Geometry = Panel, Opaque = layers.Count == 0 };

                        // The pointer's beams and reticle: the same quads as EndFrame submits as layers, drawn over the panel instead.
                        // (SteamVR also blends such layers wrongly, leaving a dark fringe around them.)
                        if (pointerVisuals != null)
                        {
                            var pointerLayers = stackalloc XrCompositionLayerBaseHeader*[OpenXRPointerVisuals.MAX_LAYERS];
                            int pointerCount = pointerVisuals.BuildLayers(Input.Hands, PointerActiveHand, HeadPosition, this, PointerEdgeTolerance, holdingModel ? 0 : 0.02f, pointerLayers);

                            for (int i = 0; i < pointerCount; i++)
                            {
                                var quad = (XrCompositionLayerQuad*)pointerLayers[i];

                                sceneSurfaces[surfaceCount++] = new OpenXRSceneRenderer.Surface
                                {
                                    Texture = pointerVisuals.TextureFor(quad->subImage.swapchain),
                                    QuadCentre = quad->pose.position.ToNumerics(),
                                    QuadOrientation = quad->pose.orientation.ToNumerics(),
                                    QuadSize = new System.Numerics.Vector2(quad->size.width, quad->size.height),
                                };
                            }
                        }
                    }
                }

                sceneRenderer.Render(localSpace, time, Input.Hands, PointerActiveHand, new System.Numerics.Vector3(0.45f, 0.8f, 1f), controllerModels,
                    sceneSurfaces.AsSpan(0, surfaceCount), ShowControllers,
                    surfaces && useEnvironmentRenderer && environmentRenderer?.HasRendered == true ? environmentRenderer : null);
            }
            catch (Exception e)
            {
                // Not essential: carry on without them.
                Logger.Error(e, "OpenXR: controller rendering failed; controller models are disabled.");
                sceneRenderingFailed = true;
            }
        }

        /// <summary>
        /// What surrounds the panel. Can be changed at any time.
        /// </summary>
        public XrEnvironmentMode Environment { get; set; }

        /// <summary>
        /// How strongly the room shows in <see cref="XrEnvironmentMode.Passthrough"/>: 1 = full, lower fades towards black.
        /// </summary>
        public float PassthroughOpacity { get; set; } = 1;

        /// <summary>
        /// Whether to ask the runtime to hide its boundary (guardian) while the room is shown in <see cref="XrEnvironmentMode.Passthrough"/>.
        /// Runtimes only allow this while a passthrough layer is shown, and may refuse; the boundary is always shown otherwise.
        /// </summary>
        public bool SuppressBoundaryInPassthrough { get; set; }

        private bool boundarySuppressed;
        private int boundaryRequestCooldown;

        private bool usePassthrough => Environment == XrEnvironmentMode.Passthrough && enabledExtensions.Contains("XR_FB_passthrough");
        private long swapchainFormat;

        private XrSessionState state = XrSessionState.XR_SESSION_STATE_UNKNOWN;
        private XrFrameState frameState;

        private readonly List<OpenXRLayer> layers = new List<OpenXRLayer>();

        /// <summary>
        /// A layer composited behind the panel, which part of the game's draw tree is redirected into.
        /// </summary>
        private class OpenXRLayer
        {
            public readonly ExternalRenderTarget Target;
            public OpenXRSwapchainTarget? Images;

            /// <summary>
            /// Centre of the layer in the LOCAL space.
            /// </summary>
            public Vector3 Position;

            /// <summary>
            /// Physical width in metres (height follows the panel's aspect ratio). Used when flat.
            /// </summary>
            public float Width;

            /// <summary>
            /// Sphere radius and horizontal angle (radians), used when curved. The sphere is centred on the anchor.
            /// </summary>
            public float Radius;

            public float HorizontalAngle;

            public OpenXRLayer(ExternalRenderTarget target)
            {
                Target = target;
            }
        }

        /// <summary>
        /// Distance of the background layer. Far enough to read as a backdrop with real parallax against the panel.
        /// </summary>
        public float BackgroundDistance { get; set; } = 6f;

        /// <summary>
        /// Adds a layer for <paramref name="target"/>, composited behind the panel. Its swapchain is created on the next frame.
        /// </summary>
        public void AddLayer(ExternalRenderTarget target)
        {
            if (layers.Any(l => l.Target == target))
                return;

            layers.Add(new OpenXRLayer(target));
        }

        private void placeLayer(OpenXRLayer layer)
        {
            switch (layer.Target.Name)
            {
                case "background":
                    layer.Radius = BackgroundDistance;
                    layer.HorizontalAngle = BackgroundAngle;
                    break;

                default:
                    // Unknown layers sit just behind the panel's default position.
                    layer.Radius = PanelDistance + 0.01f;
                    layer.HorizontalAngle = PanelHorizontalAngle;
                    break;
            }

            layer.Width = 2 * layer.Radius * MathF.Tan(layer.HorizontalAngle / 2);
            layer.Position = new Vector3(0, 0, -layer.Radius);
        }

        private readonly IOpenXRPlatform platform;

        public OpenXRSession(IOpenXRPlatform platform, Size panelSize)
        {
            this.platform = platform;
            PanelSize = panelSize;

            xr = new XrFunctions(XrLoaderLibrary.Load());
            xr.Load(0);

            platform.InitialiseLoader(xr);
            createInstance();
            getSystem();
        }

        #region Instance

        private void createInstance()
        {
            var available = enumerateInstanceExtensions();
            Logger.Log($"OpenXR: available instance extensions ({available.Count}): {string.Join(' ', available.OrderBy(e => e))}");

            foreach (string ext in platform.RequiredExtensions)
            {
                if (!available.Contains(ext))
                    throw new InvalidOperationException($"The OpenXR runtime does not support required extension {ext}.");

                enabledExtensions.Add(ext);
            }

            foreach (string ext in optional_extensions)
            {
                if (available.Contains(ext))
                    enabledExtensions.Add(ext);
                else
                    Logger.Log($"OpenXR: optional extension {ext} is not available.");
            }

            var extensionNames = enabledExtensions.Select(e => Marshal.StringToCoTaskMemUTF8(e)).ToArray();
            void* instanceCreateNext = null;

            try
            {
                var createInfo = new XrInstanceCreateInfo
                {
                    type = XR_TYPE_INSTANCE_CREATE_INFO,
                    next = instanceCreateNext = platform.AllocateInstanceCreateNext(),
                    enabledExtensionCount = (uint)extensionNames.Length,
                };

                XrUtil.WriteString(createInfo.applicationInfo.applicationName, (int)XrConstants.XR_MAX_APPLICATION_NAME_SIZE, platform.ApplicationName);
                XrUtil.WriteString(createInfo.applicationInfo.engineName, (int)XrConstants.XR_MAX_ENGINE_NAME_SIZE, "osu!framework");
                createInfo.applicationInfo.applicationVersion = 1;
                createInfo.applicationInfo.engineVersion = 1;
                createInfo.applicationInfo.apiVersion = XrConstants.XR_API_VERSION_1_0;

                fixed (IntPtr* names = extensionNames)
                {
                    createInfo.enabledExtensionNames = (byte**)names;

                    ulong created;
                    xr.xrCreateInstance(&createInfo, &created).ThrowIfFailed(nameof(xr.xrCreateInstance));
                    instance = created;

                    // Instance-level (and extension) functions can only be resolved once the instance exists.
                    xr.Load(instance);
                }
            }
            finally
            {
                platform.Free(instanceCreateNext);

                foreach (var name in extensionNames)
                    Marshal.FreeCoTaskMem(name);
            }

            var properties = new XrInstanceProperties { type = XR_TYPE_INSTANCE_PROPERTIES };
            xr.xrGetInstanceProperties(instance, &properties).ThrowIfFailed(nameof(xr.xrGetInstanceProperties));

            ulong v = properties.runtimeVersion;
            RuntimeName = XrUtil.ReadString(properties.runtimeName);
            Logger.Log($"OpenXR: instance created on runtime \"{RuntimeName}\" {v >> 48}.{(v >> 32) & 0xFFFF}.{v & 0xFFFFFFFF}; "
                       + $"enabled extensions: {string.Join(' ', enabledExtensions)}");
        }

        private HashSet<string> enumerateInstanceExtensions()
        {
            uint count;
            xr.xrEnumerateInstanceExtensionProperties(null, 0, &count, null).ThrowIfFailed(nameof(xr.xrEnumerateInstanceExtensionProperties));

            var properties = new XrExtensionProperties[count];
            for (int i = 0; i < count; i++)
                properties[i].type = XR_TYPE_EXTENSION_PROPERTIES;

            fixed (XrExtensionProperties* p = properties)
            {
                xr.xrEnumerateInstanceExtensionProperties(null, count, &count, p).ThrowIfFailed(nameof(xr.xrEnumerateInstanceExtensionProperties));

                var names = new HashSet<string>();
                for (int i = 0; i < count; i++)
                    names.Add(XrUtil.ReadString(p[i].extensionName));
                return names;
            }
        }

        private void getSystem()
        {
            var getInfo = new XrSystemGetInfo
            {
                type = XR_TYPE_SYSTEM_GET_INFO,
                formFactor = XrFormFactor.XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY,
            };

            ulong id;
            xr.xrGetSystem(instance, &getInfo, &id).ThrowIfFailed(nameof(xr.xrGetSystem));
            systemId = id;

            var properties = new XrSystemProperties { type = XR_TYPE_SYSTEM_PROPERTIES };
            xr.xrGetSystemProperties(instance, systemId, &properties).ThrowIfFailed(nameof(xr.xrGetSystemProperties));

            Logger.Log($"OpenXR: system \"{XrUtil.ReadString(properties.systemName)}\" (vendor {properties.vendorId}), "
                       + $"max swapchain {properties.graphicsProperties.maxSwapchainImageWidth}x{properties.graphicsProperties.maxSwapchainImageHeight}, "
                       + $"max layers {properties.graphicsProperties.maxLayerCount}");
        }

        #endregion

        #region Session

        /// <summary>
        /// Creates the session on the EGL context that is current on the calling thread, along with the panel swapchain.
        /// </summary>
        public void CreateSession()
        {
            // Required by the spec before xrCreateSession.
            platform.CheckGraphicsRequirements(xr, instance, systemId);

            void* binding = platform.AllocateGraphicsBinding();

            var createInfo = new XrSessionCreateInfo
            {
                type = XR_TYPE_SESSION_CREATE_INFO,
                next = binding,
                systemId = systemId,
            };

            ulong created;

            try
            {
                xr.xrCreateSession(instance, &createInfo, &created).ThrowIfFailed(nameof(xr.xrCreateSession));
            }
            finally
            {
                platform.Free(binding);
            }

            session = created;

            var spaceInfo = new XrReferenceSpaceCreateInfo
            {
                type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO,
                referenceSpaceType = XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_LOCAL,
                poseInReferenceSpace = XrUtil.IdentityPose,
            };

            ulong space;
            xr.xrCreateReferenceSpace(session, &spaceInfo, &space).ThrowIfFailed(nameof(xr.xrCreateReferenceSpace));
            localSpace = space;

            if (enabledExtensions.Contains("XR_EXT_local_floor"))
            {
                var floorInfo = new XrReferenceSpaceCreateInfo
                {
                    type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO,
                    referenceSpaceType = XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_LOCAL_FLOOR_EXT,
                    poseInReferenceSpace = XrUtil.IdentityPose,
                };

                if (xr.xrCreateReferenceSpace(session, &floorInfo, &space).Succeeded())
                    localFloorSpace = space;
            }

            var viewSpaceInfo = new XrReferenceSpaceCreateInfo
            {
                type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO,
                referenceSpaceType = XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_VIEW,
                poseInReferenceSpace = XrUtil.IdentityPose,
            };

            xr.xrCreateReferenceSpace(session, &viewSpaceInfo, &space).ThrowIfFailed(nameof(xr.xrCreateReferenceSpace));
            viewSpace = space;

            long format = createPanelSwapchain();

            Input = new OpenXRInput(xr, instance, session, enabledExtensions);
            viewLocator = new OpenXRViewLocator(xr, session);

            try
            {
                controllerModels = new OpenXRControllerModels(xr, instance, session, enabledExtensions, DebugDirectory);
            }
            catch (Exception e)
            {
                Logger.Error(e, "OpenXR: controller models are unavailable; using the built-in model.");
            }
            pointerVisuals = new OpenXRPointerVisuals(xr, session, localSpace, format, platform.SwapchainImageType,
                createTextures: !enabledExtensions.Contains("XR_KHR_composition_layer_cylinder"));

            Logger.Log("OpenXR: session created.");
        }

        /// <returns>The swapchain format used.</returns>
        private long createPanelSwapchain()
        {
            bool canDisableSrgbWrites = platform.CanDisableSrgbWrites;
            swapchainFormat = OpenXRSwapchainTarget.ChooseFormat(xr, session, canDisableSrgbWrites);

            panel = new OpenXRSwapchainTarget(xr, session, PanelSize, swapchainFormat, platform.SwapchainImageType);

            // Store the game's gamma-encoded output untouched in sRGB swapchains (state is global to the context).
            if (swapchainFormat == OpenXRSwapchainTarget.GL_SRGB8_ALPHA8)
                GL.Disable(gl_framebuffer_srgb_ext);

            Logger.Log($"OpenXR: panel swapchain {PanelSize.Width}x{PanelSize.Height}, format 0x{swapchainFormat:X}");
            return swapchainFormat;
        }

        /// <summary>
        /// Processes pending runtime events. Must be called once per frame, before <see cref="BeginFrame"/>.
        /// </summary>
        public void PollEvents()
        {
            XrEventDataBuffer buffer;

            while (true)
            {
                buffer.type = XR_TYPE_EVENT_DATA_BUFFER;
                buffer.next = null;

                var result = xr.xrPollEvent(instance, &buffer);
                if (result == XrResult.XR_EVENT_UNAVAILABLE)
                    return;

                result.ThrowIfFailed(nameof(xr.xrPollEvent));

                switch (buffer.type)
                {
                    case XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED:
                        handleSessionState(((XrEventDataSessionStateChanged*)&buffer)->state);
                        break;

                    case XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING:
                        Logger.Log("OpenXR: instance loss pending.");
                        IsSessionRunning = false;
                        ExitRequested?.Invoke();
                        break;

                    case XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING:
                        var change = (XrEventDataReferenceSpaceChangePending*)&buffer;
                        Logger.Log($"OpenXR: reference space {change->referenceSpaceType} change pending (recenter).");

                        // The system recenter resets LOCAL's yaw and position (it stays level). Re-place the anchor from the head pose so
                        // pitch is honoured too: recentering while lying down brings the panel overhead.
                        if (change->referenceSpaceType == XrReferenceSpaceType.XR_REFERENCE_SPACE_TYPE_LOCAL)
                            Recenter();
                        break;

                    case XR_TYPE_EVENT_DATA_INTERACTION_PROFILE_CHANGED:
                    case XR_TYPE_EVENT_DATA_INTERACTION_RENDER_MODELS_CHANGED_EXT:
                        // A different controller may be in use: get its model.
                        controllerModels?.Invalidate();
                        break;

                    case XR_TYPE_EVENT_DATA_EVENTS_LOST:
                        Logger.Log($"OpenXR: {((XrEventDataEventsLost*)&buffer)->lostEventCount} events lost.");
                        break;
                }
            }
        }

        private void handleSessionState(XrSessionState newState)
        {
            Logger.Log($"OpenXR: session state {state} -> {newState}");

            state = newState;

            switch (newState)
            {
                case XrSessionState.XR_SESSION_STATE_READY:
                    var beginInfo = new XrSessionBeginInfo
                    {
                        type = XR_TYPE_SESSION_BEGIN_INFO,
                        primaryViewConfigurationType = XrViewConfigurationType.XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                    };

                    xr.xrBeginSession(session, &beginInfo).ThrowIfFailed(nameof(xr.xrBeginSession));
                    IsSessionRunning = true;
                    FrameWaited = false;

                    requestDisplayRefreshRate(PreferredRefreshRate);
                    break;

                case XrSessionState.XR_SESSION_STATE_STOPPING:
                    IsSessionRunning = false;
                    xr.xrEndSession(session).ThrowIfFailed(nameof(xr.xrEndSession));
                    break;

                case XrSessionState.XR_SESSION_STATE_EXITING:
                case XrSessionState.XR_SESSION_STATE_LOSS_PENDING:
                    IsSessionRunning = false;
                    ExitRequested?.Invoke();
                    break;
            }

            StateChanged?.Invoke(newState);
        }

        #endregion

        /// <summary>
        /// The display refresh rate to request when the session starts. The highest supported rate not exceeding this is used.
        /// </summary>
        public float PreferredRefreshRate { get; set; } = 120;

        private void requestDisplayRefreshRate(float preferred)
        {
            if (!enabledExtensions.Contains("XR_FB_display_refresh_rate") || xr.xrEnumerateDisplayRefreshRatesFB == null)
                return;

            try
            {
                uint count;
                xr.xrEnumerateDisplayRefreshRatesFB(session, 0, &count, null).ThrowIfFailed(nameof(xr.xrEnumerateDisplayRefreshRatesFB));

                float[] rates = new float[count];
                fixed (float* r = rates)
                    xr.xrEnumerateDisplayRefreshRatesFB(session, count, &count, r).ThrowIfFailed(nameof(xr.xrEnumerateDisplayRefreshRatesFB));

                // Rates are reported inexactly (e.g. 120.000004 Hz), so allow a little over.
                var candidates = rates.Where(r => r <= preferred + 0.5f).ToArray();

                if (candidates.Length == 0)
                {
                    Logger.Log($"OpenXR: display refresh rates [{string.Join(", ", rates)}]; none at or below {preferred} Hz, keeping the runtime's.");
                    return;
                }

                float chosen = candidates.Max();
                xr.xrRequestDisplayRefreshRateFB(session, chosen).ThrowIfFailed(nameof(xr.xrRequestDisplayRefreshRateFB));

                float current;
                xr.xrGetDisplayRefreshRateFB(session, &current).ThrowIfFailed(nameof(xr.xrGetDisplayRefreshRateFB));

                Logger.Log($"OpenXR: display refresh rates [{string.Join(", ", rates)}], requested {chosen} Hz (now {current} Hz).");
            }
            catch (XrException e)
            {
                // Not fatal: the runtime keeps its default rate.
                Logger.Log($"OpenXR: could not set display refresh rate: {e.Message}");
            }
        }

        #region Frames

        /// <summary>
        /// Blocks until the runtime wants the next frame, and samples controller input for that frame's predicted display time.
        /// </summary>
        /// <remarks>
        /// Call this before the game update which consumes the input, so input is as fresh as possible for the frame it affects.
        /// Must be followed by <see cref="BeginFrame"/> and <see cref="EndFrame"/> before it is called again
        /// (a second xrWaitFrame without xrBeginFrame would block forever on a single thread).
        /// </remarks>
        /// <returns>Whether controller input was sampled.</returns>
        public bool WaitFrame()
        {
            if (FrameWaited)
                throw new InvalidOperationException($"{nameof(WaitFrame)} called twice without {nameof(BeginFrame)}.");

            var waitInfo = new XrFrameWaitInfo { type = XR_TYPE_FRAME_WAIT_INFO };
            var newState = new XrFrameState { type = XR_TYPE_FRAME_STATE };
            xr.xrWaitFrame(session, &waitInfo, &newState).ThrowIfFailed(nameof(xr.xrWaitFrame));
            frameState = newState;
            FrameWaited = true;

            var head = new XrSpaceLocation { type = XR_TYPE_SPACE_LOCATION };
            xr.xrLocateSpace(viewSpace, localSpace, frameState.predictedDisplayTime, &head).ThrowIfFailed(nameof(xr.xrLocateSpace));

            const ulong head_valid = XrSpaceLocationFlags.XR_SPACE_LOCATION_POSITION_VALID_BIT | XrSpaceLocationFlags.XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;

            if ((head.locationFlags & head_valid) == head_valid)
            {
                HeadPosition = head.pose.position.ToNumerics();
                HeadOrientation = head.pose.orientation.ToNumerics();

                // In the head-locked debug mode everything is placed in VIEW space, so the anchor stays at the origin.
                if (recenterPending && !DebugHeadLockedPanel)
                {
                    placeAnchor(HeadPosition, HeadOrientation);
                    recenterPending = false;
                }
            }

            updatePanelGeometry();

            return Input?.Sync(localSpace, frameState.predictedDisplayTime) == true;
        }

        /// <summary>
        /// Begins the frame previously waited on (waiting first if needed).
        /// </summary>
        /// <param name="framebuffer">The GL framebuffer to render the panel into, if this returns <see langword="true"/>.</param>
        /// <returns>Whether the application should render this frame. <see cref="EndFrame"/> must be called either way.</returns>
        public bool BeginFrame(out int framebuffer)
        {
            framebuffer = 0;

            if (!FrameWaited)
                WaitFrame();

            var beginInfo = new XrFrameBeginInfo { type = XR_TYPE_FRAME_BEGIN_INFO };
            xr.xrBeginFrame(session, &beginInfo).ThrowIfFailed(nameof(xr.xrBeginFrame));

            if (frameState.shouldRender == 0)
                return false;

            framebuffer = panel!.Acquire();

            foreach (var layer in layers)
            {

                if (layer.Images == null)
                {
                    layer.Images = new OpenXRSwapchainTarget(xr, session, PanelSize, swapchainFormat, platform.SwapchainImageType);
                    Logger.Log($"OpenXR: created layer \"{layer.Target.Name}\"");
                }

                // In passthrough the room is the background: nothing is drawn into the background layer, rather than drawing it (with
                // everything in it, e.g. a storyboard) only to leave it out of the frame.
                layer.Target.Hidden = usePassthrough && layer.Target.Name == "background";

                if (layer.Target.Hidden)
                {
                    layer.Target.NativeFramebuffer = null;
                    continue;
                }

                layer.Target.NativeFramebuffer = layer.Images.Acquire();
            }

            return true;
        }

        /// <summary>
        /// Releases the panel image (if acquired) and submits the frame's composition layers.
        /// </summary>
        public void EndFrame()
        {
            panel?.Release();

            FrameWaited = false;

            var space = DebugHeadLockedPanel ? viewSpace : localSpace;

            // Room for: passthrough, layers behind the panel, the panel, pointer visuals.
            int capacity = 1 + layers.Count + 1 + OpenXRPointerVisuals.MAX_LAYERS + 1;
            var layerPointers = stackalloc XrCompositionLayerBaseHeader*[capacity];
            var quads = stackalloc XrCompositionLayerQuad[layers.Count + 1];
            var spheres = stackalloc XrCompositionLayerEquirect2KHR[layers.Count + 1];
            uint layerCount = 0;
            int surfaceCount = 0;
            bool curvedBackground = CurvedBackground;
            var cylinders = stackalloc XrCompositionLayerCylinderKHR[layers.Count + 1];
            int cylinderCount = 0;
            updatePanelGeometry();

            XrCompositionLayerPassthroughFB passthroughLayer = default;
            updatePassthrough();

            XrCompositionLayerProjection projectionLayer;
            var projectionViews = stackalloc XrCompositionLayerProjectionView[2];
            XrCompositionLayerProjection sceneLayer;
            var sceneViews = stackalloc XrCompositionLayerProjectionView[2];
            bool environmentRendered = useEnvironmentRenderer && environmentRenderer?.HasRendered == true;
            bool fieldRendered = useField && environmentRendered;

            // Whether the scene's layer shows the game's images and the environment this frame (see drawSurfacesInScene), so they
            // aren't submitted here.
            bool surfacesInScene = drawSurfacesInScene && sceneRenderer?.HasRendered == true;

            if (environmentRendered && !surfacesInScene)
            {
                environmentRenderer!.FillLayer(&projectionLayer, projectionViews, space);
                layerPointers[layerCount++] = (XrCompositionLayerBaseHeader*)&projectionLayer;
            }

            if (usePassthrough && passthrough != null)
            {
                passthroughLayer = passthrough.Layer;
                layerPointers[layerCount++] = (XrCompositionLayerBaseHeader*)&passthroughLayer;
            }

            // Layers behind the panel, in the order they were added. Only those which received content
            // this frame are shown: an image nothing was drawn into wasn't cleared and holds stale content.
            foreach (var layer in layers)
            {
                bool hasContent = layer.Images != null && layer.Target.HasContent;

                layer.Images?.Release();
                layer.Target.NativeFramebuffer = null;

                if (!hasContent)
                    continue;

                // In passthrough the room is the background; in the field it's shown on the wall.
                if ((usePassthrough || fieldRendered) && layer.Target.Name == "background")
                    continue;

                if (surfacesInScene)
                    continue;

                placeLayer(layer);

                layerPointers[layerCount++] = backgroundSurface(layer.Images!.SubImage, XrCompositionLayerFlags.XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT,
                    layer.Position, layer.Width, layer.Radius, layer.HorizontalAngle);
            }

            if (panel?.HasReleasedImage == true)
            {
                // With layers behind it the panel is cleared to transparent, so it must be alpha-blended over them.
                ulong flags = layers.Count > 0 ? XrCompositionLayerFlags.XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT : 0;
                if (!surfacesInScene)
                    layerPointers[layerCount++] = panelSurface(Panel, panel.SubImage, flags);

                if (pointerVisuals != null && Input != null && !surfacesInScene)
                {
                    // A held model's ray comes out of its tip; a controller's starts a little ahead of it, clear of the model.
                    float laserStart = holdingModel ? 0 : 0.02f;
                    layerCount += (uint)pointerVisuals.BuildLayers(Input.Hands, PointerActiveHand, HeadPosition, this, PointerEdgeTolerance, laserStart, layerPointers + layerCount);
                }
            }

            // Controllers on top of everything: one held in front of the panel must never be hidden by it.
            if ((ShowControllers || surfacesInScene) && sceneRenderer?.HasRendered == true)
            {
                sceneRenderer.FillLayer(&sceneLayer, sceneViews, localSpace);
                layerPointers[layerCount++] = (XrCompositionLayerBaseHeader*)&sceneLayer;
            }

            // The panel: a cylinder section around the viewer (or a flat quad as fallback), placed by Panel.
            XrCompositionLayerBaseHeader* panelSurface(PanelGeometry geometry, XrSwapchainSubImage image, ulong layerFlags)
            {
                var orientation = new XrQuaternionf { x = geometry.Orientation.X, y = geometry.Orientation.Y, z = geometry.Orientation.Z, w = geometry.Orientation.W };

                if (geometry.Cylinder)
                {
                    int cylinder = cylinderCount++;

                    cylinders[cylinder] = new XrCompositionLayerCylinderKHR
                    {
                        type = XR_TYPE_COMPOSITION_LAYER_CYLINDER_KHR,
                        layerFlags = layerFlags,
                        space = space,
                        eyeVisibility = XrEyeVisibility.XR_EYE_VISIBILITY_BOTH,
                        subImage = image,
                        pose = new XrPosef { orientation = orientation, position = geometry.Centre.ToXr() },
                        radius = geometry.Radius,
                        centralAngle = geometry.HorizontalAngle,
                        aspectRatio = geometry.Aspect,
                    };

                    return (XrCompositionLayerBaseHeader*)&cylinders[cylinder];
                }

                int index = surfaceCount++;

                quads[index] = new XrCompositionLayerQuad
                {
                    type = XR_TYPE_COMPOSITION_LAYER_QUAD,
                    layerFlags = layerFlags,
                    space = space,
                    eyeVisibility = XrEyeVisibility.XR_EYE_VISIBILITY_BOTH,
                    subImage = image,
                    pose = new XrPosef { orientation = orientation, position = geometry.FromPanelSpace(new Vector3(0, 0, -geometry.Radius)).ToXr() },
                    size = new XrExtent2Df { width = geometry.FlatWidth, height = geometry.Height },
                };

                return (XrCompositionLayerBaseHeader*)&quads[index];
            }

            // Layers behind the panel: a sphere section around the anchor (or a flat quad), showing a whole swapchain image.
            XrCompositionLayerBaseHeader* backgroundSurface(XrSwapchainSubImage image, ulong layerFlags, Vector3 flatCentre, float flatWidth, float radius, float horizontalAngle)
            {
                int index = surfaceCount++;

                if (curvedBackground)
                {
                    float verticalAngle = horizontalAngle * PanelSize.Height / PanelSize.Width;

                    spheres[index] = new XrCompositionLayerEquirect2KHR
                    {
                        type = XR_TYPE_COMPOSITION_LAYER_EQUIRECT2_KHR,
                        layerFlags = layerFlags,
                        space = space,
                        eyeVisibility = XrEyeVisibility.XR_EYE_VISIBILITY_BOTH,
                        subImage = image,
                        pose = anchoredPose(Vector3.Zero),
                        radius = radius,
                        centralHorizontalAngle = horizontalAngle,
                        upperVerticalAngle = verticalAngle / 2,
                        lowerVerticalAngle = -verticalAngle / 2,
                    };

                    return (XrCompositionLayerBaseHeader*)&spheres[index];
                }

                quads[index] = new XrCompositionLayerQuad
                {
                    type = XR_TYPE_COMPOSITION_LAYER_QUAD,
                    layerFlags = layerFlags,
                    space = space,
                    eyeVisibility = XrEyeVisibility.XR_EYE_VISIBILITY_BOTH,
                    subImage = image,
                    pose = anchoredPose(flatCentre),
                    size = new XrExtent2Df { width = flatWidth, height = flatWidth * PanelSize.Height / PanelSize.Width },
                };

                return (XrCompositionLayerBaseHeader*)&quads[index];
            }

            var endInfo = new XrFrameEndInfo
            {
                type = XR_TYPE_FRAME_END_INFO,
                displayTime = frameState.predictedDisplayTime,
                environmentBlendMode = XrEnvironmentBlendMode.XR_ENVIRONMENT_BLEND_MODE_OPAQUE,
                layerCount = layerCount,
                layers = layerPointers,
            };

            var textureBindings = GLTextureBindings.Capture();
            // SteamVR copies the submitted GL images on this context in xrEndFrame, with whatever scissor is set: the framework's
            // (the size of the panel) would cut off the rest of larger images.
            bool scissor = GL.IsEnabled(EnableCap.ScissorTest);
            if (scissor) GL.Disable(EnableCap.ScissorTest);

            xr.xrEndFrame(session, &endInfo).ThrowIfFailed(nameof(xr.xrEndFrame));

            if (scissor) GL.Enable(EnableCap.ScissorTest);
            textureBindings.Restore();

            updateBoundaryVisibility();
        }

        /// <summary>
        /// Requests the boundary visibility matching <see cref="SuppressBoundaryInPassthrough"/>. Called after a frame was submitted, as
        /// suppression is only allowed once a passthrough layer is being shown.
        /// </summary>
        private void updateBoundaryVisibility()
        {
            if (!enabledExtensions.Contains("XR_META_boundary_visibility") || xr.xrRequestBoundaryVisibilityMETA == null)
                return;

            bool suppress = SuppressBoundaryInPassthrough && usePassthrough && passthrough != null;

            if (suppress == boundarySuppressed)
                return;

            // A refused request (not allowed yet, or at all) is retried now and then rather than every frame.
            if (boundaryRequestCooldown > 0)
            {
                boundaryRequestCooldown--;
                return;
            }

            var result = xr.xrRequestBoundaryVisibilityMETA(session, suppress
                ? XrBoundaryVisibilityMETA.XR_BOUNDARY_VISIBILITY_SUPPRESSED_META
                : XrBoundaryVisibilityMETA.XR_BOUNDARY_VISIBILITY_NOT_SUPPRESSED_META);

            if (result == XrResult.XR_SUCCESS)
            {
                boundarySuppressed = suppress;
                Logger.Log($"OpenXR: boundary {(suppress ? "hidden" : "shown")}.");
            }
            else
            {
                boundaryRequestCooldown = 120;
                Logger.Log($"OpenXR: boundary visibility request refused ({result}).");
            }
        }

        private void updatePassthrough()
        {
            if (!enabledExtensions.Contains("XR_FB_passthrough"))
                return;

            try
            {
                if (usePassthrough)
                {
                    passthrough ??= new OpenXRPassthrough(xr, session);
                    passthrough.SetRunning(true);
                    passthrough.SetOpacity(PassthroughOpacity);
                }
                else
                    passthrough?.SetRunning(false);
            }
            catch (XrException e)
            {
                // Not fatal: fall back to the void.
                Logger.Error(e, "OpenXR: passthrough failed; using the void environment.");
                Environment = XrEnvironmentMode.Void;
            }
        }

        /// <summary>
        /// A pose at <paramref name="anchorPoint"/> (in the anchor's frame) with the anchor's orientation, in the LOCAL space.
        /// In the head-locked debug mode the anchor frame is the VIEW space itself.
        /// </summary>
        private XrPosef anchoredPose(Vector3 anchorPoint)
        {
            Vector3 position = DebugHeadLockedPanel ? anchorPoint : FromAnchorSpace(anchorPoint);
            Quaternion orientation = DebugHeadLockedPanel ? Quaternion.Identity : AnchorOrientation;

            return new XrPosef
            {
                orientation = new XrQuaternionf { x = orientation.X, y = orientation.Y, z = orientation.Z, w = orientation.W },
                position = new XrVector3f { x = position.X, y = position.Y, z = position.Z },
            };
        }

        #endregion

        public void Dispose()
        {
            Input?.Dispose();
            Input = null;

            pointerVisuals?.Dispose();
            pointerVisuals = null;
            sceneRenderer?.Dispose();
            sceneRenderer = null;
            controllerModels?.Dispose();
            controllerModels = null;

            if (viewSpace != 0) xr.xrDestroySpace(viewSpace);
            viewSpace = 0;

            if (localFloorSpace != 0) xr.xrDestroySpace(localFloorSpace);
            localFloorSpace = 0;

            foreach (var layer in layers)
                layer.Images?.Dispose();

            panel?.Dispose();
            panel = null;

            passthrough?.Dispose();
            passthrough = null;

            environmentRenderer?.Dispose();
            environmentRenderer = null;

            if (localSpace != 0) xr.xrDestroySpace(localSpace);
            if (session != 0) xr.xrDestroySession(session);
            if (instance != 0) xr.xrDestroyInstance(instance);

            localSpace = session = instance = 0;
        }
    }
}
