// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// The small subset of EGL needed to hand the game's GL context (created by SDL) to an OpenXR runtime.
    /// </summary>
    internal static unsafe class Egl
    {
        public const int EGL_CONFIG_ID = 0x3028;
        public const int EGL_NONE = 0x3038;

        private static IntPtr library;

        private static delegate* unmanaged<IntPtr> eglGetCurrentDisplay;
        private static delegate* unmanaged<IntPtr> eglGetCurrentContext;
        private static delegate* unmanaged<IntPtr, IntPtr, int, int*, uint> eglQueryContext;
        private static delegate* unmanaged<IntPtr, int*, IntPtr*, int, int*, uint> eglChooseConfig;

        /// <summary>
        /// Address of eglGetProcAddress (for runtimes which resolve GL functions themselves, e.g. XR_MNDX_egl_enable).
        /// </summary>
        public static IntPtr GetProcAddressFunction { get; private set; }

        private static void ensureLoaded()
        {
            if (library != IntPtr.Zero)
                return;

            // Android ships libEGL.so; desktop Linux usually only libEGL.so.1 (the unversioned name comes with development packages).
            if (!NativeLibrary.TryLoad("libEGL.so", out library) && !NativeLibrary.TryLoad("libEGL.so.1", out library))
                throw new DllNotFoundException("libEGL could not be loaded.");

            eglGetCurrentDisplay = (delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(library, "eglGetCurrentDisplay");
            eglGetCurrentContext = (delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(library, "eglGetCurrentContext");
            eglQueryContext = (delegate* unmanaged<IntPtr, IntPtr, int, int*, uint>)NativeLibrary.GetExport(library, "eglQueryContext");
            eglChooseConfig = (delegate* unmanaged<IntPtr, int*, IntPtr*, int, int*, uint>)NativeLibrary.GetExport(library, "eglChooseConfig");
            GetProcAddressFunction = NativeLibrary.GetExport(library, "eglGetProcAddress");
        }

        /// <summary>
        /// Returns the EGL display, config and context that are current on the calling thread.
        /// </summary>
        public static (IntPtr display, IntPtr config, IntPtr context) GetCurrent()
        {
            ensureLoaded();

            IntPtr display = eglGetCurrentDisplay();
            IntPtr context = eglGetCurrentContext();

            if (display == IntPtr.Zero || context == IntPtr.Zero)
                throw new InvalidOperationException("No EGL context is current on this thread (on Linux/X11, SDL must use EGL: SDL_VIDEO_FORCE_EGL=1).");

            int configId;
            if (eglQueryContext(display, context, EGL_CONFIG_ID, &configId) == 0)
                throw new InvalidOperationException("eglQueryContext(EGL_CONFIG_ID) failed.");

            int* attribs = stackalloc int[] { EGL_CONFIG_ID, configId, EGL_NONE };
            IntPtr config;
            int count;

            if (eglChooseConfig(display, attribs, &config, 1, &count) == 0 || count < 1)
                throw new InvalidOperationException($"eglChooseConfig could not find config {configId}.");

            return (display, config, context);
        }
    }
}
