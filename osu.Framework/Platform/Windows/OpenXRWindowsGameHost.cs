// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Input.Handlers;
using osu.Framework.Platform.OpenXR;

namespace osu.Framework.Platform.Windows
{
    /// <summary>
    /// Torii: a Windows host that presents through OpenXR (e.g. SteamVR, Meta Quest Link, Virtual Desktop) instead of its window.
    /// See <see cref="OpenXRHostController"/>.
    /// </summary>
    /// <remarks>
    /// Always uses the GL renderer (on a WGL context), with single-threaded execution.
    /// The window stays as a regular desktop window, but is not presented to.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public class OpenXRWindowsGameHost : WindowsGameHost, IOpenXRHost
    {
        public OpenXRHostController OpenXR { get; }

        // Cached: converting a method group to a delegate allocates, and this is passed every frame.
        private readonly Action drawGame;

        public OpenXRWindowsGameHost(string gameName, HostOptions? options = null, Size? panelSize = null, string? debugDirectory = null)
            : base(gameName, options)
        {
            if (!FrameworkEnvironment.UseSDL3)
                throw new InvalidOperationException($"{nameof(OpenXRWindowsGameHost)} requires SDL3.");

            OpenXR = new OpenXRHostController(new WglDesktopOpenXRPlatform(gameName), panelSize ?? new Size(1920, 1080), debugDirectory);
            OpenXR.ExitRequested += Exit;
            drawGame = () => base.DrawFrame();
        }

        // The XR frame is waited for before the update and rendered in the draw, so both must run on one thread.
        protected override ExecutionMode? ForcedExecutionMode => ExecutionMode.SingleThread;

        protected override void ChooseAndSetupRenderer() => SetupRendererAndWindow("gl", GraphicsSurfaceType.OpenGL);

        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface)
        {
            var window = new SDL3OpenXRWindowsWindow(preferredSurface, Options.FriendlyGameName, OpenXR.PanelSize);
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

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
                OpenXR.Dispose();

            base.Dispose(isDisposing);
        }

        /// <summary>
        /// The desktop window of an <see cref="OpenXRWindowsGameHost"/>: reports the panel size as its client size, and the XR session
        /// state as its activity (the window itself rarely has focus while the headset is in use).
        /// </summary>
        private class SDL3OpenXRWindowsWindow : SDL3WindowsWindow
        {
            private readonly Size panelSize;

            public readonly BindableBool ExternalIsActive = new BindableBool(true);

            private string gameTitle = string.Empty;

            public SDL3OpenXRWindowsWindow(GraphicsSurfaceType surfaceType, string appName, Size panelSize)
                : base(surfaceType, appName)
            {
                this.panelSize = panelSize;
                gameTitle = base.Title;
                ExternalIsActive.BindValueChanged(_ => base.Title = OpenXRHostController.WindowTitle(gameTitle, ExternalIsActive.Value));
            }

            public override Size ClientSize => panelSize;

            public override IBindable<bool> IsActive => ExternalIsActive;

            // The pointer is a VR controller aimed at the panel, wherever the desktop's mouse cursor is: presses and hovering are
            // ignored while the cursor is outside the window.
            public override IBindable<bool> CursorInWindow { get; } = new BindableBool(true);

            public override string Title
            {
                get => gameTitle;
                set => base.Title = OpenXRHostController.WindowTitle(gameTitle = value, ExternalIsActive.Value);
            }
        }
    }
}
