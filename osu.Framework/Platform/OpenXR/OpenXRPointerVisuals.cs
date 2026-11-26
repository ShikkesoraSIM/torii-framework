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
    /// Where they go and what they look like comes from <see cref="PointerBeams"/>.
    /// </remarks>
    internal sealed unsafe class OpenXRPointerVisuals : IDisposable
    {
        public const int MAX_LAYERS = 7;

        // One static swapchain per image; and the same images as plain GL textures (if asked for), for drawing the visuals rather than
        // submitting them as layers (see TextureFor).
        private static readonly PointerImage[] images = Enum.GetValues<PointerImage>();
        private readonly ulong[] swapchains = new ulong[images.Length];
        private readonly int[] textures = new int[images.Length];

        private readonly XrFunctions xr;
        private readonly ulong space;

        private readonly XrCompositionLayerQuad* layers;

        private readonly XrStructureType imageType;

        /// <param name="createTextures">Whether the visuals may be drawn by the caller (see <see cref="TextureFor"/>).</param>
        public OpenXRPointerVisuals(XrFunctions xr, ulong session, ulong space, long format, XrStructureType imageType, bool createTextures = false)
        {
            this.imageType = imageType;
            this.xr = xr;
            this.space = space;

            foreach (var image in images)
            {
                byte[] pixels = PointerBeams.CreatePixels(image, out int width, out int height);

                swapchains[(int)image] = createStaticSwapchain(session, format, width, height, pixels);

                if (createTextures)
                    textures[(int)image] = createTexture(width, height, pixels);
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
            Span<PointerQuad> quads = stackalloc PointerQuad[MAX_LAYERS];

            int count = PointerBeams.Build(hands, activeHand, headPosition, panel.Panel, edgeTolerance, laserStartOffset, false, true, quads, panel.PointerOutOfReach,
                panel.IdleControllers);

            // Where a ghost points is shown by what it pointed at (e.g. a replay's cursor): beams only.
            if (ghosts != null)
                count += PointerBeams.Build(ghosts.Hands, ghosts.ActiveHand, headPosition, panel.Panel, edgeTolerance, ghosts.HoldingModel ? 0 : 0.02f, true, false, quads[count..]);

            for (int i = 0; i < count; i++)
            {
                ref readonly var q = ref quads[i];
                bool reticle = q.Image == PointerImage.Reticle;

                layers[i] = quad(swapchains[(int)q.Image], reticle ? PointerBeams.RETICLE_TEXTURE_SIZE : PointerBeams.LASER_TEXTURE_WIDTH,
                    reticle ? PointerBeams.RETICLE_TEXTURE_SIZE : PointerBeams.LASER_TEXTURE_HEIGHT, q.Centre, q.Orientation, q.Size);
                output[i] = (XrCompositionLayerBaseHeader*)&layers[i];
            }

            return count;
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
        /// The GL texture (premultiplied alpha) holding the same image as the swapchain of a layer built by <see cref="BuildLayers"/>.
        /// Only available if textures were requested on construction.
        /// </summary>
        public int TextureFor(ulong swapchain) => textures[Array.IndexOf(swapchains, swapchain)];

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
            foreach (ulong swapchain in swapchains)
                xr.xrDestroySwapchain(swapchain);

            NativeMemory.Free(layers);
        }
    }
}
