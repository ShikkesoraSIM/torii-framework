// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform.OpenXR;

namespace osu.Framework.Tests.Platform
{
    [TestFixture]
    public class OpenXRInputHandlerTest
    {
        private const int left = OpenXRInput.LEFT;
        private const int right = OpenXRInput.RIGHT;

        private OpenXRInputHandler handler = null!;
        private PanelGeometry panel = null!;
        private XrHandState[] hands = null!;

        private readonly Size panelSize = new Size(1920, 1080);

        [SetUp]
        public void SetUp()
        {
            handler = new OpenXRInputHandler();
            handler.Enabled.Value = true;
            panel = new PanelGeometry { Cylinder = false };
            hands = new XrHandState[2];

            for (int i = 0; i < 2; i++)
            {
                hands[i].AimValid = true;
                hands[i].AimDirection = -Vector3.UnitZ;
            }

            // Left hand out of the way, pointing away from the panel.
            hands[left].AimDirection = Vector3.UnitZ;

            setDepth(-0.5f);
        }

        [Test]
        public void TestSlightlyBehindStillPoints()
        {
            setDepth(handler.MaxDepthBehindPanel - 0.01f);
            hands[right].Trigger = 1;
            step();

            Assert.That(handler.IsOutOfReach(right), Is.False);
            Assert.That(handler.IsTriggerDown(right), Is.True);
        }

        [Test]
        public void TestTooFarBehindIsIgnored()
        {
            step();
            collect();

            setDepth(handler.MaxDepthBehindPanel + 0.01f);
            hands[right].AimPosition += new Vector3(0.1f, 0, 0);
            hands[right].Trigger = 1;
            step();

            Assert.That(handler.IsOutOfReach(right), Is.True);
            Assert.That(handler.IsTriggerDown(right), Is.False);
            var inputs = collect();
            Assert.That(inputs.OfType<MousePositionAbsoluteInput>(), Is.Empty, "the pointer doesn't move");
            Assert.That(inputs.OfType<MouseButtonInput>(), Is.Empty);
        }

        [Test]
        public void TestHeldTriggerReleasesWhenPushedThrough()
        {
            hands[right].Trigger = 1;
            step();
            Assert.That(handler.IsTriggerDown(right), Is.True);

            setDepth(handler.MaxDepthBehindPanel + 0.01f);
            step();
            Assert.That(handler.IsTriggerDown(right), Is.False);
        }

        [Test]
        public void TestHysteresisAndTriggerMustBeLetGoAfterComingBack()
        {
            hands[right].Trigger = 1;
            setDepth(handler.MaxDepthBehindPanel + 0.01f);
            step();
            Assert.That(handler.IsOutOfReach(right), Is.True);

            // Just inside the limit isn't enough to come back.
            setDepth(handler.MaxDepthBehindPanel - 0.01f);
            step();
            Assert.That(handler.IsOutOfReach(right), Is.True);

            // Back in front, trigger still held: no press.
            setDepth(-0.2f);
            step();
            Assert.That(handler.IsOutOfReach(right), Is.False);
            Assert.That(handler.IsTriggerDown(right), Is.False);

            hands[right].Trigger = 0;
            step();
            hands[right].Trigger = 1;
            step();
            Assert.That(handler.IsTriggerDown(right), Is.True);
        }

        [Test]
        public void TestOtherHandTakesOverPointer()
        {
            hands[left].AimDirection = -Vector3.UnitZ;
            step();
            Assert.That(handler.ActiveHand, Is.EqualTo(right));

            setDepth(handler.MaxDepthBehindPanel + 0.01f);
            step();
            Assert.That(handler.ActiveHand, Is.EqualTo(left));
        }

        /// <summary>
        /// Puts both hands' aim origins <paramref name="depth"/> behind the (flat) panel; the right one points at its centre.
        /// </summary>
        private void setDepth(float depth)
        {
            hands[right].AimPosition = new Vector3(0, 0, -panel.Radius - depth);
            hands[left].AimPosition = new Vector3(-0.2f, 0, -panel.Radius - depth + 0.3f);
        }

        private void step() => handler.Process(hands, panel, panelSize);

        private List<IInput> collect()
        {
            var inputs = new List<IInput>();
            handler.CollectPendingInputs(inputs);
            return inputs;
        }
    }
}
