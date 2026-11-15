// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// What surrounds the game panel in an immersive session.
    /// </summary>
    public enum XrEnvironmentMode
    {
        /// <summary>
        /// Black void; the game's background layer floats behind the panel.
        /// </summary>
        Void,

        /// <summary>
        /// The real room (camera passthrough) replaces the game's background layer.
        /// </summary>
        Passthrough,

        /// <summary>
        /// A rendered 3D environment around the game's background layer.
        /// </summary>
        Field,
    }

    /// <summary>
    /// Full-view camera passthrough (XR_FB_passthrough), submitted as the bottom composition layer.
    /// </summary>
    internal sealed unsafe class OpenXRPassthrough : IDisposable
    {
        private readonly XrFunctions xr;
        private ulong passthrough;
        private ulong layer;
        private bool running;
        private float opacity = -1;

        public OpenXRPassthrough(XrFunctions xr, ulong session)
        {
            this.xr = xr;

            var createInfo = new XrPassthroughCreateInfoFB { type = XR_TYPE_PASSTHROUGH_CREATE_INFO_FB };
            ulong created;
            xr.xrCreatePassthroughFB(session, &createInfo, &created).ThrowIfFailed(nameof(xr.xrCreatePassthroughFB));
            passthrough = created;

            var layerInfo = new XrPassthroughLayerCreateInfoFB
            {
                type = XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB,
                passthrough = passthrough,
                purpose = XrPassthroughLayerPurposeFB.XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB,
            };

            xr.xrCreatePassthroughLayerFB(session, &layerInfo, &created).ThrowIfFailed(nameof(xr.xrCreatePassthroughLayerFB));
            layer = created;

            Logger.Log("OpenXR: passthrough created.");
        }

        /// <summary>
        /// Starts or pauses the cameras. Paused passthrough costs nothing.
        /// </summary>
        public void SetRunning(bool value)
        {
            if (value == running)
                return;

            if (value)
            {
                xr.xrPassthroughStartFB(passthrough).ThrowIfFailed(nameof(xr.xrPassthroughStartFB));
                xr.xrPassthroughLayerResumeFB(layer).ThrowIfFailed(nameof(xr.xrPassthroughLayerResumeFB));
            }
            else
            {
                xr.xrPassthroughLayerPauseFB(layer).ThrowIfFailed(nameof(xr.xrPassthroughLayerPauseFB));
                xr.xrPassthroughPauseFB(passthrough).ThrowIfFailed(nameof(xr.xrPassthroughPauseFB));
            }

            running = value;
        }

        /// <summary>
        /// Sets how strongly the room shows: 1 = full, lower values fade it towards black (used as a "dim").
        /// </summary>
        public void SetOpacity(float value)
        {
            value = Math.Clamp(value, 0, 1);

            if (Math.Abs(value - opacity) < 0.005f)
                return;

            var style = new XrPassthroughStyleFB
            {
                type = XR_TYPE_PASSTHROUGH_STYLE_FB,
                textureOpacityFactor = value,
                edgeColor = new XrColor4f(),
            };

            xr.xrPassthroughLayerSetStyleFB(layer, &style).ThrowIfFailed(nameof(xr.xrPassthroughLayerSetStyleFB));
            opacity = value;
        }

        /// <summary>
        /// The composition layer to submit (first, so everything else is drawn over the room).
        /// </summary>
        public XrCompositionLayerPassthroughFB Layer => new XrCompositionLayerPassthroughFB
        {
            type = XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB,
            flags = XrCompositionLayerFlags.XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT,
            layerHandle = layer,
        };

        public void Dispose()
        {
            if (layer != 0) xr.xrDestroyPassthroughLayerFB(layer);
            if (passthrough != 0) xr.xrDestroyPassthroughFB(passthrough);
            layer = passthrough = 0;
        }
    }
}
