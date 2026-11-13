// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Platform.OpenXR.Native
{
    public static class XrResultExtensions
    {
        /// <summary>
        /// Whether <paramref name="result"/> is a success code. Note that some success codes (e.g. <c>XR_SESSION_LOSS_PENDING</c>,
        /// <c>XR_FRAME_DISCARDED</c>) are positive and still need handling by the caller.
        /// </summary>
        public static bool Succeeded(this XrResult result) => result >= 0;

        /// <summary>
        /// Throws an <see cref="XrException"/> if <paramref name="result"/> is an error code.
        /// </summary>
        /// <returns><paramref name="result"/>, for callers that need to distinguish between success codes.</returns>
        public static XrResult ThrowIfFailed(this XrResult result, string operation)
        {
            if (!result.Succeeded())
                throw new XrException(operation, result);

            return result;
        }
    }

    public class XrException : Exception
    {
        public XrResult Result { get; }

        public XrException(string operation, XrResult result)
            : base($"OpenXR call {operation} failed with {result}.")
        {
            Result = result;
        }
    }
}
