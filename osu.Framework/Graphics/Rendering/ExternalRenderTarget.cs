// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// A platform-provided render target which part of the draw tree can be redirected into (see
    /// <see cref="Containers.RenderTargetRedirectContainer"/>), e.g. a separate compositor layer in an immersive XR session.
    /// </summary>
    /// <remarks>
    /// Obtained through <see cref="Platform.GameHost.GetExternalRenderTarget"/>. The platform activates it per frame by setting
    /// <see cref="NativeFramebuffer"/> before the frame is drawn; while inactive, redirected content is drawn in place as usual.
    /// The target has the same size and coordinate space as the window, so redirected content keeps its screen-space layout.
    /// </remarks>
    public sealed class ExternalRenderTarget
    {
        /// <summary>
        /// The name this target was requested with.
        /// </summary>
        public string Name { get; }

        private int? nativeFramebuffer;

        /// <summary>
        /// The native (GL) framebuffer to draw redirected content into this frame, or <see langword="null"/> if inactive.
        /// Set by the platform on the draw thread before drawing each frame. Setting it resets <see cref="HasContent"/>.
        /// </summary>
        public int? NativeFramebuffer
        {
            get => nativeFramebuffer;
            set
            {
                nativeFramebuffer = value;
                HasContent = false;
            }
        }

        /// <summary>
        /// Whether anything was drawn into (and the target cleared for) the current activation.
        /// Platforms should not present a target which has no content, as its framebuffer wasn't cleared.
        /// </summary>
        public bool HasContent { get; internal set; }

        /// <summary>
        /// Whether the platform isn't showing this target for now (e.g. an XR background layer while the room is shown instead).
        /// Content redirected into a hidden target isn't drawn at all.
        /// </summary>
        public bool Hidden { get; internal set; }

        public ExternalRenderTarget(string name)
        {
            Name = name;
        }
    }
}
