// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Input.Handlers;
using osu.Framework.Platform.OpenXR;
using osu.Framework.Platform.SDL3;
using static SDL.SDL3;

namespace osu.Framework.Platform.Linux
{
    /// <summary>
    /// Torii: a Linux host that presents through OpenXR (e.g. Monado, which can simulate a headset and controllers) instead of its window.
    /// See <see cref="OpenXRHostController"/>.
    /// </summary>
    /// <remarks>
    /// Always uses the GL renderer on an EGL context, with single-threaded execution.
    /// The window stays as a regular desktop window, but is not presented to.
    /// </remarks>
    public class OpenXRLinuxGameHost : LinuxGameHost, IOpenXRHost
    {
        public OpenXRHostController OpenXR { get; }

        // Cached: converting a method group to a delegate allocates, and this is passed every frame.
        private readonly Action drawGame;

        public OpenXRLinuxGameHost(string gameName, HostOptions? options = null, Size? panelSize = null, string? debugDirectory = null)
            : base(gameName, options)
        {
            if (!FrameworkEnvironment.UseSDL3)
                throw new InvalidOperationException($"{nameof(OpenXRLinuxGameHost)} requires SDL3.");

            OpenXR = new OpenXRHostController(new EglDesktopOpenXRPlatform(gameName), panelSize ?? new Size(1920, 1080), debugDirectory);
            OpenXR.ExitRequested += Exit;
            drawGame = () => base.DrawFrame();
        }

        // The XR frame is waited for before the update and rendered in the draw, so both must run on one thread.
        protected override ExecutionMode? ForcedExecutionMode => ExecutionMode.SingleThread;

        protected override void ChooseAndSetupRenderer() => SetupRendererAndWindow("gl", GraphicsSurfaceType.OpenGL);

        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface)
        {
            var window = new SDL3OpenXRLinuxWindow(preferredSurface, Options.FriendlyGameName, BypassCompositor, OpenXR.PanelSize);
            OpenXR.IsDisplayed.BindValueChanged(d => window.ExternalIsActive.Value = d.NewValue, true);
            return window;
        }

        public override ExternalRenderTarget GetExternalRenderTarget(string name) => OpenXR.GetExternalRenderTarget(name);

        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers()
            => base.CreateAvailableInputHandlers().Prepend(OpenXR.ControllerInput);

        protected override void UpdateFrame()
        {
            OpenXR.BeforeUpdate();
            base.UpdateFrame();
        }

        protected override void DrawFrame() => OpenXR.Draw(Renderer, drawGame);

        protected override void Swap() => OpenXR.OnSwap();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                OpenXR.Dispose();

            base.Dispose(disposing);
        }

        /// <summary>
        /// The desktop window of an <see cref="OpenXRLinuxGameHost"/>: reports the panel size as its client size, and the XR session state
        /// as its activity (the window itself rarely has focus while the headset, or a simulator window, is in use).
        /// </summary>
        private class SDL3OpenXRLinuxWindow : SDL3LinuxWindow
        {
            private readonly Size panelSize;

            public readonly BindableBool ExternalIsActive = new BindableBool(true);

            public SDL3OpenXRLinuxWindow(GraphicsSurfaceType surfaceType, string appName, bool bypassCompositor, Size panelSize)
                : base(surfaceType, appName, bypassCompositor)
            {
                this.panelSize = panelSize;

                // OpenXR's EGL binding needs an EGL context, which SDL only uses by default on Wayland.
                SDL_SetHint(SDL_HINT_VIDEO_FORCE_EGL, "1"u8).LogErrorIfFailed();
            }

            public override Size ClientSize => panelSize;

            public override IBindable<bool> IsActive => ExternalIsActive;
        }
    }
}
