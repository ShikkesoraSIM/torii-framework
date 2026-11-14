// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Desktop OpenGL on a WGL context (SDL on Windows), via XR_KHR_opengl_enable. Supported by SteamVR, the Meta Quest Link runtime
    /// and Virtual Desktop.
    /// </summary>
    public unsafe class WglDesktopOpenXRPlatform : IOpenXRPlatform
    {
        public string ApplicationName { get; }

        public WglDesktopOpenXRPlatform(string applicationName)
        {
            ApplicationName = applicationName;
        }

        public IEnumerable<string> RequiredExtensions => new[] { "XR_KHR_opengl_enable" };

        public void InitialiseLoader(XrFunctions xr)
        {
        }

        public void* AllocateInstanceCreateNext() => null;

        public void CheckGraphicsRequirements(XrFunctions xr, ulong instance, ulong systemId)
        {
            var requirements = new XrGraphicsRequirementsOpenGLKHR { type = XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_KHR };
            xr.xrGetOpenGLGraphicsRequirementsKHR(instance, systemId, &requirements).ThrowIfFailed(nameof(xr.xrGetOpenGLGraphicsRequirementsKHR));
            Logger.Log($"OpenXR: GL requirements {XrUtil.FormatVersion(requirements.minApiVersionSupported)} - {XrUtil.FormatVersion(requirements.maxApiVersionSupported)}");
        }

        public void* AllocateGraphicsBinding()
        {
            IntPtr deviceContext = wglGetCurrentDC();
            IntPtr context = wglGetCurrentContext();

            if (deviceContext == IntPtr.Zero || context == IntPtr.Zero)
                throw new InvalidOperationException("No WGL context is current on this thread.");

            var binding = (XrGraphicsBindingOpenGLWin32KHR*)NativeMemory.AllocZeroed((nuint)sizeof(XrGraphicsBindingOpenGLWin32KHR));
            *binding = new XrGraphicsBindingOpenGLWin32KHR
            {
                type = XR_TYPE_GRAPHICS_BINDING_OPENGL_WIN32_KHR,
                hDC = deviceContext,
                hGLRC = context,
            };

            return binding;
        }

        public void Free(void* structure)
        {
            if (structure != null)
                NativeMemory.Free(structure);
        }

        public XrStructureType SwapchainImageType => XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_KHR;

        // Desktop GL leaves GL_FRAMEBUFFER_SRGB disabled unless enabled, so values are written to sRGB images untouched.
        public bool CanDisableSrgbWrites => true;

        // Desktop GPUs render both at the full recommended resolution with ease.
        public float EnvironmentResolutionScale => 1;

        public float ControllerResolutionScale => 1;

        [DllImport("opengl32.dll")]
        private static extern IntPtr wglGetCurrentDC();

        [DllImport("opengl32.dll")]
        private static extern IntPtr wglGetCurrentContext();
    }
}
