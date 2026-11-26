// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Reflection;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Testing;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Containers
{
    /// <summary>
    /// Torii: a cached <see cref="BufferedContainer{T}"/> with <see cref="BufferedContainer{T}.TrackSubtreeChanges"/> redraws when
    /// something inside it changes, at any depth, but not when it is only moved or faded from outside.
    /// </summary>
    public partial class TestSceneBufferedContainerSubtreeTracking : FrameworkTestScene
    {
        private Container outer = null!;
        private BufferedContainer buffered = null!;
        private Container inner = null!;
        private Box box = null!;

        private long versionBefore;

        private static readonly FieldInfo update_version = typeof(BufferedContainer<Drawable>).GetField("updateVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly FieldInfo shared_data = typeof(BufferedContainer<Drawable>).GetField("sharedData", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private long redrawVersion => (long)update_version.GetValue(buffered)!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create", () => Child = outer = new Container
            {
                Size = new Vector2(200),
                Child = buffered = new BufferedContainer(cachedFrameBuffer: true)
                {
                    RelativeSizeAxes = Axes.Both,
                    TrackSubtreeChanges = true,
                    Child = inner = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = box = new Box
                        {
                            Size = new Vector2(50),
                            Colour = Color4.Red,
                        }
                    }
                }
            });
            AddWaitStep("settle", 2);
        }

        [Test]
        public void TestMovingFromOutsideDoesNotRedraw()
        {
            AddStep("record version", () => versionBefore = redrawVersion);
            AddRepeatStep("move outer", () => outer.X += 10, 5);
            AddRepeatStep("fade outer", () => outer.Alpha -= 0.1f, 3);
            AddAssert("not redrawn", () => redrawVersion, () => Is.EqualTo(versionBefore));
        }

        [Test]
        public void TestMovingBufferedContainerDoesNotRedraw()
        {
            AddStep("record version", () => versionBefore = redrawVersion);
            AddRepeatStep("move buffered container", () => buffered.Y += 10, 5);
            AddAssert("not redrawn", () => redrawVersion, () => Is.EqualTo(versionBefore));
        }

        [Test]
        public void TestNestedColourChangeRedraws()
        {
            AddStep("record version", () => versionBefore = redrawVersion);
            AddStep("change box colour", () => box.Colour = Color4.Blue);
            AddUntilStep("redrawn", () => redrawVersion, () => Is.GreaterThan(versionBefore));
        }

        [Test]
        public void TestNestedMovementRedraws()
        {
            AddStep("record version", () => versionBefore = redrawVersion);
            AddStep("move inner container", () => inner.X = 20);
            AddUntilStep("redrawn", () => redrawVersion, () => Is.GreaterThan(versionBefore));
        }

        [Test]
        public void TestAddingChildRedraws()
        {
            AddStep("record version", () => versionBefore = redrawVersion);
            AddStep("add box", () => inner.Add(new Box { Size = new Vector2(10) }));
            AddUntilStep("redrawn", () => redrawVersion, () => Is.GreaterThan(versionBefore));
        }

        [Test]
        public void TestFrozenDoesNotUpdateOrRedraw()
        {
            int updates = 0;

            AddStep("count box updates", () => box.OnUpdate += _ => updates++);
            AddStep("freeze", () => buffered.Frozen = true);
            AddWaitStep("wait", 2);
            AddStep("record", () =>
            {
                versionBefore = redrawVersion;
                updates = 0;

                // Nothing draws in headless runs: as far as the container can tell, the buffer has to hold the current contents
                // for the children's draw nodes to be left alone.
                ((BufferedDrawNodeSharedData)shared_data.GetValue(buffered)!).DrawVersion = redrawVersion;
            });
            AddStep("change box colour", () => box.Colour = Color4.Blue);
            AddRepeatStep("move outer", () => outer.X += 10, 5);
            AddAssert("children not updated", () => updates, () => Is.Zero);
            AddAssert("not redrawn", () => redrawVersion, () => Is.EqualTo(versionBefore));

            AddStep("unfreeze", () => buffered.Frozen = false);
            AddUntilStep("change picked up", () => redrawVersion, () => Is.GreaterThan(versionBefore));
            AddAssert("children updated", () => updates, () => Is.GreaterThan(0));
        }

        [Test]
        public void TestNoChangesDoesNotRedraw()
        {
            AddStep("record version", () => versionBefore = redrawVersion);
            AddWaitStep("wait", 5);
            AddAssert("not redrawn", () => redrawVersion, () => Is.EqualTo(versionBefore));
        }
    }
}
