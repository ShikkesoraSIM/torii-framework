// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Platform.OpenXR;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Platform
{
    /// <summary>
    /// A VR scene drawn in a window: content on a panel, a pair of controllers pointing at it, and a headset.
    /// The models for what's held and for the headset are the game's, when run from a checkout next to the game's.
    /// </summary>
    public partial class TestSceneSceneViewContainer : FrameworkTestScene
    {
        private SceneViewContainer? scene;
        private Box marker = null!;

        private bool orbit = true;
        private bool wave = true;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            Child = scene = new SceneViewContainer
            {
                ShowHead = true,
                HeadPosition = new Vector3(0, 0, 0.1f),
                ActiveHand = OpenXRInput.RIGHT,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(40, 20, 60, 160) },
                    new SpriteText { Text = "top left", Font = FrameworkFont.Regular.With(size: 60), Margin = new MarginPadding(20) },
                    new SpriteText
                    {
                        Text = "bottom right", Font = FrameworkFont.Regular.With(size: 60), Margin = new MarginPadding(20),
                        Anchor = Anchor.BottomRight, Origin = Anchor.BottomRight,
                    },
                    marker = new Box { Size = new osuTK.Vector2(80), Origin = Anchor.Centre, Anchor = Anchor.Centre, Colour = Color4.Yellow },
                },
            };

            scene.Panel.Centre = Vector3.Zero;
            scene.Panel.Radius = 1.5f;
            scene.Panel.HorizontalAngle = 70 * MathF.PI / 180;
            orbit = wave = true;
        });

        [Test]
        public void TestScene()
        {
            AddStep("stop the camera", () => orbit = false);
            AddStep("stop the hands", () => wave = false);
            AddStep("camera behind the head", () => place(new Vector3(0.5f, 0.4f, 1.6f), new Vector3(0, -0.1f, -1.5f)));
            AddStep("camera from the side", () => place(new Vector3(-2.2f, 0.3f, -0.3f), new Vector3(0, -0.1f, -0.7f)));
            AddStep("camera behind the panel", () => place(new Vector3(0.6f, 0.2f, -3f), new Vector3(0, 0, -1.5f)));
            AddStep("flat panel", () => scene!.Panel.Cylinder = false);
            AddStep("curved panel", () => scene!.Panel.Cylinder = true);
            AddStep("no headset", () => scene!.ShowHead = false);
            AddStep("one hand", () => scene!.Hands[OpenXRInput.LEFT].GripValid = false);
            AddStep("orbit", () => orbit = wave = true);
            AddSliderStep("field of view", 20, 120, 60, v => Schedule(() =>
            {
                if (scene != null)
                    scene.FieldOfView = v * MathF.PI / 180;
            }));
        }

        [Test]
        public void TestModels()
        {
            if (loadModel("pen") == null)
            {
                AddStep("no models here", () => { });
                return;
            }

            AddStep("hold pens", () => scene!.HeldModel = loadModel("pen"));
            AddStep("wear a headset", () => scene!.HeadModel = loadModel("headset"));
            AddWaitStep("look at them", 20);
            AddStep("hold controllers", () => scene!.HeldModel = null);
            AddStep("no headset", () => scene!.HeadModel = null);
            AddWaitStep("look at them", 20);
            AddStep("hold pens", () => scene!.HeldModel = loadModel("pen"));
            AddStep("wear a headset", () => scene!.HeadModel = loadModel("headset"));
        }

        private static byte[]? loadModel(string name)
        {
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../../torii-osu/osu.Game/VR/Models/{name}.glb"));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private void place(Vector3 position, Vector3 target)
        {
            if (scene == null)
                return;

            var forward = Vector3.Normalize(target - position);

            scene.CameraPosition = position;
            scene.CameraOrientation = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(-forward.X, -forward.Z), MathF.Asin(forward.Y), 0);
        }

        protected override void Update()
        {
            base.Update();

            // Not set up yet.
            if (scene == null)
                return;

            float t = (float)(Time.Current / 1000);

            scene.Panel.Aspect = scene.DrawWidth / Math.Max(scene.DrawHeight, 1);

            if (orbit)
                place(new Vector3(MathF.Sin(t * 0.4f) * 1.5f, 0.25f + MathF.Sin(t * 0.3f) * 0.3f, 0.3f + MathF.Cos(t * 0.4f) * 1.1f), new Vector3(0, -0.15f, -0.7f));

            if (wave)
            {
                pose(OpenXRInput.LEFT, new Vector3(-0.2f, -0.3f, -0.35f), -0.25f + MathF.Sin(t) * 0.25f, 0.2f + MathF.Cos(t * 1.3f) * 0.1f, 0);
                pose(OpenXRInput.RIGHT, new Vector3(0.2f, -0.3f, -0.35f), 0.2f + MathF.Sin(t * 0.7f) * 0.3f, 0.2f + MathF.Cos(t) * 0.15f, MathF.Abs(MathF.Sin(t * 3)));
            }

            // Where the active hand points.
            if (scene.Panel.Intersect(scene.Hands[OpenXRInput.RIGHT].AimPosition, scene.Hands[OpenXRInput.RIGHT].AimDirection, out _, out var uv))
            {
                marker.Anchor = Anchor.TopLeft;
                marker.Position = new osuTK.Vector2(uv.X * scene.DrawWidth, uv.Y * scene.DrawHeight);
            }

            scene.HeadOrientation = Quaternion.CreateFromYawPitchRoll(MathF.Sin(t * 0.5f) * 0.3f, -0.1f, 0);
        }

        private void pose(int hand, Vector3 position, float yaw, float pitch, float trigger)
        {
            var aim = Quaternion.CreateFromYawPitchRoll(-yaw, pitch, 0);

            // Held the usual way: the handle tilted back from where it points.
            var grip = aim * Quaternion.CreateFromYawPitchRoll(0, 0.9f, 0) * Quaternion.CreateFromYawPitchRoll(0, 0, MathF.PI);

            scene!.Hands[hand] = new XrHandState
            {
                GripValid = true,
                GripPosition = position + Vector3.Transform(new Vector3(0, -0.02f, 0.05f), aim),
                GripOrientation = grip,
                AimValid = true,
                AimPosition = position,
                AimOrientation = aim,
                AimDirection = Vector3.Transform(-Vector3.UnitZ, aim),
                Trigger = trigger,
            };
        }
    }
}
