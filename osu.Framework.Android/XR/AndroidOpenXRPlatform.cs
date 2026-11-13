// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Java.Interop;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR;
using osu.Framework.Platform.OpenXR.Native;
using osuTK.Graphics.ES30;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Android.XR
{
    /// <summary>
    /// Android with OpenGL ES (e.g. Meta Quest): loader initialisation with the JavaVM, XR_KHR_android_create_instance and
    /// XR_KHR_opengl_es_enable on the EGL context SDL created.
    /// </summary>
    internal unsafe class AndroidOpenXRPlatform : IOpenXRPlatform
    {
        private readonly AndroidGameActivity activity;

        public AndroidOpenXRPlatform(AndroidGameActivity activity)
        {
            this.activity = activity;
        }

        public string ApplicationName => activity.PackageName ?? "osu!framework app";

        public IEnumerable<string> RequiredExtensions => new[] { "XR_KHR_android_create_instance", "XR_KHR_opengl_es_enable" };

        public void InitialiseLoader(XrFunctions xr)
        {
            if (xr.xrInitializeLoaderKHR == null)
                throw new InvalidOperationException("The OpenXR loader does not expose xrInitializeLoaderKHR.");

            var loaderInfo = new XrLoaderInitInfoAndroidKHR
            {
                type = XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR,
                applicationVM = (void*)JniEnvironment.Runtime.InvocationPointer,
                applicationContext = (void*)activity.ApplicationContext!.Handle,
            };

            xr.xrInitializeLoaderKHR((XrLoaderInitInfoBaseHeaderKHR*)&loaderInfo).ThrowIfFailed(nameof(xr.xrInitializeLoaderKHR));
        }

        public void* AllocateInstanceCreateNext()
        {
            var info = (XrInstanceCreateInfoAndroidKHR*)NativeMemory.AllocZeroed((nuint)sizeof(XrInstanceCreateInfoAndroidKHR));
            *info = new XrInstanceCreateInfoAndroidKHR
            {
                type = XR_TYPE_INSTANCE_CREATE_INFO_ANDROID_KHR,
                applicationVM = (void*)JniEnvironment.Runtime.InvocationPointer,
                applicationActivity = (void*)activity.Handle,
            };
            return info;
        }

        public void CheckGraphicsRequirements(XrFunctions xr, ulong instance, ulong systemId)
        {
            var requirements = new XrGraphicsRequirementsOpenGLESKHR { type = XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR };
            xr.xrGetOpenGLESGraphicsRequirementsKHR(instance, systemId, &requirements).ThrowIfFailed(nameof(xr.xrGetOpenGLESGraphicsRequirementsKHR));
            Logger.Log($"OpenXR: GLES requirements {XrUtil.FormatVersion(requirements.minApiVersionSupported)} - {XrUtil.FormatVersion(requirements.maxApiVersionSupported)}");
        }

        public void* AllocateGraphicsBinding()
        {
            var (display, config, context) = Egl.GetCurrent();

            var binding = (XrGraphicsBindingOpenGLESAndroidKHR*)NativeMemory.AllocZeroed((nuint)sizeof(XrGraphicsBindingOpenGLESAndroidKHR));
            *binding = new XrGraphicsBindingOpenGLESAndroidKHR
            {
                type = XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR,
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

        public XrStructureType SwapchainImageType => XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_ES_KHR;

        public bool CanDisableSrgbWrites
        {
            get
            {
                GL.GetInteger(GetPName.NumExtensions, out int count);

                for (int i = 0; i < count; i++)
                {
                    if (GL.GetString(StringNameIndexed.Extensions, i) == "GL_EXT_sRGB_write_control")
                        return true;
                }

                return false;
            }
        }
    }
}
