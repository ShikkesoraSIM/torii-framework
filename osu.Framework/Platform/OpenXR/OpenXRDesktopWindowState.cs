// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Drawing;
using osu.Framework.Bindables;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// What the desktop window of a host presenting through OpenXR reports in place of its own state. Shared by the platforms' window
    /// classes, which can't share a base class.
    /// </summary>
    internal sealed class OpenXRDesktopWindowState
    {
        /// <summary>
        /// The panel's size, reported as the window's client size.
        /// </summary>
        public Size ClientSize { get; }

        /// <summary>
        /// Whether the game is being displayed in the headset, reported as the window being active (the window itself rarely has focus
        /// while the headset is in use).
        /// </summary>
        public IBindable<bool> IsActive => isActive;

        /// <summary>
        /// Always true: the pointer is a VR controller aimed at the panel, wherever the desktop's mouse cursor is, and presses and
        /// hovering are ignored while the cursor is outside the window.
        /// </summary>
        public IBindable<bool> CursorInWindow { get; } = new BindableBool(true);

        private readonly BindableBool isActive = new BindableBool(true);
        private readonly Action<string> setWindowTitle;
        private string title;

        /// <param name="xr">The host's OpenXR controller.</param>
        /// <param name="title">The window's initial title.</param>
        /// <param name="setWindowTitle">Sets the actual title of the window.</param>
        public OpenXRDesktopWindowState(OpenXRHostController xr, string title, Action<string> setWindowTitle)
        {
            this.title = title;
            this.setWindowTitle = setWindowTitle;

            ClientSize = xr.PanelSize;

            isActive.BindValueChanged(_ => applyTitle());
            xr.IsDisplayed.BindValueChanged(displayed => isActive.Value = displayed.NewValue, true);
        }

        /// <summary>
        /// The title set by the game. The window's actual title also says when the game isn't being displayed in the headset (e.g. it's
        /// idle and the runtime is in standby): the window itself shows nothing, and the game would otherwise just look frozen.
        /// </summary>
        public string Title
        {
            get => title;
            set
            {
                title = value;
                applyTitle();
            }
        }

        private void applyTitle() => setWindowTitle(isActive.Value ? title : $"{title} (VR: waiting for the headset)");
    }
}
