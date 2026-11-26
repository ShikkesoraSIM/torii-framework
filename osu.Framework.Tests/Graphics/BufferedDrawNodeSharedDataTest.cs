// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering.Dummy;

namespace osu.Framework.Tests.Graphics
{
    public class BufferedDrawNodeSharedDataTest
    {
        [Test]
        public void TestReleasedFrameBuffersAreRecreatedAndRedrawn()
        {
            var renderer = new DummyRenderer();
            var sharedData = new BufferedDrawNodeSharedData(1, Framework.Graphics.Rendering.TexturePixelFormat.R8G8B8A8Float);
            sharedData.Initialise(renderer);

            var mainBuffer = sharedData.MainBuffer;
            sharedData.DrawVersion = 5;

            sharedData.ReleaseFrameBuffers();

            Assert.That(sharedData.DrawVersion, Is.EqualTo(-1), "a released buffer must be redrawn");
            Assert.That(sharedData.MainBuffer, Is.Not.SameAs(mainBuffer));
        }

        [Test]
        public void TestReleaseAfterDisposalDoesNothing()
        {
            var renderer = new DummyRenderer();
            var sharedData = new BufferedDrawNodeSharedData(1, Framework.Graphics.Rendering.TexturePixelFormat.R8G8B8A8Float);
            sharedData.Initialise(renderer);
            _ = sharedData.MainBuffer;
            sharedData.DrawVersion = 5;

            sharedData.Dispose();
            sharedData.ReleaseFrameBuffers();

            Assert.That(sharedData.DrawVersion, Is.EqualTo(5));
        }
    }
}
