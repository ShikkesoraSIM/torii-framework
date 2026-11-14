// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using osu.Framework.Platform.OpenXR.Native;
using osuTK.Graphics.ES30;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Laser beams from the controllers to the panel, and a reticle where the inactive controller points. Ghost hands (see
    /// <see cref="OpenXRGhostHands"/>) get beams of their own colour.
    /// </summary>
    /// <remarks>
    /// Each beam is a quad composition layer with a static, pre-rendered texture, oriented every frame to run from the controller
    /// to its target and to face the viewer. The app does no per-frame rendering for these; the compositor draws them.
    /// </remarks>
    internal sealed unsafe class OpenXRPointerVisuals : IDisposable
    {
        public const int MAX_LAYERS = 7;

        private const int laser_texture_width = 256;
        private const int laser_texture_height = 32;
        private const int reticle_texture_size = 64;

        private const float laser_thickness = 0.004f;
        private const float laser_miss_length = 0.8f;
        private const float reticle_diameter = 0.02f;

        /// <summary>
        /// How far in front of the panel the reticle floats, to avoid z-fighting with the panel layer.
        /// </summary>
        private const float reticle_offset = 0.003f;

        private readonly XrFunctions xr;
        private readonly ulong space;

        private readonly ulong activeLaser;
        private readonly ulong inactiveLaser;
        private readonly ulong reticle;
        private readonly ulong activeGhostLaser;
        private readonly ulong inactiveGhostLaser;

        private static readonly Vector3 laser_colour = new Vector3(0.75f, 0.9f, 1f);
        private static readonly Vector3 ghost_laser_colour = new Vector3(1f, 0.72f, 0.45f);

        private readonly XrCompositionLayerQuad* layers;

        private readonly XrStructureType imageType;

        // The same images as plain GL textures, for drawing the visuals rather than submitting them as layers (see TextureFor).
        private readonly int activeLaserTexture;
        private readonly int inactiveLaserTexture;
        private readonly int reticleTexture;
        private readonly int activeGhostLaserTexture;
        private readonly int inactiveGhostLaserTexture;

        /// <param name="createTextures">Whether the visuals may be drawn by the caller (see <see cref="TextureFor"/>).</param>
        public OpenXRPointerVisuals(XrFunctions xr, ulong session, ulong space, long format, XrStructureType imageType, bool createTextures = false)
        {
            this.imageType = imageType;
            this.xr = xr;
            this.space = space;

            activeLaser = createStaticSwapchain(session, format, laser_texture_width, laser_texture_height, laserPixels(laser_colour, 0.9f));
            inactiveLaser = createStaticSwapchain(session, format, laser_texture_width, laser_texture_height, laserPixels(laser_colour, 0.35f));
            reticle = createStaticSwapchain(session, format, reticle_texture_size, reticle_texture_size, reticlePixels());
            activeGhostLaser = createStaticSwapchain(session, format, laser_texture_width, laser_texture_height, laserPixels(ghost_laser_colour, 0.8f));
            inactiveGhostLaser = createStaticSwapchain(session, format, laser_texture_width, laser_texture_height, laserPixels(ghost_laser_colour, 0.3f));

            if (createTextures)
            {
                activeLaserTexture = createTexture(laser_texture_width, laser_texture_height, laserPixels(laser_colour, 0.9f));
                inactiveLaserTexture = createTexture(laser_texture_width, laser_texture_height, laserPixels(laser_colour, 0.35f));
                reticleTexture = createTexture(reticle_texture_size, reticle_texture_size, reticlePixels());
                activeGhostLaserTexture = createTexture(laser_texture_width, laser_texture_height, laserPixels(ghost_laser_colour, 0.8f));
                inactiveGhostLaserTexture = createTexture(laser_texture_width, laser_texture_height, laserPixels(ghost_laser_colour, 0.3f));
            }

            layers = (XrCompositionLayerQuad*)NativeMemory.AllocZeroed((nuint)(MAX_LAYERS * sizeof(XrCompositionLayerQuad)));
        }

        /// <summary>
        /// Builds this frame's pointer layers.
        /// </summary>
        /// <param name="hands">Controller states sampled for this frame.</param>
        /// <param name="activeHand">The controller driving the pointer.</param>
        /// <param name="headPosition">The viewer's head position in the LOCAL space.</param>
        /// <param name="panel">The panel the controllers point at.</param>
        /// <param name="edgeTolerance">See <see cref="OpenXRInputHandler.EdgeTolerance"/>.</param>
        /// <param name="laserStartOffset">How far along the ray the beams start, in metres.</param>
        /// <param name="output">Receives pointers to the layers. Must have room for <see cref="MAX_LAYERS"/> entries.</param>
        /// <param name="ghosts">Ghost hands prepared for this frame, to get beams as well; or null.</param>
        /// <returns>The number of layers written.</returns>
        public int BuildLayers(XrHandState[] hands, int activeHand, Vector3 headPosition, OpenXRSession panel, float edgeTolerance, float laserStartOffset,
                               XrCompositionLayerBaseHeader** output, OpenXRGhostHands? ghosts = null)
        {
            int count = 0;

            build(hands, activeHand, headPosition, panel, edgeTolerance, laserStartOffset, activeLaser, inactiveLaser, true, output, ref count);

            // Where a ghost points is shown by what it pointed at (e.g. a replay's cursor): beams only.
            if (ghosts != null)
            {
                build(ghosts.Hands, ghosts.ActiveHand, headPosition, panel, edgeTolerance, ghosts.HoldingModel ? 0 : 0.02f, activeGhostLaser, inactiveGhostLaser, false,
                    output, ref count);
            }

            return count;
        }

        private void build(XrHandState[] hands, int activeHand, Vector3 headPosition, OpenXRSession panel, float edgeTolerance, float laserStartOffset,
                           ulong activeLaser, ulong inactiveLaser, bool reticles, XrCompositionLayerBaseHeader** output, ref int count)
        {
            for (int hand = 0; hand < hands.Length; hand++)
            {
                ref readonly var state = ref hands[hand];

                if (!state.AimValid)
                    continue;

                Vector3 direction = state.AimDirection;
                Vector3 start = state.AimPosition + direction * laserStartOffset;

                bool onPanel = panel.Panel.Intersect(state.AimPosition, direction, out Vector3 hit, out Vector2 uv) && PanelGeometry.IsWithin(uv, edgeTolerance);
                Vector3 end = onPanel ? hit : start + direction * laser_miss_length;

                float length = Vector3.Distance(start, end);

                // A controller pushed through the panel still points (see PanelGeometry.Intersect), but has no beam to show.
                if (length >= 0.01f && !panel.Panel.IsBehind(state.AimPosition))
                {
                    Vector3 middle = (start + end) / 2;

                    layers[count] = quad(hand == activeHand ? activeLaser : inactiveLaser, laser_texture_width, laser_texture_height,
                        middle, faceViewerAlong(direction, headPosition - middle), new Vector2(length, laser_thickness));
                    output[count] = (XrCompositionLayerBaseHeader*)&layers[count];
                    count++;
                }

                // The active controller's target is shown by the game's own cursor.
                if (reticles && onPanel && hand != activeHand)
                {
                    Quaternion facing = panel.Panel.SurfaceOrientation(hit);
                    layers[count] = quad(reticle, reticle_texture_size, reticle_texture_size,
                        hit + Vector3.Transform(new Vector3(0, 0, reticle_offset), facing), facing, new Vector2(reticle_diameter));
                    output[count] = (XrCompositionLayerBaseHeader*)&layers[count];
                    count++;
                }
            }
        }

        private XrCompositionLayerQuad quad(ulong swapchain, int textureWidth, int textureHeight, Vector3 position, Quaternion orientation, Vector2 size) => new XrCompositionLayerQuad
        {
            type = XR_TYPE_COMPOSITION_LAYER_QUAD,
            layerFlags = XrCompositionLayerFlags.XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT,
            space = space,
            eyeVisibility = XrEyeVisibility.XR_EYE_VISIBILITY_BOTH,
            subImage = new XrSwapchainSubImage
            {
                swapchain = swapchain,
                imageRect = new XrRect2Di { extent = new XrExtent2Di { width = textureWidth, height = textureHeight } },
            },
            pose = new XrPosef
            {
                position = new XrVector3f { x = position.X, y = position.Y, z = position.Z },
                orientation = new XrQuaternionf { x = orientation.X, y = orientation.Y, z = orientation.Z, w = orientation.W },
            },
            size = new XrExtent2Df { width = size.X, height = size.Y },
        };

        /// <summary>
        /// An orientation whose X axis runs along <paramref name="axis"/> and whose face (+Z) turns towards the viewer as far as possible
        /// (a cylindrical billboard), so a flat beam never appears edge-on.
        /// </summary>
        private static Quaternion faceViewerAlong(Vector3 axis, Vector3 toViewer)
        {
            Vector3 x = Vector3.Normalize(axis);
            Vector3 z = toViewer - x * Vector3.Dot(toViewer, x);

            if (z.LengthSquared() < 1e-8f)
                z = Vector3.Cross(x, MathF.Abs(x.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);

            z = Vector3.Normalize(z);
            Vector3 y = Vector3.Cross(z, x);

            // System.Numerics uses row vectors: each row is the image of a basis axis.
            var rotation = new Matrix4x4(
                x.X, x.Y, x.Z, 0,
                y.X, y.Y, y.Z, 0,
                z.X, z.Y, z.Z, 0,
                0, 0, 0, 1);

            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
        }

        /// <summary>
        /// A soft beam: bright core across its width, fading in from the controller and fading out at the far end.
        /// Premultiplied alpha.
        /// </summary>
        private static byte[] laserPixels(Vector3 colour, float opacity)
        {
            byte[] pixels = new byte[laser_texture_width * laser_texture_height * 4];

            for (int y = 0; y < laser_texture_height; y++)
            {
                float across = (y + 0.5f) / laser_texture_height * 2 - 1;
                float core = MathF.Exp(-across * across * 6);

                for (int x = 0; x < laser_texture_width; x++)
                {
                    float along = (x + 0.5f) / laser_texture_width;
                    float fade = MathF.Min(smoothStep(0, 0.15f, along), 1 - smoothStep(0.85f, 1, along) * 0.6f);

                    writePremultiplied(pixels, (y * laser_texture_width + x) * 4, colour, core * fade * opacity);
                }
            }

            return pixels;
        }

        /// <summary>
        /// A ring with a soft edge. Premultiplied alpha.
        /// </summary>
        private static byte[] reticlePixels()
        {
            byte[] pixels = new byte[reticle_texture_size * reticle_texture_size * 4];

            for (int y = 0; y < reticle_texture_size; y++)
            {
                for (int x = 0; x < reticle_texture_size; x++)
                {
                    float dx = (x + 0.5f) / reticle_texture_size * 2 - 1;
                    float dy = (y + 0.5f) / reticle_texture_size * 2 - 1;
                    float r = MathF.Sqrt(dx * dx + dy * dy);

                    float ring = MathF.Exp(-MathF.Pow((r - 0.7f) / 0.12f, 2));
                    float dot = 1 - smoothStep(0.12f, 0.2f, r);

                    writePremultiplied(pixels, (y * reticle_texture_size + x) * 4, new Vector3(0.75f, 0.9f, 1f), MathF.Max(ring, dot) * 0.6f);
                }
            }

            return pixels;
        }

        private static float smoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
            return t * t * (3 - 2 * t);
        }

        private static void writePremultiplied(byte[] pixels, int offset, Vector3 colour, float alpha)
        {
            alpha = Math.Clamp(alpha, 0, 1);
            pixels[offset] = (byte)(colour.X * alpha * 255);
            pixels[offset + 1] = (byte)(colour.Y * alpha * 255);
            pixels[offset + 2] = (byte)(colour.Z * alpha * 255);
            pixels[offset + 3] = (byte)(alpha * 255);
        }

        /// <summary>
        /// The GL texture (premultiplied alpha) holding the same image as the swapchain of a layer built by <see cref="BuildLayers"/>.
        /// Only available if textures were requested on construction.
        /// </summary>
        public int TextureFor(ulong swapchain)
        {
            if (swapchain == activeLaser) return activeLaserTexture;
            if (swapchain == inactiveLaser) return inactiveLaserTexture;
            if (swapchain == activeGhostLaser) return activeGhostLaserTexture;
            if (swapchain == inactiveGhostLaser) return inactiveGhostLaserTexture;

            return reticleTexture;
        }

        private static int createTexture(int width, int height, byte[] pixels)
        {
            // The framework's renderer caches texture bindings.
            var textureBindings = GLTextureBindings.Capture();

            int texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);

            fixed (byte* data = pixels)
                GL.TexImage2D(TextureTarget2d.Texture2D, 0, TextureComponentCount.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)data);

            textureBindings.Restore();
            return texture;
        }

        private ulong createStaticSwapchain(ulong session, long format, int width, int height, byte[] pixels)
        {
            var createInfo = new XrSwapchainCreateInfo
            {
                type = XR_TYPE_SWAPCHAIN_CREATE_INFO,
                createFlags = XrSwapchainCreateFlags.XR_SWAPCHAIN_CREATE_STATIC_IMAGE_BIT,
                usageFlags = XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_SAMPLED_BIT | XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT,
                format = format,
                sampleCount = 1,
                width = (uint)width,
                height = (uint)height,
                faceCount = 1,
                arraySize = 1,
                mipCount = 1,
            };

            ulong swapchain;
            var textureBindings = GLTextureBindings.Capture();
            xr.xrCreateSwapchain(session, &createInfo, &swapchain).ThrowIfFailed(nameof(xr.xrCreateSwapchain));
            textureBindings.Restore();

            uint imageCount;
            xr.xrEnumerateSwapchainImages(swapchain, 0, &imageCount, null).ThrowIfFailed(nameof(xr.xrEnumerateSwapchainImages));

            var images = new XrSwapchainImageOpenGLESKHR[imageCount];
            for (int i = 0; i < imageCount; i++)
                images[i].type = imageType;

            fixed (XrSwapchainImageOpenGLESKHR* p = images)
                xr.xrEnumerateSwapchainImages(swapchain, imageCount, &imageCount, (XrSwapchainImageBaseHeader*)p).ThrowIfFailed(nameof(xr.xrEnumerateSwapchainImages));

            // A static swapchain is acquired, written and released exactly once.
            var acquireInfo = new XrSwapchainImageAcquireInfo { type = XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
            uint index;
            xr.xrAcquireSwapchainImage(swapchain, &acquireInfo, &index).ThrowIfFailed(nameof(xr.xrAcquireSwapchainImage));

            var waitInfo = new XrSwapchainImageWaitInfo { type = XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO, timeout = XrConstants.XR_INFINITE_DURATION };
            xr.xrWaitSwapchainImage(swapchain, &waitInfo).ThrowIfFailed(nameof(xr.xrWaitSwapchainImage));

            // Restore the previous binding afterwards, as the framework's renderer caches texture bindings.
            GL.GetInteger(GetPName.TextureBinding2D, out int previousTexture);
            GL.BindTexture(TextureTarget.Texture2D, (int)images[index].image);

            fixed (byte* data = pixels)
                GL.TexSubImage2D(TextureTarget2d.Texture2D, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)data);

            GL.BindTexture(TextureTarget.Texture2D, previousTexture);

            var releaseInfo = new XrSwapchainImageReleaseInfo { type = XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
            xr.xrReleaseSwapchainImage(swapchain, &releaseInfo).ThrowIfFailed(nameof(xr.xrReleaseSwapchainImage));

            return swapchain;
        }

        public void Dispose()
        {
            xr.xrDestroySwapchain(activeLaser);
            xr.xrDestroySwapchain(inactiveLaser);
            xr.xrDestroySwapchain(reticle);
            xr.xrDestroySwapchain(activeGhostLaser);
            xr.xrDestroySwapchain(inactiveGhostLaser);
            NativeMemory.Free(layers);
        }
    }
}
