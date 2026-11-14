// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;

namespace osu.Framework.Platform.OpenXR.Native
{
    /// <summary>
    /// Locates the Khronos OpenXR loader for the current platform.
    /// </summary>
    public static class XrLoaderLibrary
    {
        /// <summary>
        /// Loads the OpenXR loader library.
        /// </summary>
        /// <exception cref="DllNotFoundException">If no loader is available (e.g. the app wasn't packaged with one).</exception>
        public static IntPtr Load()
        {
            foreach (string name in candidateNames())
            {
                if (NativeLibrary.TryLoad(name, out IntPtr handle))
                    return handle;
            }

            throw new DllNotFoundException("The OpenXR loader could not be found. On Android and Windows it must be packaged with the app."
                                           + " On Linux, install the distribution's OpenXR loader package (libopenxr_loader.so.1).");
        }

        private static string[] candidateNames()
        {
            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Android:
                    return new[] { "libopenxr_loader.so" };

                case RuntimeInfo.Platform.Windows:
                    // Windows has no system-wide loader: applications ship their own.
                    return new[]
                    {
                        "openxr_loader.dll",
                        System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "openxr", "openxr_loader.dll"),
                    };

                default:
                    return new[] { "libopenxr_loader.so.1", "libopenxr_loader.so" };
            }
        }
    }
}
