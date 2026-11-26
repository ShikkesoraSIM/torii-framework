// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace osu.Framework.Platform.OpenXR.Native
{
    /// <summary>
    /// Table of OpenXR entry points, resolved through the loader's <c>xrGetInstanceProcAddr</c>.
    /// </summary>
    /// <remarks>
    /// Usage: construct with the loaded loader library, call <see cref="Load"/> with <c>XR_NULL_HANDLE</c> (0) to resolve the
    /// pre-instance functions (<c>xrCreateInstance</c>, <c>xrEnumerateInstanceExtensionProperties</c>, <c>xrInitializeLoaderKHR</c>),
    /// then call <see cref="Load"/> again with the created instance to resolve everything else.
    /// </remarks>
    public unsafe partial class XrFunctions
    {
        private readonly delegate* unmanaged<ulong, byte*, void**, XrResult> getInstanceProcAddr;

        public XrFunctions(IntPtr loaderLibrary)
        {
            getInstanceProcAddr = (delegate* unmanaged<ulong, byte*, void**, XrResult>)NativeLibrary.GetExport(loaderLibrary, "xrGetInstanceProcAddr");
        }

        /// <summary>
        /// The only functions which may be resolved without an instance (OpenXR spec, xrGetInstanceProcAddr).
        /// </summary>
        private static readonly string[] global_functions =
        {
            "xrEnumerateApiLayerProperties",
            "xrEnumerateInstanceExtensionProperties",
            "xrCreateInstance",
            "xrInitializeLoaderKHR",
        };

        private void* Resolve(ulong instance, string name)
        {
            if (instance == 0 && Array.IndexOf(global_functions, name) < 0)
                return null;

            int length = Encoding.UTF8.GetByteCount(name);
            byte* utf8 = stackalloc byte[length + 1];
            Encoding.UTF8.GetBytes(name, new Span<byte>(utf8, length));
            utf8[length] = 0;

            void* function = null;
            return getInstanceProcAddr(instance, utf8, &function) == XrResult.XR_SUCCESS ? function : null;
        }
    }
}
