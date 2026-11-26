// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Audio.Native;
using osu.Framework.Logging;

namespace osu.Framework.Audio
{
    internal static class BassUtils
    {
        internal static bool CheckFaulted(bool throwException)
        {
            if (ToriiAudioEngine.LastError == TaError.Ok)
                return false;

            string failMessage = $@"Audio engine faulted with error code {ToriiAudioEngine.LastError:D}: {ToriiAudioEngine.LastError} ({ToriiAudioEngine.LastErrorMessage}).";

            if (throwException)
                throw new InvalidOperationException(failMessage);

            Logger.Log(failMessage, LoggingTarget.Runtime, LogLevel.Important);
            return true;
        }

        internal static double DbToLevel(double gain)
        {
            if (gain == 0) return 0;

            return Math.Pow(10, gain / 20);
        }

        internal static double LevelToDb(double level)
        {
            if (level == 0) return 0;

            return Math.Log(level, 10) * 20f;
        }
    }
}
