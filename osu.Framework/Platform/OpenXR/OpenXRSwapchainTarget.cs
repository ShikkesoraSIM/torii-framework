// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Drawing;
using System.Linq;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using osuTK.Graphics.ES30;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// An OpenXR swapchain the game renders into, with a GL framebuffer (colour + depth/stencil) per swapchain image.
    /// </summary>
    internal sealed unsafe class OpenXRSwapchainTarget : IDisposable
    {
        public const long GL_RGBA8 = 0x8058;
        public const long GL_SRGB8_ALPHA8 = 0x8C43;

        public ulong Swapchain { get; private set; }

        public Size Size { get; }

        /// <summary>
        /// Whether at least one image has been rendered and released, i.e. whether the swapchain has content to present.
        /// </summary>
        public bool HasReleasedImage { get; private set; }

        private readonly XrFunctions xr;
        private readonly int[] framebuffers;
        private readonly int[] textures;

        /// <summary>
        /// The GL texture of the currently acquired image, or 0 if none is acquired.
        /// </summary>
        public int CurrentTexture { get; private set; }
        private readonly int depthStencilRenderbuffer;
        private bool imageAcquired;

        /// <summary>
        /// Picks the swapchain format for gamma-encoded game output: sRGB (so the compositor interprets colours correctly, with sRGB write
        /// conversion disabled on the GL side so they're stored untouched) if the GL implementation can disable sRGB writes, else UNORM.
        /// </summary>
        public static long ChooseFormat(XrFunctions xr, ulong session, bool canDisableSrgbWrites)
        {
            uint count;
            xr.xrEnumerateSwapchainFormats(session, 0, &count, null).ThrowIfFailed(nameof(xr.xrEnumerateSwapchainFormats));
            long[] formats = new long[count];

            fixed (long* f = formats)
                xr.xrEnumerateSwapchainFormats(session, count, &count, f).ThrowIfFailed(nameof(xr.xrEnumerateSwapchainFormats));

            long format = canDisableSrgbWrites && formats.Contains(GL_SRGB8_ALPHA8) ? GL_SRGB8_ALPHA8 : GL_RGBA8;

            if (!formats.Contains(format))
                throw new InvalidOperationException($"No usable swapchain format (runtime offers: {string.Join(", ", formats.Select(x => $"0x{x:X}"))}).");

            return format;
        }

        public OpenXRSwapchainTarget(XrFunctions xr, ulong session, Size size, long format, XrStructureType imageType)
        {
            this.xr = xr;
            Size = size;

            var createInfo = new XrSwapchainCreateInfo
            {
                type = XR_TYPE_SWAPCHAIN_CREATE_INFO,
                usageFlags = XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_SAMPLED_BIT,
                format = format,
                sampleCount = 1,
                width = (uint)size.Width,
                height = (uint)size.Height,
                faceCount = 1,
                arraySize = 1,
                mipCount = 1,
            };

            // SteamVR fails swapchain creation (XR_ERROR_RUNTIME_FAILURE) if a GL error is already pending on the context.
            for (var e = GL.GetError(); e != ErrorCode.NoError; e = GL.GetError())
                Logger.Log($"OpenXR: discarding pending GL error {e} before creating a swapchain.");

            ulong created;
            var textureBindings = GLTextureBindings.Capture();
            xr.xrCreateSwapchain(session, &createInfo, &created).ThrowIfFailed($"{nameof(xr.xrCreateSwapchain)} ({size.Width}x{size.Height}, format 0x{format:X})");
            textureBindings.Restore();
            Swapchain = created;

            uint imageCount;
            xr.xrEnumerateSwapchainImages(Swapchain, 0, &imageCount, null).ThrowIfFailed(nameof(xr.xrEnumerateSwapchainImages));

            // GL and GLES swapchain image structures share a layout; only the type differs.
            var images = new XrSwapchainImageOpenGLESKHR[imageCount];
            for (int i = 0; i < imageCount; i++)
                images[i].type = imageType;

            fixed (XrSwapchainImageOpenGLESKHR* p = images)
                xr.xrEnumerateSwapchainImages(Swapchain, imageCount, &imageCount, (XrSwapchainImageBaseHeader*)p).ThrowIfFailed(nameof(xr.xrEnumerateSwapchainImages));

            // One depth-stencil buffer shared by all images (the framework's front-to-back pass uses depth testing).
            depthStencilRenderbuffer = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthStencilRenderbuffer);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferInternalFormat.Depth24Stencil8, size.Width, size.Height);

            framebuffers = new int[imageCount];
            textures = new int[imageCount];

            for (int i = 0; i < imageCount; i++)
            {
                textures[i] = (int)images[i].image;
                framebuffers[i] = GL.GenFramebuffer();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffers[i]);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget2d.Texture2D, (int)images[i].image, 0);
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depthStencilRenderbuffer);

                var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);

                if (status != FramebufferErrorCode.FramebufferComplete)
                {
                    // A status of 0 is an error rather than an incomplete framebuffer: usually that no GL context is current on this
                    // thread (every GL call is then ignored), which the version string tells apart.
                    string detail = GL.GetString(StringName.Version) == null ? "no GL context is current on this thread" : $"GL error {GL.GetError()}";
                    throw new InvalidOperationException($"Swapchain framebuffer {i} is incomplete: {status} ({detail}).");
                }
            }

            // The renderer rebinds its back buffer at the start of every frame.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        /// <summary>
        /// Acquires and waits for the next image.
        /// </summary>
        /// <returns>The GL framebuffer for the image.</returns>
        public int Acquire()
        {
            var acquireInfo = new XrSwapchainImageAcquireInfo { type = XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
            uint index;
            xr.xrAcquireSwapchainImage(Swapchain, &acquireInfo, &index).ThrowIfFailed(nameof(xr.xrAcquireSwapchainImage));

            var waitInfo = new XrSwapchainImageWaitInfo { type = XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO, timeout = XrConstants.XR_INFINITE_DURATION };
            xr.xrWaitSwapchainImage(Swapchain, &waitInfo).ThrowIfFailed(nameof(xr.xrWaitSwapchainImage));

            imageAcquired = true;
            CurrentTexture = textures[index];
            return framebuffers[index];
        }

        /// <summary>
        /// Releases the acquired image, if any.
        /// </summary>
        public void Release()
        {
            if (!imageAcquired)
                return;

            var releaseInfo = new XrSwapchainImageReleaseInfo { type = XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
            xr.xrReleaseSwapchainImage(Swapchain, &releaseInfo).ThrowIfFailed(nameof(xr.xrReleaseSwapchainImage));

            imageAcquired = false;
            CurrentTexture = 0;
            HasReleasedImage = true;
        }

        public XrSwapchainSubImage SubImage => new XrSwapchainSubImage
        {
            swapchain = Swapchain,
            imageRect = new XrRect2Di { extent = new XrExtent2Di { width = Size.Width, height = Size.Height } },
        };

        public void Dispose() => Dispose(false);

        /// <param name="deleteGraphics">
        /// Whether to delete the GL objects too. Only when the GL context is current on the calling thread, and worth it when the
        /// context lives on (a session being replaced by another, rather than the app closing).
        /// </param>
        public void Dispose(bool deleteGraphics)
        {
            if (Swapchain != 0)
                xr.xrDestroySwapchain(Swapchain);

            Swapchain = 0;

            if (!deleteGraphics || graphicsDeleted)
                return;

            graphicsDeleted = true;

            foreach (int framebuffer in framebuffers)
            {
                if (framebuffer != 0)
                    GL.DeleteFramebuffer(framebuffer);
            }

            if (depthStencilRenderbuffer != 0)
                GL.DeleteRenderbuffer(depthStencilRenderbuffer);
        }

        private bool graphicsDeleted;
    }
}
