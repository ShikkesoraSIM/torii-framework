// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;
using NUnit.Framework;
using osu.Framework.Platform.OpenXR;

namespace osu.Framework.Tests.Platform
{
    [TestFixture]
    public class PanelGeometryTest
    {
        [Test]
        public void TestSurfacePointRoundTrips()
        {
            var panel = new PanelGeometry();
            var uv = new Vector2(0.2f, 0.65f);

            Assert.That(panel.IntersectLocal(Vector3.Zero, panel.SurfacePointLocal(uv), out _, out var hit), Is.True);
            assertClose(uv, hit);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TestRayFromBehindHitsWhereItsLineCrosses(bool cylinder)
        {
            var panel = new PanelGeometry { Cylinder = cylinder };
            var uv = new Vector2(0.3f, 0.6f);

            Vector3 surface = panel.SurfacePointLocal(uv);
            var direction = Vector3.Normalize(new Vector3(0.2f, -0.1f, -1));

            Vector3 inFront = surface - direction * 0.3f;
            Vector3 behind = surface + direction * 0.1f;

            Assert.That(panel.IsBehind(inFront), Is.False);
            Assert.That(panel.IsBehind(behind), Is.True);

            Assert.That(panel.IntersectLocal(inFront, direction, out _, out var hit), Is.True);
            assertClose(uv, hit);

            // Pushed through the panel, pointing the same way or turned around: still the same point.
            Assert.That(panel.IntersectLocal(behind, direction, out _, out hit), Is.True);
            assertClose(uv, hit);
            Assert.That(panel.IntersectLocal(behind, -direction, out _, out hit), Is.True);
            assertClose(uv, hit);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TestDepthBehindIsDistanceFromSurface(bool cylinder)
        {
            var panel = new PanelGeometry { Cylinder = cylinder, Centre = new Vector3(1, 2, 3), Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f) };

            Vector3 surface = panel.SurfacePointLocal(new Vector2(0.5f, 0.3f));
            Vector3 outward = cylinder ? Vector3.Normalize(surface with { Y = 0 }) : -Vector3.UnitZ;

            Assert.That(panel.DepthBehind(panel.FromPanelSpace(surface)), Is.EqualTo(0).Within(1e-4f));
            Assert.That(panel.DepthBehind(panel.FromPanelSpace(surface + outward * 0.2f)), Is.EqualTo(0.2f).Within(1e-4f));
            Assert.That(panel.DepthBehind(panel.FromPanelSpace(surface - outward * 0.3f)), Is.EqualTo(-0.3f).Within(1e-4f));
        }

        private static void assertClose(Vector2 expected, Vector2 actual)
        {
            Assert.That(MathF.Abs(expected.X - actual.X), Is.LessThan(1e-5f), $"{actual} != {expected}");
            Assert.That(MathF.Abs(expected.Y - actual.Y), Is.LessThan(1e-5f), $"{actual} != {expected}");
        }
    }
}
