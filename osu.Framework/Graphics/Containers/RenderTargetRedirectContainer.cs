// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.Rendering;
using osuTK.Graphics;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// A container whose subtree is drawn into an <see cref="ExternalRenderTarget"/> instead of the window, while that target is active.
    /// </summary>
    /// <remarks>
    /// The redirection swaps which framebuffer the renderer treats as the back buffer, so redirected content is drawn exactly as it would be
    /// on screen (same orientation, scissoring and depth handling), just into a different image. Several containers may share a target;
    /// draw order within a target is preserved. Content nested inside an off-screen buffer (e.g. a <see cref="BufferedContainer"/>) is not
    /// redirected. Currently only supported by the OpenGL renderer; elsewhere the subtree is drawn in place.
    /// </remarks>
    public partial class RenderTargetRedirectContainer : Container
    {
        private ExternalRenderTarget? target;

        /// <summary>
        /// The target to draw into, or <see langword="null"/> to draw in place.
        /// </summary>
        public ExternalRenderTarget? Target
        {
            get => target;
            set
            {
                if (target == value)
                    return;

                target = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        protected override DrawNode CreateDrawNode() => new RedirectDrawNode(this);

        private class RedirectDrawNode : CompositeDrawableDrawNode
        {
            protected new RenderTargetRedirectContainer Source => (RenderTargetRedirectContainer)base.Source;

            private ExternalRenderTarget? target;

            public RedirectDrawNode(RenderTargetRedirectContainer source)
                : base(source)
            {
            }

            public override void ApplyState()
            {
                base.ApplyState();
                target = Source.Target;
            }

            protected override void Draw(IRenderer renderer)
            {
                // Not shown, so not worth drawing (in place neither: the content belongs to the target).
                if (target?.Hidden == true)
                    return;

                int? previous = beginRedirect(renderer);
                base.Draw(renderer);
                endRedirect(renderer, previous);
            }

            protected override void DrawOpaqueInterior(IRenderer renderer)
            {
                if (target?.Hidden == true)
                    return;

                int? previous = beginRedirect(renderer);
                base.DrawOpaqueInterior(renderer);
                endRedirect(renderer, previous);
            }

            /// <returns>The previous redirect, to restore afterwards.</returns>
            private int? beginRedirect(IRenderer renderer)
            {
                if (target?.NativeFramebuffer is not int framebuffer || renderer is not GLRenderer glRenderer || !renderer.UsingBackbuffer)
                    return null;

                int? previous = glRenderer.BackbufferRedirect;
                glRenderer.SetBackbufferRedirect(framebuffer);

                // The first draw into the target this frame clears it (colour and depth), so stale content from a previous use of the
                // native image never shows.
                if (!target.HasContent)
                {
                    // Transparent black: layers are composited as premultiplied alpha (osuTK's Color4.Transparent is transparent *white*).
                    renderer.Clear(new ClearInfo(new Color4(0, 0, 0, 0)));
                    target.HasContent = true;
                }

                return previous;
            }

            private void endRedirect(IRenderer renderer, int? previous)
            {
                if (target?.NativeFramebuffer is int && renderer is GLRenderer glRenderer && renderer.UsingBackbuffer)
                    glRenderer.SetBackbufferRedirect(previous);
            }
        }
    }
}
