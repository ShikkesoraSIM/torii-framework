// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;
using NUnit.Framework;
using osu.Framework.Platform.OpenXR;

namespace osu.Framework.Tests.Platform
{
    [TestFixture]
    public class ControllerIdleTrackerTest
    {
        private const int left = OpenXRInput.LEFT;
        private const int right = OpenXRInput.RIGHT;

        private ControllerIdleTracker tracker = null!;
        private XrHandState[] hands = null!;
        private double time;

        [SetUp]
        public void SetUp()
        {
            tracker = new ControllerIdleTracker { HideDelay = 5 };
            hands = new XrHandState[2];
            time = 0;

            for (int i = 0; i < 2; i++)
            {
                hands[i].GripValid = true;
                hands[i].GripOrientation = Quaternion.Identity;
                hands[i].GripPosition = new Vector3(i == left ? -0.3f : 0.3f, 1, -0.3f);
            }
        }

        [Test]
        public void TestResting()
        {
            wait(4.9);
            Assert.That(tracker.IsIdle(left), Is.False);

            wait(0.2);
            Assert.That(tracker.IsIdle(left), Is.True);
            Assert.That(tracker.IsIdle(right), Is.False, "the controller driving the pointer is never hidden");
        }

        [Test]
        public void TestJitterDoesNotWake()
        {
            wait(6);
            hands[left].GripPosition += new Vector3(0.005f, 0, 0);
            hands[left].GripOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.02f);
            wait(0.1);
            Assert.That(tracker.IsIdle(left), Is.True);
        }

        [Test]
        public void TestMovingWakes()
        {
            wait(6);
            hands[left].GripPosition += new Vector3(0.05f, 0, 0);
            wait(0.1);
            Assert.That(tracker.IsIdle(left), Is.False);
        }

        [Test]
        public void TestTurningWakes()
        {
            wait(6);
            hands[left].GripOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30 * MathF.PI / 180);
            wait(0.1);
            Assert.That(tracker.IsIdle(left), Is.False);
        }

        [Test]
        public void TestButtonWakes()
        {
            wait(6);
            hands[left].PrimaryButton = true;
            wait(0.1);
            Assert.That(tracker.IsIdle(left), Is.False);
        }

        [Test]
        public void TestNoLongerActiveWaitsBeforeHiding()
        {
            wait(6);
            Assert.That(tracker.IsIdle(right), Is.False);

            // The left one takes over the pointer: the right one stays shown for a while.
            wait(0.1, activeHand: left);
            Assert.That(tracker.IsIdle(right), Is.False);
            wait(5.1, activeHand: left);
            Assert.That(tracker.IsIdle(right), Is.True);
        }

        [Test]
        public void TestDisabled()
        {
            tracker.HideDelay = 0;
            wait(60);
            Assert.That(tracker.IsIdle(left), Is.False);
        }

        private void wait(double seconds, int activeHand = right)
        {
            double end = time + seconds;

            for (; time < end; time += 0.1)
                tracker.Update(hands, activeHand, time);

            time = end;
            tracker.Update(hands, activeHand, time);
        }
    }
}
