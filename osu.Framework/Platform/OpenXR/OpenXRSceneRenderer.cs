// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using osuTK.Graphics.ES30;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Draws what the game shows in 3D into a stereo projection layer composited above the panel and the laser beams: models of the
    /// motion controllers where they're held (so a controller held in front of the panel is never hidden by it).
    /// For runtimes which can't composite the game's other layers properly with this one (SteamVR), it also draws those underneath the
    /// controllers, as the whole scene in a single layer: the environment, the game's images (see <see cref="Surface"/>) and the pointer.
    /// </summary>
    /// <remarks>
    /// The built-in controller models are made of a few primitives (no assets), lit simply, with accents that light up while the
    /// trigger and grip are pressed. The active controller's accent glows brighter. With only controllers to show the layer is mostly
    /// empty, so mobile platforms render it at reduced resolution, and it's skipped entirely while neither controller is in view;
    /// silhouettes fade out over their last few degrees, which hides the jagged edges a reduced resolution would otherwise show.
    /// Shares the game's GL context, so all GL state it touches is saved and restored.
    /// </remarks>
    internal sealed unsafe class OpenXRSceneRenderer : IDisposable
    {
        private const float near_plane = 0.03f;
        private const float far_plane = 20f;

        private readonly XrFunctions xr;
        private readonly ulong session;
        private readonly OpenXRSwapchainTarget[] eyes = new OpenXRSwapchainTarget[2];
        private readonly XrView[] views = new XrView[2];

        private readonly int program;
        private readonly int vertexArray;
        private readonly int vertexBuffer;
        private readonly int indexBuffer;

        private readonly int uViewProjection, uModel, uNormal, uViewPos, uBase, uEmissive;

        // Models provided by the runtime (see OpenXRControllerModels): textured, with their own GPU resources.
        private readonly int texturedProgram;
        private readonly int tViewProjection, tModel, tNormal, tViewPos, tBase, tEmissive, tRim, tTexture;
        private readonly int whiteTexture;
        private readonly Dictionary<GltfModel, GpuModel> gpuModels = new Dictionary<GltfModel, GpuModel>();
        private readonly List<GltfModel> released = new List<GltfModel>();

        /// <summary>
        /// The colour used for parts whose texture couldn't be decoded (e.g. KTX2): neutral dark plastic.
        /// </summary>
        private static readonly Vector4 untextured_colour = new Vector4(0.12f, 0.125f, 0.135f, 1);

        private readonly Mesh sphere;
        private readonly Mesh cylinder;

        // Surfaces showing game images (see Surface): a strip of quads, rebuilt for every surface drawn.
        private const int surface_segments = 48;
        private const int surface_vertex_floats = 5;
        private const int surface_sphere_bands = 16;
        private readonly int surfaceProgram;
        private readonly int sViewProjection, sTexture, sOpaque;
        private readonly int surfaceVertexArray;
        private readonly int surfaceVertexBuffer;
        private readonly float[] surfaceVertices = new float[(surface_segments + 1) * 2 * surface_vertex_floats];

        /// <summary>
        /// A surface showing a game image, drawn behind the controllers. For runtimes which can't composite the game's layers in order
        /// with this one (SteamVR shows quad layers as overlays, in front of every projection layer).
        /// </summary>
        public struct Surface
        {
            /// <summary>
            /// The GL texture to show (bottom-up, premultiplied alpha, gamma-encoded).
            /// </summary>
            public int Texture;

            /// <summary>
            /// Where the surface is, in the space passed to <see cref="Render"/>. Null for a flat quad given by the Quad fields.
            /// </summary>
            public PanelGeometry? Geometry;

            /// <summary>
            /// A flat quad's centre, orientation (it lies in its XY plane) and size in metres. Used without a <see cref="Geometry"/>.
            /// </summary>
            public Vector3 QuadCentre;

            public Quaternion QuadOrientation;
            public Vector2 QuadSize;

            /// <summary>
            /// When above zero, the surface is a section of a sphere of this radius instead of a flat quad: centred on
            /// <see cref="QuadCentre"/>, facing along -Z of <see cref="QuadOrientation"/>, covering the angles (radians) in
            /// <see cref="QuadSize"/> horizontally and vertically.
            /// </summary>
            public float SphereRadius;

            /// <summary>
            /// Whether the image's alpha is ignored.
            /// </summary>
            public bool Opaque;
        }

        /// <summary>
        /// Whether the last <see cref="Render"/> drew anything (the layer should only be submitted if so).
        /// </summary>
        public bool HasRendered { get; private set; }

        private OpenXRSceneRenderer(XrFunctions xr, ulong session, long format, XrStructureType imageType, Size eyeSize)
        {
            this.xr = xr;
            this.session = session;

            var state = GLStateSnapshot.Capture();

            for (int i = 0; i < 2; i++)
                eyes[i] = new OpenXRSwapchainTarget(xr, session, eyeSize, format, imageType);

            program = createProgram(vertex_shader, fragment_shader);
            texturedProgram = createProgram(textured_vertex_shader, textured_fragment_shader);
            tViewProjection = GL.GetUniformLocation(texturedProgram, "u_viewProjection");
            tModel = GL.GetUniformLocation(texturedProgram, "u_model");
            tNormal = GL.GetUniformLocation(texturedProgram, "u_normal");
            tViewPos = GL.GetUniformLocation(texturedProgram, "u_viewPos");
            tBase = GL.GetUniformLocation(texturedProgram, "u_base");
            tEmissive = GL.GetUniformLocation(texturedProgram, "u_emissive");
            tRim = GL.GetUniformLocation(texturedProgram, "u_rim");
            tTexture = GL.GetUniformLocation(texturedProgram, "u_texture");

            surfaceProgram = createProgram(surface_vertex_shader, surface_fragment_shader);
            sViewProjection = GL.GetUniformLocation(surfaceProgram, "u_viewProjection");
            sTexture = GL.GetUniformLocation(surfaceProgram, "u_texture");
            sOpaque = GL.GetUniformLocation(surfaceProgram, "u_opaque");

            surfaceVertexArray = GL.GenVertexArray();
            GL.BindVertexArray(surfaceVertexArray);
            surfaceVertexBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, surfaceVertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(surfaceVertices.Length * sizeof(float)), IntPtr.Zero, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, surface_vertex_floats * sizeof(float), IntPtr.Zero);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, surface_vertex_floats * sizeof(float), (IntPtr)(3 * sizeof(float)));

            whiteTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, whiteTexture);
            var white = stackalloc byte[] { 255, 255, 255, 255 };
            GL.TexImage2D(TextureTarget2d.Texture2D, 0, TextureComponentCount.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)white);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            uViewProjection = GL.GetUniformLocation(program, "u_viewProjection");
            uModel = GL.GetUniformLocation(program, "u_model");
            uNormal = GL.GetUniformLocation(program, "u_normal");
            uViewPos = GL.GetUniformLocation(program, "u_viewPos");
            uBase = GL.GetUniformLocation(program, "u_base");
            uEmissive = GL.GetUniformLocation(program, "u_emissive");

            // One buffer for all primitives: interleaved position + normal.
            var vertices = new List<float>();
            var indices = new List<ushort>();
            sphere = buildSphere(vertices, indices, 24, 16);
            cylinder = buildCylinder(vertices, indices, 24);

            vertexArray = GL.GenVertexArray();
            GL.BindVertexArray(vertexArray);

            vertexBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
            float[] vertexData = vertices.ToArray();
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(vertexData.Length * sizeof(float)), vertexData, BufferUsageHint.StaticDraw);

            indexBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
            ushort[] indexData = indices.ToArray();
            GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(indexData.Length * sizeof(ushort)), indexData, BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), IntPtr.Zero);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (IntPtr)(3 * sizeof(float)));

            state.Restore();

            Logger.Log($"OpenXR: scene renderer created ({eyeSize.Width}x{eyeSize.Height} per eye)");
        }

        /// <summary>
        /// Creates the renderer, sizing its per-eye images from the runtime's recommendation scaled by <paramref name="resolutionScale"/>.
        /// Must be called on the GL thread.
        /// </summary>
        public static OpenXRSceneRenderer Create(XrFunctions xr, ulong instance, ulong systemId, ulong session, long format, XrStructureType imageType,
                                                      float resolutionScale, OpenXRViewLocator viewLocator)
        {
            var configViews = stackalloc XrViewConfigurationView[2];
            configViews[0] = new XrViewConfigurationView { type = XR_TYPE_VIEW_CONFIGURATION_VIEW };
            configViews[1] = new XrViewConfigurationView { type = XR_TYPE_VIEW_CONFIGURATION_VIEW };

            uint count;
            xr.xrEnumerateViewConfigurationViews(instance, systemId, XrViewConfigurationType.XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2, &count, configViews)
              .ThrowIfFailed(nameof(xr.xrEnumerateViewConfigurationViews));

            var size = new Size(
                Math.Max(64, (int)(configViews[0].recommendedImageRectWidth * resolutionScale)),
                Math.Max(64, (int)(configViews[0].recommendedImageRectHeight * resolutionScale)));

            return new OpenXRSceneRenderer(xr, session, format, imageType, size) { ViewLocator = viewLocator };
        }

        /// <summary>
        /// Renders the frame: the environment and surfaces given, and the controllers if any is tracked and in view.
        /// </summary>
        /// <param name="space">The base space the views and controller poses are in (and the layer is submitted in).</param>
        /// <param name="displayTime">The frame's predicted display time.</param>
        /// <param name="hands">Controller states sampled for this frame.</param>
        /// <param name="activeHand">The controller driving the pointer.</param>
        /// <param name="accent">The accent colour (gamma-encoded RGB).</param>
        /// <param name="models">Models of the actual controllers, posed for this frame. Hands without one get the built-in model.</param>
        /// <param name="environment">An environment prepared for this frame to draw behind everything else, or null.</param>
        /// <param name="surfaces">Game images to draw behind the controllers, back to front.</param>
        /// <param name="showControllers">Whether the controllers are drawn (otherwise only the surfaces).</param>
        public void Render(ulong space, long displayTime, XrHandState[] hands, int activeHand, Vector3 accent, OpenXRControllerModels? models,
                           ReadOnlySpan<Surface> surfaces = default, bool showControllers = true, OpenXREnvironmentRenderer? environment = null)
        {
            HasRendered = false;

            // Surfaces are always drawn: they're the game itself.
            bool anyVisible = false;
            bool viewsLocated = false;

            if (surfaces.Length > 0 || environment != null)
            {
                if (!locateViews(space, displayTime))
                    return;

                anyVisible = viewsLocated = true;
            }

            for (int hand = 0; showControllers && hand < hands.Length && !anyVisible; hand++)
            {
                if (!tracked(hand, hands, models, out var position))
                    continue;

                if (!viewsLocated)
                {
                    if (!locateViews(space, displayTime))
                        return;

                    viewsLocated = true;
                }

                anyVisible = isInView(views[0], position) || isInView(views[1], position);
            }

            if (!anyVisible)
                return;

            var state = GLStateSnapshot.Capture();

            try
            {
                GL.UseProgram(program);
                GL.BindVertexArray(vertexArray);
                GL.Disable(EnableCap.ScissorTest);
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Less);
                GL.DepthMask(true);
                GL.Enable(EnableCap.CullFace);
                GL.CullFace(CullFaceMode.Back);
                GL.FrontFace(FrontFaceDirection.Ccw);
                GL.Enable(EnableCap.Blend);
                GL.BlendEquation(BlendEquationMode.FuncAdd);
                GL.BlendFunc(BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
                GL.ColorMask(true, true, true, true);
                GL.ClearColor(0, 0, 0, 0);
                GL.ClearDepth(1);
                GL.ActiveTexture(TextureUnit.Texture0);

                releaseGpuModels();

                for (int eye = 0; eye < 2; eye++)
                    renderEye(eye, hands, activeHand, accent, models, surfaces, showControllers, environment);

                HasRendered = true;
            }
            finally
            {
                state.Restore();
            }
        }

        private static bool tracked(int hand, XrHandState[] hands, OpenXRControllerModels? models, out Vector3 position)
        {
            if (models?.Hands[hand] is { Visible: true } model)
            {
                position = model.Root.Translation;
                return true;
            }

            position = hands[hand].GripPosition;
            return hands[hand].GripValid;
        }

        /// <summary>
        /// Locates the views for each frame, shared with the frame's other projection layers.
        /// </summary>
        public required OpenXRViewLocator ViewLocator { get; init; }

        private bool locateViews(ulong space, long displayTime) => ViewLocator.Locate(space, displayTime, views);

        private static bool isInView(in XrView view, Vector3 point)
        {
            // In view space, with a margin for the model's size.
            var orientation = view.pose.orientation.ToNumerics();
            Vector3 p = Vector3.Transform(point - view.pose.position.ToNumerics(), Quaternion.Inverse(orientation));

            float depth = -p.Z;
            if (depth < -0.15f)
                return false;

            float margin = 0.15f;
            float d = MathF.Max(depth, 0.01f);
            return p.X > MathF.Tan(view.fov.angleLeft) * d - margin && p.X < MathF.Tan(view.fov.angleRight) * d + margin
                                                                   && p.Y > MathF.Tan(view.fov.angleDown) * d - margin && p.Y < MathF.Tan(view.fov.angleUp) * d + margin;
        }

        private void renderEye(int eye, XrHandState[] hands, int activeHand, Vector3 accent, OpenXRControllerModels? models, ReadOnlySpan<Surface> surfaces,
                               bool showControllers, OpenXREnvironmentRenderer? environment)
        {
            var target = eyes[eye];
            int framebuffer = target.Acquire();

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.Viewport(0, 0, target.Size.Width, target.Size.Height);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            ref readonly var view = ref views[eye];
            Vector3 eyePosition = view.pose.position.ToNumerics();
            Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(view.pose.orientation.ToNumerics()) * Matrix4x4.CreateTranslation(eyePosition), out var viewMatrix);
            var viewProjection = viewMatrix * projection(view.fov);

            // The whole scene in one layer, back to front: SteamVR only keeps its first projection layer steady as the head moves.
            environment?.DrawInto(view);

            if (surfaces.Length > 0)
                drawSurfaces(surfaces, viewProjection);

            for (int hand = 0; showControllers && hand < hands.Length; hand++)
            {
                if (models?.Hands[hand] is { Visible: true } model)
                {
                    GL.UseProgram(texturedProgram);
                    setMatrix(tViewProjection, viewProjection);
                    GL.Uniform3(tViewPos, eyePosition.X, eyePosition.Y, eyePosition.Z);
                    drawModel(model, hand == activeHand, accent);
                }
                else if (hands[hand].GripValid)
                {
                    GL.UseProgram(program);
                    GL.BindVertexArray(vertexArray);
                    setMatrix(uViewProjection, viewProjection);
                    GL.Uniform3(uViewPos, eyePosition.X, eyePosition.Y, eyePosition.Z);
                    drawController(hands[hand], hand == activeHand, accent);
                }
            }

            // Only the colour matters; let tiled GPUs skip storing the depth/stencil attachment.
            var discard = stackalloc FramebufferAttachment[] { FramebufferAttachment.DepthStencilAttachment };
            GL.InvalidateFramebuffer(FramebufferTarget.Framebuffer, 1, discard);

            target.Release();
        }

        /// <summary>
        /// Draws the game's surfaces back to front, without depth: the controllers are always shown in front of them.
        /// </summary>
        private void drawSurfaces(ReadOnlySpan<Surface> surfaces, Matrix4x4 viewProjection)
        {
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.UseProgram(surfaceProgram);
            GL.BindVertexArray(surfaceVertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, surfaceVertexBuffer);
            setMatrix(sViewProjection, viewProjection);
            GL.Uniform1(sTexture, 0);

            foreach (ref readonly var surface in surfaces)
            {
                var geometry = surface.Geometry;

                GL.BindTexture(TextureTarget.Texture2D, surface.Texture);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                // The game's output is gamma-encoded in an sRGB image, and is stored the same way here: sample the stored values as-is.
                const TextureParameterName texture_srgb_decode_ext = (TextureParameterName)0x8A48;
                const int skip_decode_ext = 0x8A4A;
                GL.TexParameter(TextureTarget.Texture2D, texture_srgb_decode_ext, skip_decode_ext);

                GL.Uniform1(sOpaque, surface.Opaque ? 1f : 0f);

                // One strip per band of rows. Only a sphere section curves vertically.
                int bands = surface.SphereRadius > 0 ? surface_sphere_bands : 1;

                for (int band = 0; band < bands; band++)
                {
                    int n = 0;

                    for (int i = 0; i <= surface_segments; i++)
                    {
                        float u = (float)i / surface_segments;

                        for (int row = 0; row < 2; row++)
                        {
                            float v = (float)(band + row) / bands;
                            Vector3 p;

                            if (geometry != null)
                                p = geometry.FromPanelSpace(geometry.SurfacePointLocal(new Vector2(u, v)));
                            else if (surface.SphereRadius > 0)
                            {
                                float yaw = (u - 0.5f) * surface.QuadSize.X;
                                float pitch = (0.5f - v) * surface.QuadSize.Y;
                                var local = new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), -MathF.Cos(pitch) * MathF.Cos(yaw)) * surface.SphereRadius;
                                p = surface.QuadCentre + Vector3.Transform(local, surface.QuadOrientation);
                            }
                            else
                                p = surface.QuadCentre + Vector3.Transform(new Vector3((u - 0.5f) * surface.QuadSize.X, (0.5f - v) * surface.QuadSize.Y, 0), surface.QuadOrientation);

                            surfaceVertices[n++] = p.X;
                            surfaceVertices[n++] = p.Y;
                            surfaceVertices[n++] = p.Z;
                            surfaceVertices[n++] = u;
                            // Game images are bottom-up in GL.
                            surfaceVertices[n++] = 1 - v;
                        }
                    }

                    fixed (float* vertices = surfaceVertices)
                        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, (IntPtr)(surfaceVertices.Length * sizeof(float)), (IntPtr)vertices);

                    GL.DrawArrays(PrimitiveType.TriangleStrip, 0, (surface_segments + 1) * 2);
                }
            }

            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
        }

        /// <summary>
        /// Draws one controller. Grip space: the handle runs along Z (-Z towards the thumb, where the head with the thumbstick is) and +Y
        /// points forward out of the fist (where the trigger is).
        /// </summary>
        private void drawController(in XrHandState hand, bool active, Vector3 accent)
        {
            var grip = Matrix4x4.CreateFromQuaternion(hand.GripOrientation) * Matrix4x4.CreateTranslation(hand.GripPosition);

            var body = new Vector3(0.085f, 0.09f, 0.10f);
            var bodyDark = new Vector3(0.05f, 0.055f, 0.065f);
            float accentLevel = active ? 0.85f : 0.3f;

            // Handle: a tapered capsule along Z.
            draw(cylinder, part(new Vector3(0.016f, 0.019f, 0.085f), Quaternion.Identity, new Vector3(0, 0, 0.005f)) * grip, body, Vector3.Zero);
            draw(sphere, part(new Vector3(0.016f, 0.019f, 0.012f), Quaternion.Identity, new Vector3(0, 0, 0.0475f)) * grip, body, Vector3.Zero);

            // Head: a flattened ellipsoid on top of the handle, reaching forward over the trigger and tilted towards the thumb.
            var headTilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f);
            var headCentre = new Vector3(0, 0.012f, -0.052f);
            draw(sphere, part(new Vector3(0.031f, 0.039f, 0.016f), headTilt, headCentre) * grip, body, Vector3.Zero);

            // Accent band around the head.
            draw(cylinder, part(new Vector3(0.0318f, 0.0398f, 0.006f), headTilt, headCentre) * grip, bodyDark, accent * accentLevel);

            // Thumbstick on the head's top face, leaning with the stick.
            var stickTilt = headTilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, hand.Thumbstick.X * 0.35f)
                                     * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -hand.Thumbstick.Y * 0.35f);
            var stickBase = headCentre + Vector3.Transform(new Vector3(0, -0.004f, -0.016f), headTilt);
            draw(cylinder, part(new Vector3(0.004f, 0.004f, 0.012f), stickTilt, stickBase + Vector3.Transform(new Vector3(0, 0, -0.004f), stickTilt)) * grip,
                bodyDark, Vector3.Zero);
            draw(sphere, part(new Vector3(0.008f, 0.008f, 0.003f), stickTilt, stickBase + Vector3.Transform(new Vector3(0, 0, -0.0105f), stickTilt)) * grip,
                body * 1.4f, Vector3.Zero);

            // Trigger: under the head at the front, pulled in and lit as it's pressed.
            float trigger = Math.Clamp(hand.Trigger, 0, 1);
            draw(sphere, part(new Vector3(0.0075f, 0.009f, 0.015f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.5f - 0.25f * trigger),
                new Vector3(0, 0.03f - 0.005f * trigger, -0.03f)) * grip, bodyDark, accent * (0.08f + 0.9f * trigger));

            // Grip button: along the front of the handle, lit as it's squeezed.
            float squeeze = Math.Clamp(hand.Squeeze, 0, 1);
            draw(sphere, part(new Vector3(0.011f, 0.007f, 0.024f), Quaternion.Identity, new Vector3(0, 0.0165f - 0.002f * squeeze, 0.008f)) * grip,
                bodyDark, accent * (0.05f + 0.8f * squeeze));
        }

        #region Runtime-provided models

        /// <summary>
        /// GPU resources of a <see cref="GltfModel"/>.
        /// </summary>
        private sealed class GpuModel
        {
            public readonly List<(int mesh, int vertexArray, int vertexBuffer, int indexBuffer, int indexCount, int material)> Primitives = new();
            public readonly int[] Textures;

            public GpuModel(int imageCount)
            {
                Textures = new int[imageCount];
            }
        }

        /// <summary>
        /// Releases the GPU resources of a model that's no longer used (at the next render).
        /// </summary>
        public void Release(GltfModel model) => released.Add(model);

        private void releaseGpuModels()
        {
            foreach (var model in released)
            {
                if (!gpuModels.Remove(model, out var gpu))
                    continue;

                foreach (var p in gpu.Primitives)
                {
                    GL.DeleteVertexArray(p.vertexArray);
                    GL.DeleteBuffer(p.vertexBuffer);
                    GL.DeleteBuffer(p.indexBuffer);
                }

                foreach (int texture in gpu.Textures)
                {
                    if (texture != 0)
                        GL.DeleteTexture(texture);
                }
            }

            released.Clear();
        }

        private GpuModel upload(GltfModel model)
        {
            var gpu = new GpuModel(model.Images.Count);

            for (int i = 0; i < model.Images.Count; i++)
            {
                if (model.Images[i] is not { } image)
                    continue;

                int texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, texture);

                // glTF texture coordinates have (0, 0) at the image's top-left, which is its first row: upload rows in order.
                fixed (byte* pixels = image.Pixels)
                    GL.TexImage2D(TextureTarget2d.Texture2D, 0, TextureComponentCount.Rgba8, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)pixels);

                GL.GenerateMipmap(TextureTarget.Texture2D);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                gpu.Textures[i] = texture;
            }

            for (int m = 0; m < model.Meshes.Count; m++)
            {
                foreach (var primitive in model.Meshes[m].Primitives)
                {
                    int vertexArray = GL.GenVertexArray();
                    GL.BindVertexArray(vertexArray);

                    int vertexBuffer = GL.GenBuffer();
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
                    GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(primitive.Vertices.Length * sizeof(float)), primitive.Vertices, BufferUsageHint.StaticDraw);

                    int indexBuffer = GL.GenBuffer();
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(primitive.Indices.Length * sizeof(uint)), primitive.Indices, BufferUsageHint.StaticDraw);

                    const int stride = GltfModel.Primitive.FLOATS_PER_VERTEX * sizeof(float);
                    GL.EnableVertexAttribArray(0);
                    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, IntPtr.Zero);
                    GL.EnableVertexAttribArray(1);
                    GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (IntPtr)(3 * sizeof(float)));
                    GL.EnableVertexAttribArray(2);
                    GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (IntPtr)(6 * sizeof(float)));

                    gpu.Primitives.Add((m, vertexArray, vertexBuffer, indexBuffer, primitive.Indices.Length, primitive.Material));
                }
            }

            GL.BindVertexArray(0);
            return gpu;
        }

        private void drawModel(OpenXRControllerModels.HandModel hand, bool active, Vector3 accent)
        {
            var model = hand.Model;

            if (!gpuModels.TryGetValue(model, out var gpu))
                gpuModels[model] = gpu = upload(model);

            // A faint accent outline marks the controller driving the pointer.
            var rim = accent * (active ? 0.5f : 0.12f);
            GL.Uniform3(tRim, rim.X, rim.Y, rim.Z);
            GL.Uniform1(tTexture, 0);

            for (int n = 0; n < model.Nodes.Count; n++)
            {
                var node = model.Nodes[n];
                if (node.Mesh < 0 || node.Mesh >= model.Meshes.Count || !hand.NodeVisible[n])
                    continue;

                var world = hand.NodeWorld[n] * hand.Root;
                Matrix4x4.Invert(world, out var inverse);
                setMatrix(tModel, world);
                setMatrix(tNormal, Matrix4x4.Transpose(inverse));

                // Mirroring transforms flip the winding.
                GL.FrontFace(world.GetDeterminant() < 0 ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw);

                foreach (var p in gpu.Primitives)
                {
                    if (p.mesh != node.Mesh)
                        continue;

                    var material = p.material >= 0 && p.material < model.Materials.Count ? model.Materials[p.material] : null;
                    var colour = material == null ? Vector4.One : material.TextureUnavailable ? untextured_colour : material.BaseColour;
                    var emissive = material?.Emissive ?? Vector3.Zero;
                    int texture = material?.BaseColourImage >= 0 ? gpu.Textures[material.BaseColourImage] : 0;

                    GL.Uniform4(tBase, colour.X, colour.Y, colour.Z, colour.W);
                    GL.Uniform3(tEmissive, emissive.X, emissive.Y, emissive.Z);
                    GL.BindTexture(TextureTarget.Texture2D, texture != 0 ? texture : whiteTexture);

                    if (material?.DoubleSided == true)
                        GL.Disable(EnableCap.CullFace);

                    GL.BindVertexArray(p.vertexArray);
                    GL.DrawElements(PrimitiveType.Triangles, p.indexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);

                    if (material?.DoubleSided == true)
                        GL.Enable(EnableCap.CullFace);
                }
            }

            GL.FrontFace(FrontFaceDirection.Ccw);
        }

        #endregion

        private static Matrix4x4 part(Vector3 scale, Quaternion rotation, Vector3 translation)
            => Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

        private void draw(in Mesh mesh, Matrix4x4 model, Vector3 baseColour, Vector3 emissive)
        {
            Matrix4x4.Invert(model, out var inverse);

            setMatrix(uModel, model);
            setMatrix(uNormal, Matrix4x4.Transpose(inverse));
            GL.Uniform3(uBase, baseColour.X, baseColour.Y, baseColour.Z);
            GL.Uniform3(uEmissive, emissive.X, emissive.Y, emissive.Z);
            GL.DrawElements(PrimitiveType.Triangles, mesh.IndexCount, DrawElementsType.UnsignedShort, (IntPtr)(mesh.FirstIndex * sizeof(ushort)));
        }

        /// <remarks>
        /// System.Numerics matrices are row-vector and row-major; uploaded as-is they're the column-vector, column-major matrices GLSL expects.
        /// </remarks>
        private static void setMatrix(int location, Matrix4x4 matrix) => GL.UniformMatrix4(location, 1, false, (float*)&matrix);

        /// <summary>
        /// An asymmetric perspective projection for an OpenXR field of view (GL clip space), in row-vector convention.
        /// </summary>
        private static Matrix4x4 projection(XrFovf fov)
        {
            float left = MathF.Tan(fov.angleLeft), right = MathF.Tan(fov.angleRight);
            float down = MathF.Tan(fov.angleDown), up = MathF.Tan(fov.angleUp);
            float width = right - left, height = up - down;

            return new Matrix4x4
            {
                M11 = 2 / width,
                M22 = 2 / height,
                M31 = (right + left) / width,
                M32 = (up + down) / height,
                M33 = -(far_plane + near_plane) / (far_plane - near_plane),
                M34 = -1,
                M43 = -2 * far_plane * near_plane / (far_plane - near_plane),
            };
        }

        /// <summary>
        /// The projection layer for the frame just rendered (the pose and fov of each view must match what was rendered).
        /// Composited with alpha blending over the layers below.
        /// </summary>
        public void FillLayer(XrCompositionLayerProjection* layer, XrCompositionLayerProjectionView* projectionViews, ulong space)
        {
            for (int i = 0; i < 2; i++)
            {
                projectionViews[i] = new XrCompositionLayerProjectionView
                {
                    type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW,
                    pose = views[i].pose,
                    fov = views[i].fov,
                    subImage = eyes[i].SubImage,
                };
            }

            *layer = new XrCompositionLayerProjection
            {
                type = XR_TYPE_COMPOSITION_LAYER_PROJECTION,
                layerFlags = XrCompositionLayerFlags.XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT,
                space = space,
                viewCount = 2,
                views = projectionViews,
            };
        }

        #region Meshes

        private readonly record struct Mesh(int FirstIndex, int IndexCount);

        /// <summary>
        /// A unit sphere (radius 1).
        /// </summary>
        private static Mesh buildSphere(List<float> vertices, List<ushort> indices, int segments, int rings)
        {
            int firstVertex = vertices.Count / 6;
            int firstIndex = indices.Count;

            for (int r = 0; r <= rings; r++)
            {
                float theta = MathF.PI * r / rings;

                for (int s = 0; s <= segments; s++)
                {
                    float phi = 2 * MathF.PI * s / segments;
                    var n = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta));
                    vertices.AddRange(new[] { n.X, n.Y, n.Z, n.X, n.Y, n.Z });
                }
            }

            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = firstVertex + r * (segments + 1) + s;
                    int b = a + segments + 1;
                    // Counter-clockwise seen from outside (s turns anticlockwise about +Z, r moves away from the +Z pole).
                    indices.AddRange(new[] { (ushort)a, (ushort)b, (ushort)(a + 1), (ushort)(a + 1), (ushort)b, (ushort)(b + 1) });
                }
            }

            return new Mesh(firstIndex, indices.Count - firstIndex);
        }

        /// <summary>
        /// A unit cylinder along Z (radius 1, from z = -0.5 to 0.5), capped.
        /// </summary>
        private static Mesh buildCylinder(List<float> vertices, List<ushort> indices, int segments)
        {
            int firstIndex = indices.Count;

            // Side.
            int side = vertices.Count / 6;

            for (int s = 0; s <= segments; s++)
            {
                float phi = 2 * MathF.PI * s / segments;
                float x = MathF.Cos(phi), y = MathF.Sin(phi);
                vertices.AddRange(new[] { x, y, -0.5f, x, y, 0 });
                vertices.AddRange(new[] { x, y, 0.5f, x, y, 0 });
            }

            for (int s = 0; s < segments; s++)
            {
                int a = side + s * 2;
                indices.AddRange(new[] { (ushort)a, (ushort)(a + 2), (ushort)(a + 1), (ushort)(a + 2), (ushort)(a + 3), (ushort)(a + 1) });
            }

            // Caps.
            foreach (float z in new[] { -0.5f, 0.5f })
            {
                int centre = vertices.Count / 6;
                vertices.AddRange(new[] { 0, 0, z, 0, 0, MathF.Sign(z) });

                for (int s = 0; s <= segments; s++)
                {
                    float phi = 2 * MathF.PI * s / segments;
                    vertices.AddRange(new[] { MathF.Cos(phi), MathF.Sin(phi), z, 0, 0, MathF.Sign(z) });
                }

                for (int s = 0; s < segments; s++)
                {
                    ushort a = (ushort)(centre + 1 + s), b = (ushort)(centre + 2 + s);

                    if (z > 0)
                        indices.AddRange(new[] { (ushort)centre, a, b });
                    else
                        indices.AddRange(new[] { (ushort)centre, b, a });
                }
            }

            return new Mesh(firstIndex, indices.Count - firstIndex);
        }

        #endregion

        #region Shaders

        private const string surface_vertex_shader = @"#version 300 es
layout(location = 0) in vec3 a_position;
layout(location = 1) in vec2 a_uv;
uniform mat4 u_viewProjection;
out vec2 v_uv;
void main()
{
    v_uv = a_uv;
    gl_Position = u_viewProjection * vec4(a_position, 1.0);
}";

        private const string surface_fragment_shader = @"#version 300 es
precision highp float;
in vec2 v_uv;
uniform sampler2D u_texture;
uniform float u_opaque;
out vec4 o_colour;
void main()
{
    // Game images have no mipmaps: four samples spread over the pixel's footprint keep text and thin lines from shimmering where
    // the image is shown smaller than it is, and change nothing where it's shown larger.
    vec2 dx = dFdx(v_uv) * 0.25;
    vec2 dy = dFdy(v_uv) * 0.25;
    vec4 c = (texture(u_texture, v_uv + dx + dy) + texture(u_texture, v_uv + dx - dy)
              + texture(u_texture, v_uv - dx + dy) + texture(u_texture, v_uv - dx - dy)) * 0.25;
    o_colour = vec4(c.rgb, max(c.a, u_opaque));
}";

        private const string vertex_shader = @"#version 300 es
layout(location = 0) in vec3 a_position;
layout(location = 1) in vec3 a_normal;
uniform mat4 u_viewProjection;
uniform mat4 u_model;
uniform mat4 u_normal;
out vec3 v_position;
out vec3 v_normal;
void main()
{
    vec4 world = u_model * vec4(a_position, 1.0);
    v_position = world.xyz;
    v_normal = (u_normal * vec4(a_normal, 0.0)).xyz;
    gl_Position = u_viewProjection * world;
}";

        private const string fragment_shader = @"#version 300 es
precision highp float;
in vec3 v_position;
in vec3 v_normal;
uniform vec3 u_viewPos;
uniform vec3 u_base;
uniform vec3 u_emissive;
out vec4 o_colour;
void main()
{
    vec3 n = normalize(v_normal);
    vec3 v = normalize(u_viewPos - v_position);
    float facing = dot(n, v);

    // Soft key light from above, a little fill, and a rim that outlines the shape against dark surroundings.
    vec3 light = normalize(vec3(0.3, 1.0, 0.4));
    float diffuse = max(dot(n, light), 0.0);
    vec3 h = normalize(light + v);
    float specular = pow(max(dot(n, h), 0.0), 40.0) * 0.35;
    float rim = pow(1.0 - max(facing, 0.0), 3.0);

    vec3 colour = u_base * (0.45 + 0.9 * diffuse) + vec3(specular) + vec3(0.18, 0.22, 0.28) * rim + u_emissive;

    // Silhouettes fade out over their last few degrees: cheap antialiasing at the layer's reduced resolution.
    float alpha = clamp(facing * 6.0, 0.0, 1.0);
    o_colour = vec4(colour * alpha, alpha);
}";

        private const string textured_vertex_shader = @"#version 300 es
layout(location = 0) in vec3 a_position;
layout(location = 1) in vec3 a_normal;
layout(location = 2) in vec2 a_texCoord;
uniform mat4 u_viewProjection;
uniform mat4 u_model;
uniform mat4 u_normal;
out vec3 v_position;
out vec3 v_normal;
out vec2 v_texCoord;
void main()
{
    vec4 world = u_model * vec4(a_position, 1.0);
    v_position = world.xyz;
    v_normal = (u_normal * vec4(a_normal, 0.0)).xyz;
    v_texCoord = a_texCoord;
    gl_Position = u_viewProjection * world;
}";

        private const string textured_fragment_shader = @"#version 300 es
precision highp float;
in vec3 v_position;
in vec3 v_normal;
in vec2 v_texCoord;
uniform vec3 u_viewPos;
uniform vec4 u_base;
uniform vec3 u_emissive;
uniform vec3 u_rim;
uniform sampler2D u_texture;
out vec4 o_colour;
void main()
{
    vec3 n = normalize(v_normal);
    vec3 v = normalize(u_viewPos - v_position);
    float facing = dot(n, v);
    // Double-sided surfaces seen from behind are lit as their front.
    if (facing < 0.0) { n = -n; facing = -facing; }

    vec4 base = texture(u_texture, v_texCoord) * u_base;

    vec3 light = normalize(vec3(0.3, 1.0, 0.4));
    float diffuse = max(dot(n, light), 0.0);
    vec3 h = normalize(light + v);
    float specular = pow(max(dot(n, h), 0.0), 40.0) * 0.15;
    float rim = pow(1.0 - facing, 3.0);

    // Lit for a dim scene: light plastic shouldn't glare next to the panel.
    vec3 colour = base.rgb * (0.28 + 0.5 * diffuse) + vec3(specular) + (vec3(0.08, 0.1, 0.14) + u_rim) * rim + u_emissive;

    // Silhouettes fade out over their last few degrees: cheap antialiasing at the layer's reduced resolution.
    float alpha = clamp(facing * 5.0, 0.0, 1.0);
    o_colour = vec4(colour * alpha, alpha);
}";

        private static int createProgram(string vertexSource, string fragmentSource)
        {
            int vertex = compileShader(ShaderType.VertexShader, vertexSource);
            int fragment = compileShader(ShaderType.FragmentShader, fragmentSource);

            int p = GL.CreateProgram();
            GL.AttachShader(p, vertex);
            GL.AttachShader(p, fragment);
            GL.LinkProgram(p);

            GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
                throw new InvalidOperationException($"Scene shader failed to link: {GL.GetProgramInfoLog(p)}");

            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            return p;
        }

        private static int compileShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);

            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0)
                throw new InvalidOperationException($"Controller {type} failed to compile: {GL.GetShaderInfoLog(shader)}");

            return shader;
        }

        #endregion

        public void Dispose()
        {
            // GL objects go with the context; only the swapchains need releasing.
            foreach (var eye in eyes)
                eye?.Dispose();
        }
    }
}
