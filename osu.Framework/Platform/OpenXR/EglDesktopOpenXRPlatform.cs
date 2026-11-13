// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Runtime.InteropServices;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Desktop OpenGL on an EGL context (SDL on Linux), via XR_KHR_opengl_enable + XR_MNDX_egl_enable. Supported by Monado, which also
    /// provides a simulated headset and controllers for developing without hardware.
    /// </summary>
    public unsafe class EglDesktopOpenXRPlatform : IOpenXRPlatform
    {
        public string ApplicationName { get; }

        public EglDesktopOpenXRPlatform(string applicationName)
        {
            ApplicationName = applicationName;
        }

        public IEnumerable<string> RequiredExtensions => new[] { "XR_KHR_opengl_enable", "XR_MNDX_egl_enable" };

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
            var (display, config, context) = Egl.GetCurrent();

            var binding = (XrGraphicsBindingEGLMNDX*)NativeMemory.AllocZeroed((nuint)sizeof(XrGraphicsBindingEGLMNDX));
            *binding = new XrGraphicsBindingEGLMNDX
            {
                type = XR_TYPE_GRAPHICS_BINDING_EGL_MNDX,
                getProcAddress = Egl.GetProcAddressFunction,
                display = display,
                config = config,
                context = context,
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
    }
}
