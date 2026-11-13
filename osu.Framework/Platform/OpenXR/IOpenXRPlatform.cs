// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Platform.OpenXR.Native;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// The platform-specific parts of running an OpenXR session on the game's GL context: how the loader is initialised, which extensions
    /// bind the graphics API, and how the current GL context is handed to the runtime.
    /// </summary>
    public unsafe interface IOpenXRPlatform
    {
        /// <summary>
        /// The application name reported to the runtime.
        /// </summary>
        string ApplicationName { get; }

        /// <summary>
        /// Instance extensions this platform needs (e.g. the graphics binding).
        /// </summary>
        IEnumerable<string> RequiredExtensions { get; }

        /// <summary>
        /// Initialises the loader if the platform requires it (e.g. Android). Called before any other OpenXR call.
        /// </summary>
        void InitialiseLoader(XrFunctions xr);

        /// <summary>
        /// Allocates the structure chained to <see cref="XrInstanceCreateInfo.next"/>, or returns null. Freed with <see cref="Free"/>.
        /// </summary>
        void* AllocateInstanceCreateNext();

        /// <summary>
        /// Queries the runtime's graphics requirements, which the spec requires before creating a session.
        /// </summary>
        void CheckGraphicsRequirements(XrFunctions xr, ulong instance, ulong systemId);

        /// <summary>
        /// Allocates the graphics binding for the GL context current on the calling thread. Freed with <see cref="Free"/>.
        /// </summary>
        void* AllocateGraphicsBinding();

        void Free(void* structure);

        /// <summary>
        /// The structure type of swapchain images (GL and GLES images share a layout).
        /// </summary>
        XrStructureType SwapchainImageType { get; }

        /// <summary>
        /// Whether the GL context can write gamma-encoded values into sRGB swapchain images untouched (sRGB write conversion disabled).
        /// Called on the GL thread.
        /// </summary>
        bool CanDisableSrgbWrites { get; }
    }
}
