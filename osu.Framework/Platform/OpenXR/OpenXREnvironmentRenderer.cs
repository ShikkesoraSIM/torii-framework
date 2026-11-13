// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Drawing;
using System.Numerics;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using osuTK.Graphics.ES30;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Where things are in the rendered environment, in the session's base space.
    /// </summary>
    internal struct EnvironmentScene
    {
        /// <summary>
        /// Whether the background is shown on a wall in a hall (custom field), or the scene is an ambient void (the background floats
        /// behind the panel as its own layer).
        /// </summary>
        public bool Field;

        /// <summary>
        /// Height of the (real) floor. The floor is always level.
        /// </summary>
        public float FloorY;

        /// <summary>
        /// Where the grid is centred (XZ) and which way it faces (yaw, radians), so its lines run towards the wall.
        /// </summary>
        public Vector2 GridOrigin;

        public float GridYaw;

        /// <summary>
        /// The wall's centre, its in-plane axes (unit) and size in metres. The wall faces the viewer.
        /// </summary>
        public Vector3 WallCentre, WallRight, WallUp;

        public Vector2 WallSize;

        /// <summary>
        /// Direction the ambient glow comes from (the front).
        /// </summary>
        public Vector3 GlowDirection;

        /// <summary>
        /// Where the background is seen from the viewer, around <see cref="GlowDirection"/> in the <see cref="WallRight"/> /
        /// <see cref="WallUp"/> frame: centre x, y, half width and half height. Light spills around it (a halo).
        /// For the field's flat wall these are tangents (a rectangle on the wall plane); for the void's sphere section, angles in radians.
        /// </summary>
        public Vector4 Halo;
    }

    /// <summary>
    /// Renders the environment around the game into a stereo projection layer: an ambient void (a glow in the background's dominant
    /// colour and a grid floor in its complementary colour) or a custom field (a dark hall with the background on a far wall and a
    /// reflective grid floor). The floor reacts to the music. Includes a GPU flash limiter for the wall.
    /// </summary>
    /// <remarks>
    /// The scene is analytic: each eye is a single full-screen pass in which every pixel's view ray is intersected with the floor plane and
    /// the wall rectangle, so there is no geometry, depth buffer or asset loading. The background's dominant colour and brightness are
    /// measured and smoothed over time by 1x1 GPU passes (no CPU readback). It shares the game's GL context, so all GL state it touches is
    /// saved and restored (the framework's renderer caches GL state).
    /// </remarks>
    internal sealed unsafe class OpenXREnvironmentRenderer : IDisposable
    {
        public const int BAND_COUNT = 32;

        /// <summary>
        /// Flash reduction strength: 0 = off, 1 = strongest (the wall's average brightness may only rise slowly).
        /// </summary>
        public float FlashReduction { get; set; } = 0.5f;

        /// <summary>
        /// Time constant (seconds) with which the ambient colour follows the background's dominant colour.
        /// </summary>
        public float ColourFadeTime { get; set; } = 1.2f;

        private readonly XrFunctions xr;
        private readonly ulong session;
        private readonly OpenXRSwapchainTarget[] eyes = new OpenXRSwapchainTarget[2];
        private readonly XrView[] views = new XrView[2];

        private readonly int sceneProgram;
        private readonly int luminanceProgram;
        private readonly int adaptProgram;
        private readonly int dominantProgram;
        private readonly int colourAdaptProgram;
        private readonly int vertexArray;

        // Flash limiter: current wall luminance, and two ping-ponged adapted luminance targets (all 1x1).
        private readonly (int texture, int framebuffer) currentLuminance;
        private readonly (int texture, int framebuffer)[] adaptedLuminance = new (int, int)[2];
        private int adaptedLuminanceIndex;

        // Ambient colour: the background's dominant colour this frame, and two ping-ponged smoothed colour targets (all 1x1).
        private readonly (int texture, int framebuffer) currentColour;
        private readonly (int texture, int framebuffer)[] ambientColour = new (int, int)[2];
        private int ambientColourIndex;

        private long lastRenderTime;

        private readonly float[] bands = new float[BAND_COUNT];
        private float beatPulse;
        private bool kiai;

        public bool HasRendered { get; private set; }

        private OpenXREnvironmentRenderer(XrFunctions xr, ulong session, long format, XrStructureType imageType, Size eyeSize)
        {
            this.xr = xr;
            this.session = session;

            // Setting up changes GL state the framework caches (e.g. the clear colour, via the 1x1 targets' initial clears).
            var state = GLState.Capture();

            for (int i = 0; i < 2; i++)
                eyes[i] = new OpenXRSwapchainTarget(xr, session, eyeSize, format, imageType);

            sceneProgram = compileProgram(scene_fragment);
            luminanceProgram = compileProgram(luminance_fragment);
            adaptProgram = compileProgram(adapt_fragment);
            dominantProgram = compileProgram(dominant_fragment);
            colourAdaptProgram = compileProgram(colour_adapt_fragment);
            vertexArray = GL.GenVertexArray();

            currentLuminance = create1x1Target(0, 0, 0, 0);
            for (int i = 0; i < 2; i++)
                adaptedLuminance[i] = create1x1Target(0, 0, 0, 0);

            currentColour = create1x1Target(0, 0, 0, 0);
            // Start from a calm blue until the first background has been measured.
            for (int i = 0; i < 2; i++)
                ambientColour[i] = create1x1Target(0.35f, 0.5f, 1f, 1f);

            state.Restore();

            Logger.Log($"OpenXR: environment renderer created ({eyeSize.Width}x{eyeSize.Height} per eye)");
        }

        /// <summary>
        /// Creates the renderer, sizing its per-eye images from the runtime's recommendation scaled by <paramref name="resolutionScale"/>.
        /// Must be called on the GL thread.
        /// </summary>
        public static OpenXREnvironmentRenderer Create(XrFunctions xr, ulong instance, ulong systemId, ulong session, long format, XrStructureType imageType,
                                                       float resolutionScale)
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

            return new OpenXREnvironmentRenderer(xr, session, format, imageType, size);
        }

        /// <summary>
        /// Sets the music data driving the floor: per-band amplitudes (0..1, low to high frequencies), a beat pulse (1 on the beat,
        /// decaying to 0) and whether kiai time is active.
        /// </summary>
        public void SetAudio(ReadOnlySpan<float> amplitudes, float pulse, bool isKiai)
        {
            int n = Math.Min(amplitudes.Length, BAND_COUNT);
            amplitudes[..n].CopyTo(bands);
            beatPulse = pulse;
            kiai = isKiai;
        }

        /// <summary>
        /// Renders both eyes for the given frame.
        /// </summary>
        /// <param name="space">The base space the views are located in (and the layer is submitted in).</param>
        /// <param name="displayTime">The frame's predicted display time.</param>
        /// <param name="scene">Where the floor, grid, wall and glow are, in <paramref name="space"/>.</param>
        /// <param name="backgroundTexture">The GL texture holding the game's background this frame, or 0 if none.</param>
        public void Render(ulong space, long displayTime, in EnvironmentScene scene, int backgroundTexture)
        {
            HasRendered = false;

            var locateInfo = new XrViewLocateInfo
            {
                type = XR_TYPE_VIEW_LOCATE_INFO,
                viewConfigurationType = XrViewConfigurationType.XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                displayTime = displayTime,
                space = space,
            };

            var viewState = new XrViewState { type = XR_TYPE_VIEW_STATE };
            uint count;

            views[0].type = views[1].type = XR_TYPE_VIEW;

            fixed (XrView* v = views)
                xr.xrLocateViews(session, &locateInfo, &viewState, 2, &count, v).ThrowIfFailed(nameof(xr.xrLocateViews));

            const ulong valid = XrViewStateFlags.XR_VIEW_STATE_POSITION_VALID_BIT | XrViewStateFlags.XR_VIEW_STATE_ORIENTATION_VALID_BIT;
            if ((viewState.viewStateFlags & valid) != valid)
                return;

            float dt = lastRenderTime == 0 ? 0 : Math.Clamp((displayTime - lastRenderTime) / 1e9f, 0, 0.1f);
            lastRenderTime = displayTime;

            var state = GLState.Capture();

            try
            {
                GL.BindVertexArray(vertexArray);
                GL.Disable(EnableCap.ScissorTest);
                GL.Disable(EnableCap.DepthTest);
                GL.Disable(EnableCap.Blend);
                GL.Disable(EnableCap.CullFace);
                GL.ColorMask(true, true, true, true);
                GL.ActiveTexture(TextureUnit.Texture0);

                if (backgroundTexture != 0)
                    prepareBackgroundTexture(backgroundTexture);

                updateFlashLimiter(backgroundTexture, dt);
                updateAmbientColour(backgroundTexture, dt);

                for (int eye = 0; eye < 2; eye++)
                    renderEye(eye, scene, backgroundTexture);

                HasRendered = true;
            }
            finally
            {
                state.Restore();
            }
        }

        private void prepareBackgroundTexture(int texture)
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            // The game's output is gamma-encoded in an sRGB image; sample the stored values as-is, like the rest of the pipeline.
            const TextureParameterName texture_srgb_decode_ext = (TextureParameterName)0x8A48;
            const int skip_decode_ext = 0x8A4A;
            GL.TexParameter(TextureTarget.Texture2D, texture_srgb_decode_ext, skip_decode_ext);
        }

        private void drawTo((int texture, int framebuffer) target, int program)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, target.framebuffer);
            GL.Viewport(0, 0, 1, 1);
            GL.UseProgram(program);
        }

        private void bind(TextureUnit unit, int texture)
        {
            GL.ActiveTexture(unit);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.ActiveTexture(TextureUnit.Texture0);
        }

        private void updateFlashLimiter(int backgroundTexture, float dt)
        {
            // 1. Average luminance of the background this frame.
            drawTo(currentLuminance, luminanceProgram);
            bind(TextureUnit.Texture0, backgroundTexture);
            GL.Uniform1(GL.GetUniformLocation(luminanceProgram, "u_wall"), 0);
            GL.Uniform1(GL.GetUniformLocation(luminanceProgram, "u_hasWall"), backgroundTexture != 0 ? 1f : 0f);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // 2. Adapted luminance: may rise only at a limited rate, falls quickly.
            int previous = adaptedLuminanceIndex;
            adaptedLuminanceIndex = 1 - adaptedLuminanceIndex;

            float strength = Math.Clamp(FlashReduction, 0, 1);
            float maxRise = strength <= 0 ? 1000 : 4 - 3.5f * strength; // luminance per second

            drawTo(adaptedLuminance[adaptedLuminanceIndex], adaptProgram);
            bind(TextureUnit.Texture1, adaptedLuminance[previous].texture);
            bind(TextureUnit.Texture2, currentLuminance.texture);
            GL.Uniform1(GL.GetUniformLocation(adaptProgram, "u_adapted"), 1);
            GL.Uniform1(GL.GetUniformLocation(adaptProgram, "u_current"), 2);
            GL.Uniform1(GL.GetUniformLocation(adaptProgram, "u_maxRise"), maxRise * dt);
            GL.Uniform1(GL.GetUniformLocation(adaptProgram, "u_fall"), 1 - MathF.Exp(-dt * 8));
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        private void updateAmbientColour(int backgroundTexture, float dt)
        {
            if (backgroundTexture == 0)
                return;

            // 1. Dominant colour of the background this frame (alpha = confidence: low for black / grey images).
            drawTo(currentColour, dominantProgram);
            bind(TextureUnit.Texture0, backgroundTexture);
            GL.Uniform1(GL.GetUniformLocation(dominantProgram, "u_image"), 0);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // 2. Fade the ambient colour towards it.
            int previous = ambientColourIndex;
            ambientColourIndex = 1 - ambientColourIndex;

            drawTo(ambientColour[ambientColourIndex], colourAdaptProgram);
            bind(TextureUnit.Texture1, ambientColour[previous].texture);
            bind(TextureUnit.Texture2, currentColour.texture);
            GL.Uniform1(GL.GetUniformLocation(colourAdaptProgram, "u_previous"), 1);
            GL.Uniform1(GL.GetUniformLocation(colourAdaptProgram, "u_current"), 2);
            GL.Uniform1(GL.GetUniformLocation(colourAdaptProgram, "u_rate"), 1 - MathF.Exp(-dt / Math.Max(ColourFadeTime, 0.01f)));
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        private void renderEye(int eye, in EnvironmentScene scene, int backgroundTexture)
        {
            var target = eyes[eye];
            int framebuffer = target.Acquire();

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.Viewport(0, 0, target.Size.Width, target.Size.Height);

            var view = views[eye];
            Quaternion orientation = view.pose.orientation.ToNumerics();

            GL.UseProgram(sceneProgram);
            setUniform("u_eye", view.pose.position.ToNumerics());
            setUniform("u_right", Vector3.Transform(Vector3.UnitX, orientation));
            setUniform("u_up", Vector3.Transform(Vector3.UnitY, orientation));
            setUniform("u_back", Vector3.Transform(Vector3.UnitZ, orientation));

            GL.Uniform4(GL.GetUniformLocation(sceneProgram, "u_tan"),
                MathF.Tan(view.fov.angleLeft), MathF.Tan(view.fov.angleRight), MathF.Tan(view.fov.angleDown), MathF.Tan(view.fov.angleUp));

            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_field"), scene.Field ? 1f : 0f);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_floorY"), scene.FloorY);
            GL.Uniform4(GL.GetUniformLocation(sceneProgram, "u_grid"), scene.GridOrigin.X, scene.GridOrigin.Y, MathF.Cos(scene.GridYaw), MathF.Sin(scene.GridYaw));
            setUniform("u_wallCentre", scene.WallCentre);
            setUniform("u_wallRight", scene.WallRight);
            setUniform("u_wallUp", scene.WallUp);
            GL.Uniform2(GL.GetUniformLocation(sceneProgram, "u_wallSize"), scene.WallSize.X, scene.WallSize.Y);
            setUniform("u_glowDir", scene.GlowDirection);
            GL.Uniform4(GL.GetUniformLocation(sceneProgram, "u_halo"), scene.Halo.X, scene.Halo.Y, scene.Halo.Z, scene.Halo.W);

            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_hasWall"), backgroundTexture != 0 ? 1f : 0f);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_flashReduction"), FlashReduction > 0 ? 1f : 0f);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_pulse"), beatPulse);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_kiai"), kiai ? 1f : 0f);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_bands"), BAND_COUNT, bands);

            bind(TextureUnit.Texture1, adaptedLuminance[adaptedLuminanceIndex].texture);
            bind(TextureUnit.Texture2, currentLuminance.texture);
            bind(TextureUnit.Texture3, ambientColour[ambientColourIndex].texture);
            bind(TextureUnit.Texture0, backgroundTexture);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_wallTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_adapted"), 1);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_current"), 2);
            GL.Uniform1(GL.GetUniformLocation(sceneProgram, "u_ambient"), 3);

            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // Only the colour matters; let tiled GPUs skip storing the depth/stencil attachment.
            var discard = stackalloc FramebufferAttachment[] { FramebufferAttachment.DepthStencilAttachment };
            GL.InvalidateFramebuffer(FramebufferTarget.Framebuffer, 1, discard);

            target.Release();
        }

        private void setUniform(string name, Vector3 v) => GL.Uniform3(GL.GetUniformLocation(sceneProgram, name), v.X, v.Y, v.Z);

        /// <summary>
        /// The projection layer for the frame just rendered (the pose and fov of each view must match what was rendered).
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
                space = space,
                viewCount = 2,
                views = projectionViews,
            };
        }

        private static (int texture, int framebuffer) create1x1Target(float r, float g, float b, float a)
        {
            int texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget2d.Texture2D, 0, TextureComponentCount.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            int framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget2d.Texture2D, texture, 0);
            GL.ClearColor(r, g, b, a);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            return (texture, framebuffer);
        }

        private static int compileProgram(string fragmentSource)
        {
            int vertex = compileShader(ShaderType.VertexShader, fullscreen_vertex);
            int fragment = compileShader(ShaderType.FragmentShader, fragmentSource);

            int program = GL.CreateProgram();
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);

            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
                throw new InvalidOperationException($"Environment shader failed to link: {GL.GetProgramInfoLog(program)}");

            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            return program;
        }

        private static int compileShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);

            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0)
                throw new InvalidOperationException($"Environment {type} failed to compile: {GL.GetShaderInfoLog(shader)}");

            return shader;
        }

        public void Dispose()
        {
            foreach (var eye in eyes)
                eye?.Dispose();
        }

        /// <summary>
        /// Snapshot of the GL state this renderer changes, restored afterwards so the framework's cached GL state stays valid.
        /// </summary>
        private unsafe struct GLState
        {
            private int program, vertexArray, arrayBuffer, framebuffer, activeTexture;
            private int texture0, texture1, texture2, texture3;
            private int viewportX, viewportY, viewportW, viewportH;
            private bool scissor, depthTest, blend, cullFace;
            private bool maskR, maskG, maskB, maskA;
            private float clearR, clearG, clearB, clearA;

            public static GLState Capture()
            {
                var s = new GLState();
                GL.GetInteger(GetPName.CurrentProgram, out s.program);
                GL.GetInteger(GetPName.VertexArrayBinding, out s.vertexArray);
                GL.GetInteger(GetPName.ArrayBufferBinding, out s.arrayBuffer);
                GL.GetInteger(GetPName.FramebufferBinding, out s.framebuffer);
                GL.GetInteger(GetPName.ActiveTexture, out s.activeTexture);

                GL.ActiveTexture(TextureUnit.Texture0);
                GL.GetInteger(GetPName.TextureBinding2D, out s.texture0);
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.GetInteger(GetPName.TextureBinding2D, out s.texture1);
                GL.ActiveTexture(TextureUnit.Texture2);
                GL.GetInteger(GetPName.TextureBinding2D, out s.texture2);
                GL.ActiveTexture(TextureUnit.Texture3);
                GL.GetInteger(GetPName.TextureBinding2D, out s.texture3);
                GL.ActiveTexture((TextureUnit)s.activeTexture);

                int* viewport = stackalloc int[4];
                GL.GetInteger(GetPName.Viewport, viewport);
                (s.viewportX, s.viewportY, s.viewportW, s.viewportH) = (viewport[0], viewport[1], viewport[2], viewport[3]);

                s.scissor = GL.IsEnabled(EnableCap.ScissorTest);
                s.depthTest = GL.IsEnabled(EnableCap.DepthTest);
                s.blend = GL.IsEnabled(EnableCap.Blend);
                s.cullFace = GL.IsEnabled(EnableCap.CullFace);

                bool* colorMask = stackalloc bool[4];
                GL.GetBoolean(GetPName.ColorWritemask, colorMask);
                (s.maskR, s.maskG, s.maskB, s.maskA) = (colorMask[0], colorMask[1], colorMask[2], colorMask[3]);

                float* clearColour = stackalloc float[4];
                GL.GetFloat(GetPName.ColorClearValue, clearColour);
                (s.clearR, s.clearG, s.clearB, s.clearA) = (clearColour[0], clearColour[1], clearColour[2], clearColour[3]);
                return s;
            }

            public readonly void Restore()
            {
                GL.UseProgram(program);
                GL.BindVertexArray(vertexArray);
                GL.BindBuffer(BufferTarget.ArrayBuffer, arrayBuffer);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);

                GL.ActiveTexture(TextureUnit.Texture3);
                GL.BindTexture(TextureTarget.Texture2D, texture3);
                GL.ActiveTexture(TextureUnit.Texture2);
                GL.BindTexture(TextureTarget.Texture2D, texture2);
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, texture1);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, texture0);
                GL.ActiveTexture((TextureUnit)activeTexture);

                GL.Viewport(viewportX, viewportY, viewportW, viewportH);
                set(EnableCap.ScissorTest, scissor);
                set(EnableCap.DepthTest, depthTest);
                set(EnableCap.Blend, blend);
                set(EnableCap.CullFace, cullFace);
                GL.ColorMask(maskR, maskG, maskB, maskA);
                GL.ClearColor(clearR, clearG, clearB, clearA);
            }

            private static void set(EnableCap cap, bool enabled)
            {
                if (enabled) GL.Enable(cap);
                else GL.Disable(cap);
            }
        }

        #region Shaders

        private const string fullscreen_vertex = @"#version 300 es
out vec2 v_ndc;
void main()
{
    // Full-screen triangle from gl_VertexID; no vertex buffers needed.
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2)) * 2.0 - 1.0;
    v_ndc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}";

        private const string luminance_fragment = @"#version 300 es
precision mediump float;
uniform sampler2D u_wall;
uniform float u_hasWall;
out vec4 o_colour;
void main()
{
    // Average luma of a 4x4 grid of samples over the image (premultiplied: transparent areas count as black).
    float sum = 0.0;
    for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            vec3 c = texture(u_wall, (vec2(x, y) + 0.5) / 4.0).rgb;
            sum += dot(c, vec3(0.2126, 0.7152, 0.0722));
        }
    o_colour = vec4(vec3(u_hasWall * sum / 16.0), 1.0);
}";

        private const string adapt_fragment = @"#version 300 es
precision highp float;
uniform sampler2D u_adapted;
uniform sampler2D u_current;
uniform float u_maxRise;
uniform float u_fall;
out vec4 o_colour;
void main()
{
    float adapted = texelFetch(u_adapted, ivec2(0), 0).r;
    float current = texelFetch(u_current, ivec2(0), 0).r;
    // Brightness may only rise at a limited rate (softening flashes), but falls quickly.
    float next = current > adapted ? adapted + min(current - adapted, u_maxRise) : mix(adapted, current, u_fall);
    o_colour = vec4(vec3(next), 1.0);
}";

        private const string dominant_fragment = @"#version 300 es
precision highp float;
uniform sampler2D u_image;
out vec4 o_colour;

vec3 rgb2hsv(vec3 c)
{
    vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-10)), d / (q.x + 1e-10), q.x);
}

void main()
{
    // Dominant colour via a coarse hue histogram over an 8x8 grid of samples: each sample votes for its hue with a weight favouring
    // saturated, bright pixels. The strongest hue (with its neighbours) wins, so two competing colours don't average into mud.
    const int BINS = 12;
    float weight[BINS];
    vec3 colour[BINS];
    for (int i = 0; i < BINS; i++) { weight[i] = 0.0; colour[i] = vec3(0.0); }

    float coverage = 0.0;
    for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            vec3 c = texture(u_image, (vec2(x, y) + 0.5) / 8.0).rgb;
            vec3 hsv = rgb2hsv(c);
            float w = hsv.y * hsv.y * hsv.z;
            int bin = int(hsv.x * float(BINS)) % BINS;
            weight[bin] += w;
            colour[bin] += c * w;
            coverage += step(0.03, hsv.z);
        }

    int best = 0;
    float bestWeight = -1.0;
    float total = 0.0;
    for (int i = 0; i < BINS; i++)
    {
        float w = weight[(i + BINS - 1) % BINS] + weight[i] + weight[(i + 1) % BINS];
        total += weight[i];
        if (w > bestWeight) { bestWeight = w; best = i; }
    }

    vec3 c = (colour[(best + BINS - 1) % BINS] + colour[best] + colour[(best + 1) % BINS]) / max(bestWeight, 1e-5);
    float mx = max(c.r, max(c.g, c.b));

    // Keep the hue at full brightness (the glow sets its own intensity). Confidence is low for black, grey or near-empty images (e.g. a
    // fully dimmed background), so the ambient colour holds instead of drifting.
    vec3 hue = mx > 0.001 ? c / mx : vec3(0.0);
    float confidence = clamp(total * 0.5, 0.0, 1.0) * clamp(coverage / 16.0, 0.0, 1.0);
    o_colour = vec4(hue, confidence);
}";

        private const string colour_adapt_fragment = @"#version 300 es
precision highp float;
uniform sampler2D u_previous;
uniform sampler2D u_current;
uniform float u_rate;
out vec4 o_colour;
void main()
{
    vec3 previous = texelFetch(u_previous, ivec2(0), 0).rgb;
    vec4 current = texelFetch(u_current, ivec2(0), 0);
    o_colour = vec4(mix(previous, current.rgb, u_rate * current.a), 1.0);
}";

        private const string scene_fragment = @"#version 300 es
precision highp float;
in vec2 v_ndc;

uniform vec3 u_eye;
uniform vec3 u_right;
uniform vec3 u_up;
uniform vec3 u_back;
uniform vec4 u_tan;          // left, right, down, up (tangents)

uniform float u_field;       // 1 = custom field (wall), 0 = ambient void
uniform float u_floorY;
uniform vec4 u_grid;         // origin x, origin z, cos(yaw), sin(yaw)
uniform vec3 u_wallCentre;
uniform vec3 u_wallRight;
uniform vec3 u_wallUp;
uniform vec2 u_wallSize;
uniform vec3 u_glowDir;
uniform vec4 u_halo;         // where the background is seen: centre x, y, half width, half height (see EnvironmentScene.Halo)

uniform float u_hasWall;
uniform float u_flashReduction;
uniform float u_pulse;
uniform float u_kiai;
uniform float u_bands[32];

uniform sampler2D u_wallTex;
uniform sampler2D u_adapted;
uniform sampler2D u_current;
uniform sampler2D u_ambient;

out vec4 o_colour;

const float FAR = 1e6;

// Flash limiter: scale the wall so its average brightness never exceeds the adapted level.
float wallScale()
{
    if (u_flashReduction < 0.5)
        return 1.0;
    float adapted = texelFetch(u_adapted, ivec2(0), 0).r;
    float current = texelFetch(u_current, ivec2(0), 0).r;
    return current > 0.01 ? clamp(adapted / current, 0.0, 1.0) : 1.0;
}

// Intersects a ray with the wall rectangle. Returns t, and the image coordinates in uv.
float hitWall(vec3 o, vec3 d, out vec2 uv)
{
    uv = vec2(0.0);
    if (u_field < 0.5) return FAR;
    vec3 n = cross(u_wallRight, u_wallUp); // faces the viewer
    float denom = dot(d, n);
    if (denom > -1e-4) return FAR;
    float t = dot(u_wallCentre - o, n) / denom;
    if (t <= 0.0) return FAR;
    vec3 p = o + d * t - u_wallCentre;
    uv = vec2(dot(p, u_wallRight) / u_wallSize.x + 0.5, dot(p, u_wallUp) / u_wallSize.y + 0.5);
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return FAR;
    return t;
}

vec3 sampleWall(vec2 uv)
{
    // Game images are bottom-up in GL; the wall image's top is at uv.y = 1.
    return texture(u_wallTex, uv).rgb * u_hasWall * wallScale();
}

// How bright the background shows (after dimming and flash reduction), for the light it casts.
float shownBrightness()
{
    return texelFetch(u_current, ivec2(0), 0).r * wallScale() * u_hasWall;
}

float band(float x01)
{
    int i = int(clamp(x01, 0.0, 0.999) * 32.0);
    return u_bands[i];
}

// The complementary colour (hue rotated 180 degrees), at full brightness; a cool blue for greys.
vec3 complement(vec3 c)
{
    float mx = max(c.r, max(c.g, c.b));
    float mn = min(c.r, min(c.g, c.b));
    if (mx - mn < 0.08) return vec3(0.35, 0.55, 1.0);
    vec3 comp = vec3(mx + mn) - c;
    return comp / max(comp.r, max(comp.g, comp.b));
}

// Light spilling around the background, like bias lighting behind a screen: brightest at its edges, fading with the angle from them.
float halo(vec3 d)
{
    vec3 l = vec3(dot(d, u_wallRight), dot(d, u_wallUp), dot(d, u_glowDir));

    // The wall is flat, so its outline is a rectangle in tangent space; the void's background is a sphere section, a rectangle in angles.
    vec2 a;
    if (u_field > 0.5)
        a = l.z > 1e-3 ? l.xy / l.z : vec2(1e3);
    else
        a = vec2(atan(l.x, l.z), asin(clamp(l.y, -1.0, 1.0)));

    float dist = length(max(abs(a - u_halo.xy) - u_halo.zw, 0.0));
    return 0.6 * exp(-dist / 0.04) + 0.4 * exp(-dist / 0.22);
}

// The ambient light of the scene from direction d: a soft dome of the ambient colour, brightest straight ahead and fading to nothing
// at the sides, and the halo around the background (dimmer when the background shows dark).
vec3 glow(vec3 d, vec3 ambient)
{
    float facing = max(dot(d, u_glowDir), 0.0);
    float dome = (0.22 * pow(facing, 2.0) + 0.04 * facing) * mix(1.0, 0.6, u_field);
    float light = 0.3 + 0.7 * smoothstep(0.0, 0.3, shownBrightness());
    return ambient * (dome + 0.5 * halo(d) * light);
}

// The floor's own light (grid), which fades into the distance, and what it reflects (wall and glow), which doesn't.
vec3 shadeFloor(vec3 p, vec3 d, vec3 ambient, vec3 gridColour, out vec3 reflection)
{
    // Grid space: aligned with where the player faces (lines run towards the wall), centred under them.
    vec2 rel = p.xz - u_grid.xy;
    vec2 g = vec2(rel.x * u_grid.z + rel.y * u_grid.w, -rel.x * u_grid.w + rel.y * u_grid.z);

    // Grid lines every metre, antialiased with screen-space derivatives.
    vec2 w = fwidth(g) * 1.2;
    vec2 lineDist = abs(fract(g - 0.5) - 0.5);
    vec2 lines = 1.0 - smoothstep(vec2(0.0), w, lineDist);
    float line = max(lines.x, lines.y);

    float dist = length(p.xz - u_eye.xz);
    float fade = exp(-dist * 0.08);

    // Music: lines brighten with the frequency band under them (low frequencies on the left), and a ripple travels from the front
    // towards the player on every beat.
    float span = max(u_wallSize.x, 8.0);
    float amp = band(g.x / span + 0.5);
    float reach = 10.0;
    float ripplePos = -reach * u_pulse;
    float ripple = exp(-abs(g.y - ripplePos) * 1.5) * u_pulse;

    vec3 colour = gridColour * (1.0 + u_kiai * 0.5);
    vec3 grid = colour * line * (0.10 + amp * 0.9 + ripple * 0.6) * fade;

    float fresnel = 0.35 + 0.65 * pow(1.0 - max(-d.y, 0.0), 4.0);
    vec3 r = reflect(d, vec3(0.0, 1.0, 0.0));

    // Glossy reflection of the wall (field): blurred more the further up the wall it reflects, and fading out from the wall's base
    // upwards and towards its sides, so it reads as a sheen on the floor rather than a mirrored box.
    reflection = vec3(0.0);
    vec2 ruv;
    float rt = hitWall(p, r, ruv);
    if (rt < FAR)
    {
        float blur = 0.004 + ruv.y * 0.04;
        vec3 wall = (sampleWall(ruv) + sampleWall(ruv + vec2(blur, 0.0)) + sampleWall(ruv - vec2(blur, 0.0))
                     + sampleWall(ruv + vec2(0.0, blur)) + sampleWall(ruv + vec2(0.0, 2.0 * blur))) * 0.2;
        float fade = exp(-ruv.y * 1.6) * smoothstep(0.0, 0.08, ruv.x) * smoothstep(1.0, 0.92, ruv.x) * smoothstep(1.0, 0.85, ruv.y);
        reflection = wall * 0.6 * fade;
    }

    // ...and of the glow and halo, which pool on the floor in front of the background.
    reflection = (reflection + glow(r, ambient) * 0.6) * fresnel;

    return grid;
}

void main()
{
    vec3 dirEye = vec3(
        mix(u_tan.x, u_tan.y, v_ndc.x * 0.5 + 0.5),
        mix(u_tan.z, u_tan.w, v_ndc.y * 0.5 + 0.5),
        -1.0);
    vec3 d = normalize(dirEye.x * u_right + dirEye.y * u_up + dirEye.z * u_back);
    vec3 o = u_eye;

    vec3 ambient = texelFetch(u_ambient, ivec2(0), 0).rgb;
    vec3 gridColour = complement(ambient);

    vec2 wuv;
    float tWall = hitWall(o, d, wuv);

    float tFloor = FAR;
    if (d.y < -1e-4)
    {
        float t = (u_floorY - o.y) / d.y;
        if (t > 0.0) tFloor = t;
    }

    vec3 sky = glow(d, ambient);

    vec3 colour;
    if (tWall < tFloor)
        colour = sampleWall(wuv);
    else if (tFloor < FAR)
    {
        // Distant floor fades into the glow, so the floor meets the sky without a seam at the horizon.
        // Reflections aren't fogged: they reflect the (bright) background and glow, so they stay visible up to the horizon.
        float fog = exp(-tFloor * 0.06);
        vec3 reflection;
        vec3 grid = shadeFloor(o + d * tFloor, d, ambient, gridColour, reflection);
        colour = mix(sky, grid, fog) + reflection * mix(0.6, 1.0, fog);
    }
    else
        colour = sky;

    o_colour = vec4(colour, 1.0);
}";

        #endregion
    }
}
