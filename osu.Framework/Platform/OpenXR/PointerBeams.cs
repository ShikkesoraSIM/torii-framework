// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// The images of <see cref="PointerBeams"/>.
    /// </summary>
    internal enum PointerImage
    {
        ActiveLaser,
        InactiveLaser,
        Reticle,
        ActiveGhostLaser,
        InactiveGhostLaser,
    }

    /// <summary>
    /// A flat rectangle showing a <see cref="PointerImage"/>: centred on <see cref="Centre"/>, its width along X and height along Y of
    /// <see cref="Orientation"/>, facing +Z.
    /// </summary>
    internal readonly record struct PointerQuad(PointerImage Image, Vector3 Centre, Quaternion Orientation, Vector2 Size);

    /// <summary>
    /// Laser beams from controllers to the panel, and a reticle where a controller points: where they go and what they look like,
    /// apart from how they're drawn. Shared by <see cref="OpenXRPointerVisuals"/> (for the headset) and
    /// anything drawing the same scene another way (in a window, say).
    /// </summary>
    internal static class PointerBeams
    {
        /// <summary>
        /// The most quads <see cref="Build"/> writes per hand.
        /// </summary>
        public const int MAX_QUADS_PER_HAND = 2;

        public const int LASER_TEXTURE_WIDTH = 256;
        public const int LASER_TEXTURE_HEIGHT = 32;
        public const int RETICLE_TEXTURE_SIZE = 64;

        private const float laser_thickness = 0.004f;
        private const float laser_miss_length = 0.8f;
        private const float reticle_diameter = 0.02f;

        /// <summary>
        /// How far in front of the panel the reticle floats, to avoid z-fighting with the panel.
        /// </summary>
        private const float reticle_offset = 0.003f;

        private static readonly Vector3 laser_colour = new Vector3(0.75f, 0.9f, 1f);
        private static readonly Vector3 ghost_laser_colour = new Vector3(1f, 0.72f, 0.45f);

        /// <summary>
        /// Places the beams of a pair of hands, each turned to face the viewer, and their reticles.
        /// </summary>
        /// <param name="hands">The controllers: those with a valid aim pose get a beam.</param>
        /// <param name="activeHand">The controller driving the pointer.</param>
        /// <param name="viewerPosition">Where the beams are seen from.</param>
        /// <param name="panel">The panel the controllers point at.</param>
        /// <param name="edgeTolerance">See <see cref="OpenXRInputHandler.EdgeTolerance"/>.</param>
        /// <param name="laserStartOffset">How far along the ray the beams start, in metres.</param>
        /// <param name="ghost">Whether the hands are ghost hands (see <see cref="OpenXRGhostHands"/>), with beams of their own colour.</param>
        /// <param name="reticles">Whether the inactive controller gets a reticle where it points. (The active one's target is shown by
        /// the game's own cursor.)</param>
        /// <param name="output">Receives the quads. Must have room for <see cref="MAX_QUADS_PER_HAND"/> per hand.</param>
        /// <returns>The number of quads written.</returns>
        public static int Build(ReadOnlySpan<XrHandState> hands, int activeHand, Vector3 viewerPosition, PanelGeometry panel, float edgeTolerance, float laserStartOffset,
                                bool ghost, bool reticles, Span<PointerQuad> output)
        {
            int count = 0;

            for (int hand = 0; hand < hands.Length; hand++)
            {
                ref readonly var state = ref hands[hand];

                if (!state.AimValid)
                    continue;

                Vector3 direction = state.AimDirection;
                Vector3 start = state.AimPosition + direction * laserStartOffset;

                bool onPanel = panel.Intersect(state.AimPosition, direction, out Vector3 hit, out Vector2 uv) && PanelGeometry.IsWithin(uv, edgeTolerance);
                Vector3 end = onPanel ? hit : start + direction * laser_miss_length;

                float length = Vector3.Distance(start, end);

                // A controller pushed through the panel still points (see PanelGeometry.Intersect), but has no beam to show.
                if (length >= 0.01f && !panel.IsBehind(state.AimPosition))
                {
                    Vector3 middle = (start + end) / 2;
                    bool active = hand == activeHand;

                    var image = ghost
                        ? active ? PointerImage.ActiveGhostLaser : PointerImage.InactiveGhostLaser
                        : active ? PointerImage.ActiveLaser : PointerImage.InactiveLaser;

                    output[count++] = new PointerQuad(image, middle, faceViewerAlong(direction, viewerPosition - middle), new Vector2(length, laser_thickness));
                }

                if (reticles && onPanel && hand != activeHand)
                {
                    Quaternion facing = panel.SurfaceOrientation(hit);
                    output[count++] = new PointerQuad(PointerImage.Reticle, hit + Vector3.Transform(new Vector3(0, 0, reticle_offset), facing), facing, new Vector2(reticle_diameter));
                }
            }

            return count;
        }

        /// <summary>
        /// The picture of <paramref name="image"/>: RGBA8 with premultiplied alpha, top row first.
        /// </summary>
        public static byte[] CreatePixels(PointerImage image, out int width, out int height)
        {
            if (image == PointerImage.Reticle)
            {
                width = height = RETICLE_TEXTURE_SIZE;
                return reticlePixels();
            }

            width = LASER_TEXTURE_WIDTH;
            height = LASER_TEXTURE_HEIGHT;

            return image switch
            {
                PointerImage.ActiveLaser => laserPixels(laser_colour, 0.9f),
                PointerImage.InactiveLaser => laserPixels(laser_colour, 0.35f),
                PointerImage.ActiveGhostLaser => laserPixels(ghost_laser_colour, 0.8f),
                _ => laserPixels(ghost_laser_colour, 0.3f),
            };
        }

        /// <summary>
        /// An orientation whose X axis runs along <paramref name="axis"/> and whose face (+Z) turns towards the viewer as far as possible
        /// (a cylindrical billboard), so a flat beam never appears edge-on.
        /// </summary>
        private static Quaternion faceViewerAlong(Vector3 axis, Vector3 toViewer)
        {
            Vector3 x = Vector3.Normalize(axis);
            Vector3 z = toViewer - x * Vector3.Dot(toViewer, x);

            if (z.LengthSquared() < 1e-8f)
                z = Vector3.Cross(x, MathF.Abs(x.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);

            z = Vector3.Normalize(z);
            Vector3 y = Vector3.Cross(z, x);

            // System.Numerics uses row vectors: each row is the image of a basis axis.
            var rotation = new Matrix4x4(
                x.X, x.Y, x.Z, 0,
                y.X, y.Y, y.Z, 0,
                z.X, z.Y, z.Z, 0,
                0, 0, 0, 1);

            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
        }

        /// <summary>
        /// A soft beam: bright core across its width, fading in from the controller and fading out at the far end.
        /// </summary>
        private static byte[] laserPixels(Vector3 colour, float opacity)
        {
            byte[] pixels = new byte[LASER_TEXTURE_WIDTH * LASER_TEXTURE_HEIGHT * 4];

            for (int y = 0; y < LASER_TEXTURE_HEIGHT; y++)
            {
                float across = (y + 0.5f) / LASER_TEXTURE_HEIGHT * 2 - 1;
                float core = MathF.Exp(-across * across * 6);

                for (int x = 0; x < LASER_TEXTURE_WIDTH; x++)
                {
                    float along = (x + 0.5f) / LASER_TEXTURE_WIDTH;
                    float fade = MathF.Min(smoothStep(0, 0.15f, along), 1 - smoothStep(0.85f, 1, along) * 0.6f);

                    writePremultiplied(pixels, (y * LASER_TEXTURE_WIDTH + x) * 4, colour, core * fade * opacity);
                }
            }

            return pixels;
        }

        /// <summary>
        /// A ring with a soft edge.
        /// </summary>
        private static byte[] reticlePixels()
        {
            byte[] pixels = new byte[RETICLE_TEXTURE_SIZE * RETICLE_TEXTURE_SIZE * 4];

            for (int y = 0; y < RETICLE_TEXTURE_SIZE; y++)
            {
                for (int x = 0; x < RETICLE_TEXTURE_SIZE; x++)
                {
                    float dx = (x + 0.5f) / RETICLE_TEXTURE_SIZE * 2 - 1;
                    float dy = (y + 0.5f) / RETICLE_TEXTURE_SIZE * 2 - 1;
                    float r = MathF.Sqrt(dx * dx + dy * dy);

                    float ring = MathF.Exp(-MathF.Pow((r - 0.7f) / 0.12f, 2));
                    float dot = 1 - smoothStep(0.12f, 0.2f, r);

                    writePremultiplied(pixels, (y * RETICLE_TEXTURE_SIZE + x) * 4, laser_colour, MathF.Max(ring, dot) * 0.6f);
                }
            }

            return pixels;
        }

        private static float smoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
            return t * t * (3 - 2 * t);
        }

        private static void writePremultiplied(byte[] pixels, int offset, Vector3 colour, float alpha)
        {
            alpha = Math.Clamp(alpha, 0, 1);
            pixels[offset] = (byte)(colour.X * alpha * 255);
            pixels[offset + 1] = (byte)(colour.Y * alpha * 255);
            pixels[offset + 2] = (byte)(colour.Z * alpha * 255);
            pixels[offset + 3] = (byte)(alpha * 255);
        }
    }
}
