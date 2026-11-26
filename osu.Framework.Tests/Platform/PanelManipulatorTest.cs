// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Numerics;
using NUnit.Framework;
using osu.Framework.Platform.OpenXR;

namespace osu.Framework.Tests.Platform
{
    [TestFixture]
    public class PanelManipulatorTest
    {
        private const int right = 1;

        private FakeHost host = null!;
        private PanelManipulator manipulator = null!;
        private XrHandState[] hands = null!;
        private long time;

        [SetUp]
        public void SetUp()
        {
            host = new FakeHost();
            manipulator = new PanelManipulator();
            hands = new XrHandState[2];

            for (int i = 0; i < 2; i++)
            {
                hands[i].AimValid = true;
                hands[i].AimOrientation = Quaternion.Identity;
                hands[i].AimDirection = -Vector3.UnitZ;
            }

            // The right hand a little below and to the right of the head, pointing at the panel's centre.
            setHand(new Vector3(0.2f, -0.3f, -0.3f), aimAt(new Vector3(0.2f, -0.3f, -0.3f), surfaceCentre()));
            time = Stopwatch.Frequency;
        }

        [Test]
        public void TestHandRotationSwingsPanelAroundController()
        {
            grab(out Vector2 uv, out float distance);

            // Turn the wrist 30 degrees about the vertical (the hand doesn't move).
            var forwardBefore = host.Panel.Forward;
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30 * MathF.PI / 180);
            setHand(hands[right].AimPosition, Quaternion.Normalize(turn * hands[right].AimOrientation));
            step();

            assertHeld(uv, distance);
            Assert.That(angleBetween(host.Panel.Forward, forwardBefore), Is.EqualTo(30).Within(0.01f), "the panel turns with the hand");
        }

        [Test]
        public void TestHandMovementMovesPanelByTheSameAmount()
        {
            var centreBefore = surfaceCentre();
            var orientationBefore = host.Panel.Orientation;
            grab(out Vector2 uv, out float distance);

            setHand(hands[right].AimPosition + new Vector3(0.1f, 0.05f, -0.2f), hands[right].AimOrientation);
            step();

            assertHeld(uv, distance);
            Assert.That(Vector3.Distance(surfaceCentre(), centreBefore + new Vector3(0.1f, 0.05f, -0.2f)), Is.LessThan(1e-4f));
            Assert.That(MathF.Abs(Quaternion.Dot(host.Panel.Orientation, orientationBefore)), Is.EqualTo(1).Within(1e-6f));
        }

        [Test]
        public void TestGrabbedPointStaysOnRayThroughMovementAndRotation()
        {
            // Grab off-centre.
            var target = host.Panel.FromPanelSpace(host.Panel.SurfacePointLocal(new Vector2(0.3f, 0.7f)));
            setHand(hands[right].AimPosition, aimAt(hands[right].AimPosition, target));

            grab(out Vector2 uv, out float distance);
            Assert.That(Vector2.Distance(uv, new Vector2(0.3f, 0.7f)), Is.LessThan(1e-3f));

            var position = hands[right].AimPosition;
            var orientation = hands[right].AimOrientation;

            for (int i = 1; i <= 20; i++)
            {
                var turn = Quaternion.CreateFromYawPitchRoll(0.04f * i, -0.02f * i, 0.03f * i);
                setHand(position + new Vector3(0.01f, 0.02f, -0.005f) * i, Quaternion.Normalize(turn * orientation));
                step();

                assertHeld(uv, distance);
            }
        }

        [Test]
        public void TestSmoothedRayHoldsGrabbedPoint()
        {
            grab(out Vector2 uv, out float distance);

            // The smoothed ray lags behind the controller's actual orientation.
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 20 * MathF.PI / 180);
            var lagging = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 15 * MathF.PI / 180);
            var orientation = hands[right].AimOrientation;

            hands[right].AimOrientation = Quaternion.Normalize(turn * orientation);
            hands[right].AimDirection = Vector3.Transform(-Vector3.UnitZ, Quaternion.Normalize(lagging * orientation));
            step();

            assertHeld(uv, distance);
        }

        [Test]
        public void TestThumbstickPushesAwayAlongRay()
        {
            grab(out Vector2 uv, out float distance);

            hands[right].Thumbstick = new Vector2(0, 1);
            step(0.5);

            Assert.That(host.Panel.Radius, Is.EqualTo(1.5f + 0.6f).Within(0.01f));
            assertHeld(uv, distance * host.Panel.Radius / 1.5f);
        }

        [Test]
        public void TestReleaseReportsLayoutChange()
        {
            int changes = 0;
            manipulator.LayoutChanged += () => changes++;

            grab();
            setHand(hands[right].AimPosition + new Vector3(0, 0.05f, 0), hands[right].AimOrientation);
            step();
            release();

            Assert.That(changes, Is.EqualTo(1));
            Assert.That(manipulator.GrabbingHand, Is.EqualTo(-1));
        }

        [Test]
        public void TestCornerGrabResizesAroundCentre()
        {
            // Aim at the top right corner.
            var corner = host.Panel.FromPanelSpace(host.Panel.SurfacePointLocal(new Vector2(0.97f, 0.03f)));
            setHand(hands[right].AimPosition, aimAt(hands[right].AimPosition, corner));
            var centreBefore = surfaceCentre();

            grab();

            // Drag the corner out to where 1.2x the panel's size would put it.
            var further = host.Panel.FromPanelSpace(host.Panel.SurfacePointLocal(new Vector2(0.5f + 0.47f * 1.2f, 0.5f - 0.47f * 1.2f)));
            setHand(hands[right].AimPosition, aimAt(hands[right].AimPosition, further));
            step();

            Assert.That(host.Panel.HorizontalAngle, Is.EqualTo(60 * MathF.PI / 180 * 1.2f).Within(0.01f));
            Assert.That(Vector3.Distance(surfaceCentre(), centreBefore), Is.LessThan(1e-4f));
        }

        private Vector3 surfaceCentre() => host.Panel.FromPanelSpace(new Vector3(0, 0, -host.Panel.Radius));

        private void grab() => grab(out _, out _);

        /// <param name="uv">The point of the panel the hand's ray hits when grabbing.</param>
        /// <param name="distance">How far along the ray that is.</param>
        private void grab(out Vector2 uv, out float distance)
        {
            Assert.That(host.Panel.Intersect(hands[right].AimPosition, hands[right].AimDirection, out Vector3 hit, out uv));
            distance = Vector3.Distance(hit, hands[right].AimPosition);

            hands[right].Squeeze = 1;
            step();
            Assert.That(manipulator.GrabbingHand, Is.EqualTo(right));
        }

        /// <summary>
        /// Asserts that the hand's ray hits the panel at <paramref name="uv"/>, <paramref name="distance"/> away.
        /// </summary>
        private void assertHeld(Vector2 uv, float distance)
        {
            Assert.That(host.Panel.Intersect(hands[right].AimPosition, hands[right].AimDirection, out Vector3 hit, out Vector2 nowUv));
            Assert.That(Vector2.Distance(nowUv, uv), Is.LessThan(1e-3f), "the ray stays on the grabbed point");
            Assert.That(Vector3.Distance(hit, hands[right].AimPosition), Is.EqualTo(distance).Within(1e-3f), "at the same distance");
        }

        private void release()
        {
            hands[right].Squeeze = 0;
            step();
        }

        private void step(double seconds = 1 / 90.0)
        {
            time += (long)(seconds * Stopwatch.Frequency);
            manipulator.Update(hands, host, time);
        }

        private void setHand(Vector3 position, Quaternion orientation)
        {
            hands[right].AimPosition = position;
            hands[right].AimOrientation = orientation;
            hands[right].AimDirection = Vector3.Transform(-Vector3.UnitZ, orientation);
        }

        /// <summary>
        /// An orientation at <paramref name="from"/> whose -Z points at <paramref name="target"/>, with +Y as up as possible.
        /// </summary>
        private static Quaternion aimAt(Vector3 from, Vector3 target)
        {
            Vector3 z = Vector3.Normalize(from - target);
            Vector3 x = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, z));
            Vector3 y = Vector3.Cross(z, x);

            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                x.X, x.Y, x.Z, 0,
                y.X, y.Y, y.Z, 0,
                z.X, z.Y, z.Z, 0,
                0, 0, 0, 1)));
        }

        private static float angleBetween(Vector3 a, Vector3 b)
            => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;

        private class FakeHost : IPanelHost
        {
            public PanelGeometry Panel { get; } = new PanelGeometry();

            public void SetPanelPose(Vector3 centre, Quaternion orientation)
            {
                Panel.Centre = centre;
                Panel.Orientation = orientation;
            }

            public void SetPanelShape(float radius, float horizontalAngle)
            {
                Panel.Radius = radius;
                Panel.HorizontalAngle = horizontalAngle;
            }
        }
    }
}
