// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Audio
{
    /// <summary>
    /// Backend-agnostic description of an audio output device.
    /// </summary>
    public struct AudioDeviceInfo
    {
        public string? Name { get; set; }
        public string? Driver { get; set; }
        public bool IsDefault { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsInitialized { get; set; }
    }
}
