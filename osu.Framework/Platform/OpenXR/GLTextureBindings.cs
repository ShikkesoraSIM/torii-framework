// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osuTK.Graphics.ES30;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Keeps the 2D texture bindings across an OpenXR call which may change them. SteamVR leaves its own textures bound after
    /// xrCreateSwapchain and xrEndFrame; the framework caches bindings, so its next texture upload would go to the wrong texture.
    /// </summary>
    internal readonly ref struct GLTextureBindings
    {
        private readonly int activeUnit;
        private readonly int activeBinding;
        private readonly int firstBinding;

        private GLTextureBindings(int activeUnit, int activeBinding, int firstBinding)
        {
            this.activeUnit = activeUnit;
            this.activeBinding = activeBinding;
            this.firstBinding = firstBinding;
        }

        public static GLTextureBindings Capture()
        {
            GL.GetInteger(GetPName.ActiveTexture, out int unit);
            GL.GetInteger(GetPName.TextureBinding2D, out int binding);

            int first = binding;

            if (unit != (int)TextureUnit.Texture0)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.GetInteger(GetPName.TextureBinding2D, out first);
                GL.ActiveTexture((TextureUnit)unit);
            }

            return new GLTextureBindings(unit, binding, first);
        }

        public void Restore()
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, firstBinding);
            GL.ActiveTexture((TextureUnit)activeUnit);
            GL.BindTexture(TextureTarget.Texture2D, activeBinding);
        }
    }
}
