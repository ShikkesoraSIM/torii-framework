// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Diagnostics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Graphics
{
    /// <summary>
    /// Contains data which is shared between all <see cref="BufferedDrawNode"/>s of a <see cref="Drawable"/>.
    /// </summary>
    /// <remarks>
    /// This should be constructed _once_ per <see cref="Drawable"/>, and given to the constructor of <see cref="BufferedDrawNode"/>.
    /// </remarks>
    public class BufferedDrawNodeSharedData : IDisposable
    {
        /// <summary>
        /// The version of drawn contents currently present in <see cref="MainBuffer"/> and <see cref="effectBuffers"/>.
        /// This should only be modified by <see cref="BufferedDrawNode"/>.
        /// </summary>
        internal long DrawVersion = -1;

        /// <summary>
        /// Whether the frame buffer position should be snapped to the nearest pixel when blitting.
        /// This amounts to setting the texture filtering mode to "nearest".
        /// </summary>
        public readonly bool PixelSnapping;

        /// <summary>
        /// Whether the frame buffer should be clipped to be contained in the root node.
        /// </summary>
        public readonly bool ClipToRootNode;

        public bool IsInitialised { get; private set; }

        /// <summary>
        /// A set of <see cref="IFrameBuffer"/>s which are used in a ping-pong manner to render effects to.
        /// </summary>
        private readonly IFrameBuffer[] effectBuffers;

        private readonly RenderBufferFormat[] mainBufferFormats;
        private readonly TextureFilteringMode filterMode;
        private readonly TexturePixelFormat textureFormat;

        private IRenderer renderer;
        private IFrameBuffer mainBuffer;

        /// <summary>
        /// Creates a new <see cref="BufferedDrawNodeSharedData"/> with no effect buffers.
        /// </summary>
        public BufferedDrawNodeSharedData(TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float, RenderBufferFormat[] formats = null, bool pixelSnapping = false, bool clipToRootNode = false)
            : this(0, textureFormat, formats, pixelSnapping, clipToRootNode)
        {
        }

        /// <summary>
        /// Creates a new <see cref="BufferedDrawNodeSharedData"/> with a specific amount of effect buffers.
        /// </summary>
        /// <param name="effectBufferCount">The number of effect buffers.</param>
        /// <param name="textureFormat">The main render buffer format.</param>
        /// <param name="mainBufferFormats">The render buffer formats to attach to the main frame buffer.</param>
        /// <param name="pixelSnapping">Whether the frame buffer position should be snapped to the nearest pixel when blitting.
        /// This amounts to setting the texture filtering mode to "nearest".</param>
        /// <param name="clipToRootNode">Whether the frame buffer should be clipped to be contained in the root node..</param>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="effectBufferCount"/> is less than 0.</exception>
        public BufferedDrawNodeSharedData(int effectBufferCount, TexturePixelFormat textureFormat, RenderBufferFormat[] mainBufferFormats = null, bool pixelSnapping = false, bool clipToRootNode = false)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(effectBufferCount);

            this.textureFormat = textureFormat;
            this.mainBufferFormats = mainBufferFormats;
            PixelSnapping = pixelSnapping;
            ClipToRootNode = clipToRootNode;
            filterMode = PixelSnapping ? TextureFilteringMode.Nearest : TextureFilteringMode.Linear;

            effectBuffers = new IFrameBuffer[effectBufferCount];
        }

        /// <summary>
        /// The <see cref="IFrameBuffer"/> which contains the original version of the rendered <see cref="Drawable"/>.
        /// </summary>
        public IFrameBuffer MainBuffer => mainBuffer ??= renderer.CreateFrameBuffer(textureFormat, mainBufferFormats, filterMode);

        public void Initialise(IRenderer renderer)
        {
            this.renderer = renderer;
            IsInitialised = true;
        }

        private int currentEffectBuffer = -1;

        /// <summary>
        /// The <see cref="IFrameBuffer"/> which contains the most up-to-date drawn effect.
        /// </summary>
        public IFrameBuffer CurrentEffectBuffer => currentEffectBuffer == -1 ? MainBuffer : getEffectBufferAtIndex(currentEffectBuffer);

        /// <summary>
        /// Retrieves the next <see cref="IFrameBuffer"/> which effects can be rendered to.
        /// </summary>
        /// <exception cref="InvalidOperationException">If there are no available effect buffers.</exception>
        public IFrameBuffer GetNextEffectBuffer()
        {
            if (effectBuffers.Length == 0)
                throw new InvalidOperationException($"The {nameof(BufferedDrawNode)} requested an effect buffer, but none were available.");

            if (++currentEffectBuffer >= effectBuffers.Length)
                currentEffectBuffer = 0;

            return getEffectBufferAtIndex(currentEffectBuffer);
        }

        private IFrameBuffer getEffectBufferAtIndex(int index) => effectBuffers[index] ??= renderer.CreateFrameBuffer(filteringMode: filterMode);

        /// <summary>
        /// Resets <see cref="CurrentEffectBuffer"/>.
        /// This should only be called by <see cref="BufferedDrawNode"/>.
        /// </summary>
        internal void ResetCurrentEffectBuffer() => currentEffectBuffer = -1;

        /// <summary>
        /// Torii: whether the frame buffers are released when not drawn for a while. Set once, before the first draw.
        /// </summary>
        internal bool ReleaseWhenNotDrawn;

        /// <summary>
        /// Torii: whether <see cref="IRenderer"/> is tracking when these buffers were last drawn, to release them once unused
        /// (see <see cref="Containers.BufferedContainer{T}.ReleaseFrameBufferWhenNotDrawn"/>). Only accessed on the draw thread.
        /// </summary>
        internal bool ReleaseTracked;

        /// <summary>
        /// Torii: the <see cref="IRenderer.FrameIndex"/> these buffers were last drawn in. Only accessed on the draw thread.
        /// </summary>
        internal ulong LastDrawnFrame;

        private bool isDisposed;

        /// <summary>
        /// Torii: frees the frame buffers, which are created again (and redrawn) the next time they're drawn.
        /// Must be called on the draw thread, once no queued GPU frame can still use them.
        /// </summary>
        internal void ReleaseFrameBuffers()
        {
            if (isDisposed)
                return;

            mainBuffer?.Dispose();
            mainBuffer = null;

            for (int i = 0; i < effectBuffers.Length; i++)
            {
                effectBuffers[i]?.Dispose();
                effectBuffers[i] = null;
            }

            currentEffectBuffer = -1;
            DrawVersion = -1;
        }

        public void Dispose()
        {
            renderer?.ScheduleDisposal(static d => d.Dispose(true), this);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            Debug.Assert(IsInitialised);

            isDisposed = true;

            // Torii: the field, not MainBuffer, which would create a buffer just to dispose it (e.g. after ReleaseFrameBuffers()).
            mainBuffer?.Dispose();
            for (int i = 0; i < effectBuffers.Length; i++)
                effectBuffers[i]?.Dispose();
        }
    }
}
