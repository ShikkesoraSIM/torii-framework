// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Drawing;
using osuTK.Graphics.ES30;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// A GL framebuffer of the game's own (colour + depth/stencil) which the framework can draw a frame into, like a swapchain image.
    /// Created and used on the GL thread; goes with the context.
    /// </summary>
    internal sealed unsafe class OffscreenTarget
    {
        public int Framebuffer { get; }

        private readonly Size size;

        public OffscreenTarget(Size size)
        {
            this.size = size;

            // The framework caches the bindings changed here.
            var textureBindings = GLTextureBindings.Capture();
            GL.GetInteger(GetPName.FramebufferBinding, out int previousFramebuffer);

            int texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget2d.Texture2D, 0, TextureComponentCount.Rgba8, size.Width, size.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            int depthStencil = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthStencil);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferInternalFormat.Depth24Stencil8, size.Width, size.Height);

            Framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget2d.Texture2D, texture, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depthStencil);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            textureBindings.Restore();

            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException($"Offscreen framebuffer is incomplete: {status}.");
        }

        /// <summary>
        /// Copies the image to the context's default framebuffer of the given size, scaled to fit it with its aspect ratio kept.
        /// </summary>
        public void BlitToBackbuffer(Size backbufferSize)
        {
            float scale = Math.Min((float)backbufferSize.Width / size.Width, (float)backbufferSize.Height / size.Height);
            int width = (int)(size.Width * scale), height = (int)(size.Height * scale);
            int x = (backbufferSize.Width - width) / 2, y = (backbufferSize.Height - height) / 2;

            // The framework caches the state changed here.
            GL.GetInteger(GetPName.FramebufferBinding, out int previousFramebuffer);
            bool scissor = GL.IsEnabled(EnableCap.ScissorTest);
            float* clearColour = stackalloc float[4];
            GL.GetFloat(GetPName.ColorClearValue, clearColour);

            if (scissor) GL.Disable(EnableCap.ScissorTest);

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, Framebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);

            if (width != backbufferSize.Width || height != backbufferSize.Height)
            {
                GL.ClearColor(0, 0, 0, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.ClearColor(clearColour[0], clearColour[1], clearColour[2], clearColour[3]);
            }

            GL.BlitFramebuffer(0, 0, size.Width, size.Height, x, y, x + width, y + height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            if (scissor) GL.Enable(EnableCap.ScissorTest);
        }
    }
}
