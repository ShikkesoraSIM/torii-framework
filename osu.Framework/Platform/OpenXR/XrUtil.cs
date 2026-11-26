// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using osu.Framework.Platform.OpenXR.Native;

namespace osu.Framework.Platform.OpenXR
{
    internal static unsafe class XrUtil
    {
        public static string ReadString(byte* buffer) => Marshal.PtrToStringUTF8((IntPtr)buffer) ?? string.Empty;

        /// <summary>
        /// Reads a null-terminated UTF-8 string from a fixed-size buffer, never reading past <paramref name="capacity"/> bytes.
        /// </summary>
        public static string ReadString(byte* buffer, int capacity)
        {
            int length = 0;
            while (length < capacity && buffer[length] != 0)
                length++;

            return Encoding.UTF8.GetString(buffer, length);
        }

        public static string PathToString(XrFunctions xr, ulong instance, ulong path)
        {
            uint count;
            xr.xrPathToString(instance, path, 0, &count, null).ThrowIfFailed(nameof(xr.xrPathToString));

            byte* buffer = stackalloc byte[(int)count + 1];
            xr.xrPathToString(instance, path, count, &count, buffer).ThrowIfFailed(nameof(xr.xrPathToString));
            return ReadString(buffer, (int)count);
        }

        /// <summary>
        /// Writes a null-terminated UTF-8 string into a fixed-size buffer, truncating if necessary.
        /// </summary>
        public static void WriteString(byte* buffer, int capacity, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            int length = Math.Min(bytes.Length, capacity - 1);
            bytes.AsSpan(0, length).CopyTo(new Span<byte>(buffer, capacity));
            buffer[length] = 0;
        }

        public static ulong StringToPath(XrFunctions xr, ulong instance, string path)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(path + '\0');
            ulong result;

            fixed (byte* p = utf8)
                xr.xrStringToPath(instance, p, &result).ThrowIfFailed($"{nameof(xr.xrStringToPath)}({path})");

            return result;
        }

        public static string FormatVersion(ulong v) => $"{v >> 48}.{(v >> 32) & 0xFFFF}.{v & 0xFFFFFFFF}";

        public static XrPosef IdentityPose => new XrPosef { orientation = new XrQuaternionf { w = 1 } };

        public static Vector3 ToNumerics(this XrVector3f v) => new Vector3(v.x, v.y, v.z);

        public static XrVector3f ToXr(this Vector3 v) => new XrVector3f { x = v.X, y = v.Y, z = v.Z };

        public static Quaternion ToNumerics(this XrQuaternionf q) => new Quaternion(q.x, q.y, q.z, q.w);
    }
}
