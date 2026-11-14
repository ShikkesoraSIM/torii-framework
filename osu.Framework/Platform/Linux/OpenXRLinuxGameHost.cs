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

        private SDL3OpenXRLinuxWindow? window;

        // Cached: converting a method group to a delegate allocates, and this is passed every frame.
        private readonly Action drawGame;

        public OpenXRLinuxGameHost(string gameName, HostOptions? options = null, Size? panelSize = null, string? debugDirectory = null)
            : base(gameName, options)
        {
            if (!FrameworkEnvironment.UseSDL3)
                throw new InvalidOperationException($"{nameof(OpenXRLinuxGameHost)} requires SDL3.");

            OpenXR = new OpenXRHostController(new EglDesktopOpenXRPlatform(gameName), panelSize ?? new Size(1920, 1080), debugDirectory);
            // (Unless the game deals with the session ending itself, e.g. by carrying on outside VR.)
            OpenXR.ExitRequested += () =>
            {
                if (OpenXR.ExitWhenSessionEnds)
                    Exit();
            };
            OpenXR.PresentsDesktopView = true;
            drawGame = () => base.DrawFrame();
        }

        // The XR frame is waited for before the update and rendered in the draw, so both must run on one thread.
        protected override ExecutionMode? ForcedExecutionMode => ExecutionMode.SingleThread;

        protected override void ChooseAndSetupRenderer() => SetupRendererAndWindow("gl", GraphicsSurfaceType.OpenGL);

        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface)
            => window = new SDL3OpenXRLinuxWindow(preferredSurface, Options.FriendlyGameName, BypassCompositor, OpenXR);

        public override ExternalRenderTarget GetExternalRenderTarget(string name) => OpenXR.GetExternalRenderTarget(name);

        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers()
            => base.CreateAvailableInputHandlers().Prepend(OpenXR.ControllerInput);

        protected override void UpdateFrame()
        {
            OpenXR.BeforeUpdate();
            base.UpdateFrame();
        }

        protected override void DrawFrame() => OpenXR.Draw(Renderer, drawGame);

        protected override void Swap()
        {
            OpenXR.OnSwap();

            // The window shows the game's desktop view (see OpenXRHostController.DESKTOP_VIEW), every few frames.
            if (window == null || !OpenXR.BlitDesktopView(window.WindowSize))
                return;

            // Never wait for the monitor: frames are paced by the headset.
            if (Renderer.VerticalSync)
                Renderer.VerticalSync = false;

            base.Swap();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                OpenXR.Dispose();

            base.Dispose(disposing);
        }

        /// <summary>
        /// The desktop window of an <see cref="OpenXRLinuxGameHost"/>. See <see cref="OpenXRDesktopWindowState"/>.
        /// </summary>
        private class SDL3OpenXRLinuxWindow : SDL3LinuxWindow
        {
            private readonly OpenXRDesktopWindowState state;

            public SDL3OpenXRLinuxWindow(GraphicsSurfaceType surfaceType, string appName, bool bypassCompositor, OpenXRHostController xr)
                : base(surfaceType, appName, bypassCompositor)
            {
                state = new OpenXRDesktopWindowState(xr, base.Title, title => base.Title = title, () => base.ClientSize);

                // OpenXR's EGL binding needs an EGL context, which SDL only uses by default on Wayland.
                SDL_SetHint(SDL_HINT_VIDEO_FORCE_EGL, "1"u8).LogErrorIfFailed();
            }

            public override Size ClientSize => state.ClientSize;

            public override IBindable<bool> IsActive => state.IsActive;

            public override IBindable<bool> CursorInWindow => state.CursorInWindow;

            /// <summary>
            /// The actual client size of the window, in pixels.
            /// </summary>
            public Size WindowSize => state.WindowSize;

            protected override osuTK.Vector2 TransformPointerPosition(osuTK.Vector2 position) => state.ToPanel(position);

            public override string Title
            {
                get => state.Title;
                set => state.Title = value;
            }
        }
    }
}
