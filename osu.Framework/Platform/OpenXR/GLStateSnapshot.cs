// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osuTK.Graphics.ES30;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Snapshot of the GL state the OpenXR renderers change, restored afterwards so the framework's cached GL state stays valid.
    /// </summary>
    internal unsafe struct GLStateSnapshot
    {
        private int program, vertexArray, arrayBuffer, framebuffer, activeTexture;
        private int texture0, texture1, texture2, texture3;
        private int viewportX, viewportY, viewportW, viewportH;
        private bool scissor, depthTest, blend, cullFace, depthMask;
        private int depthFunc, cullFaceMode, frontFace;
        private int blendSrcRgb, blendDstRgb, blendSrcAlpha, blendDstAlpha, blendEquationRgb, blendEquationAlpha;
        private bool maskR, maskG, maskB, maskA;
        private float clearR, clearG, clearB, clearA;
        private float clearDepth;

        public static GLStateSnapshot Capture()
        {
            var s = new GLStateSnapshot();
            GL.GetInteger(GetPName.CurrentProgram, out s.program);
            GL.GetInteger(GetPName.VertexArrayBinding, out s.vertexArray);
            GL.GetInteger(GetPName.ArrayBufferBinding, out s.arrayBuffer);
            GL.GetInteger(GetPName.FramebufferBinding, out s.framebuffer);
            GL.GetInteger(GetPName.ActiveTexture, out s.activeTexture);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.GetInteger(GetPName.TextureBinding2D, out s.texture0);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.GetInteger(GetPName.TextureBinding2D, out s.texture1);
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.GetInteger(GetPName.TextureBinding2D, out s.texture2);
            GL.ActiveTexture(TextureUnit.Texture3);
            GL.GetInteger(GetPName.TextureBinding2D, out s.texture3);
            GL.ActiveTexture((TextureUnit)s.activeTexture);

            int* viewport = stackalloc int[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            (s.viewportX, s.viewportY, s.viewportW, s.viewportH) = (viewport[0], viewport[1], viewport[2], viewport[3]);

            s.scissor = GL.IsEnabled(EnableCap.ScissorTest);
            s.depthTest = GL.IsEnabled(EnableCap.DepthTest);
            s.blend = GL.IsEnabled(EnableCap.Blend);
            s.cullFace = GL.IsEnabled(EnableCap.CullFace);
            GL.GetBoolean(GetPName.DepthWritemask, out s.depthMask);
            GL.GetInteger(GetPName.DepthFunc, out s.depthFunc);
            GL.GetInteger(GetPName.CullFaceMode, out s.cullFaceMode);
            GL.GetInteger(GetPName.FrontFace, out s.frontFace);
            GL.GetInteger(GetPName.BlendSrcRgb, out s.blendSrcRgb);
            GL.GetInteger(GetPName.BlendDstRgb, out s.blendDstRgb);
            GL.GetInteger(GetPName.BlendSrcAlpha, out s.blendSrcAlpha);
            GL.GetInteger(GetPName.BlendDstAlpha, out s.blendDstAlpha);
            GL.GetInteger(GetPName.BlendEquationRgb, out s.blendEquationRgb);
            GL.GetInteger(GetPName.BlendEquationAlpha, out s.blendEquationAlpha);

            bool* colorMask = stackalloc bool[4];
            GL.GetBoolean(GetPName.ColorWritemask, colorMask);
            (s.maskR, s.maskG, s.maskB, s.maskA) = (colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
            float* clearColour = stackalloc float[4];
            GL.GetFloat(GetPName.ColorClearValue, clearColour);
            (s.clearR, s.clearG, s.clearB, s.clearA) = (clearColour[0], clearColour[1], clearColour[2], clearColour[3]);
            GL.GetFloat(GetPName.DepthClearValue, out s.clearDepth);
            return s;
        }

        public readonly void Restore()
        {
            GL.UseProgram(program);
            GL.BindVertexArray(vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, arrayBuffer);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.ActiveTexture(TextureUnit.Texture3);
            GL.BindTexture(TextureTarget.Texture2D, texture3);
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture2D, texture2);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, texture1);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, texture0);
            GL.ActiveTexture((TextureUnit)activeTexture);
            GL.Viewport(viewportX, viewportY, viewportW, viewportH);

            set(EnableCap.ScissorTest, scissor);
            set(EnableCap.DepthTest, depthTest);
            set(EnableCap.Blend, blend);
            set(EnableCap.CullFace, cullFace);
            GL.DepthMask(depthMask);
            GL.DepthFunc((DepthFunction)depthFunc);
            GL.CullFace((CullFaceMode)cullFaceMode);
            GL.FrontFace((FrontFaceDirection)frontFace);
            GL.BlendFuncSeparate((BlendingFactorSrc)blendSrcRgb, (BlendingFactorDest)blendDstRgb, (BlendingFactorSrc)blendSrcAlpha, (BlendingFactorDest)blendDstAlpha);
            GL.BlendEquationSeparate((BlendEquationMode)blendEquationRgb, (BlendEquationMode)blendEquationAlpha);
            GL.ColorMask(maskR, maskG, maskB, maskA);
            GL.ClearColor(clearR, clearG, clearB, clearA);
            GL.ClearDepth(clearDepth);
        }

        private static void set(EnableCap cap, bool enabled)
        {
            if (enabled)
                GL.Enable(cap);
            else
                GL.Disable(cap);
        }
    }
}
