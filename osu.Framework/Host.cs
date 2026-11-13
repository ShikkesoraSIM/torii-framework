// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Diagnostics;
using osu.Framework.Platform;
using osu.Framework.Platform.Linux;
using osu.Framework.Platform.MacOS;
using osu.Framework.Platform.Windows;

namespace osu.Framework
{
    public static class Host
    {
        public static DesktopGameHost GetSuitableDesktopHost(string gameName, HostOptions hostOptions = null)
        {
            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Windows:
                    Debug.Assert(OperatingSystem.IsWindows());
                    return new WindowsGameHost(gameName, hostOptions);

                case RuntimeInfo.Platform.Linux:
                    return new LinuxGameHost(gameName, hostOptions);

                case RuntimeInfo.Platform.macOS:
                    return new MacOSGameHost(gameName, hostOptions);

                default:
                    throw new InvalidOperationException($"Could not find a suitable host for the selected operating system ({RuntimeInfo.OS}).");
            }
        }

        /// <summary>
        /// Torii: a desktop host that presents through an OpenXR runtime (e.g. Monado) instead of its window. Linux only.
        /// </summary>
        /// <param name="gameName">The game name.</param>
        /// <param name="hostOptions">The host options.</param>
        /// <param name="debugDirectory">A directory holding OpenXR debug flag files, or null.</param>
        public static DesktopGameHost GetOpenXRDesktopHost(string gameName, HostOptions hostOptions = null, string debugDirectory = null)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Linux)
                throw new PlatformNotSupportedException("OpenXR desktop hosts are only supported on Linux.");

            return new OpenXRLinuxGameHost(gameName, hostOptions, debugDirectory: debugDirectory);
        }
    }
}
