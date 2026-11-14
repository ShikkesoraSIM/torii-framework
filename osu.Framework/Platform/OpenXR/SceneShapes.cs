// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// A run of indices of <see cref="SceneShapes.Indices"/> making up one mesh.
    /// </summary>
    internal readonly record struct MeshRange(int FirstIndex, int IndexCount);

    /// <summary>
    /// One part of a shape built by <see cref="SceneShapes"/>: a primitive mesh, where it goes, and its colours (gamma-encoded RGB).
    /// </summary>
    internal readonly record struct ShapePart(MeshRange Mesh, Matrix4x4 Model, Vector3 Base, Vector3 Emissive);

    /// <summary>
    /// What the things of a VR scene look like, apart from how they're drawn: the built-in stylised controller, made of a few
    /// primitive meshes, and how surfaces are lit. Shared by <see cref="OpenXRSceneRenderer"/> (for the headset) and
    /// anything drawing the same scene another way (in a window, say).
    /// </summary>
    internal static class SceneShapes
    {
        /// <summary>
        /// The most parts <see cref="BuildController"/> writes.
        /// </summary>
        public const int MAX_PARTS = 8;

        /// <summary>
        /// Floats per vertex of <see cref="Vertices"/>: position (3), normal (3).
        /// </summary>
        public const int FLOATS_PER_VERTEX = 6;

        /// <summary>
        /// The vertices of all primitive meshes.
        /// </summary>
        public static readonly float[] Vertices;

        /// <summary>
        /// The triangles of all primitive meshes, counter-clockwise seen from outside.
        /// </summary>
        public static readonly ushort[] Indices;

        /// <summary>
        /// A unit sphere (radius 1).
        /// </summary>
        public static readonly MeshRange Sphere;

        /// <summary>
        /// A unit cylinder along Z (radius 1, from z = -0.5 to 0.5), capped.
        /// </summary>
        public static readonly MeshRange Cylinder;

        static SceneShapes()
        {
            var vertices = new List<float>();
            var indices = new List<ushort>();

            Sphere = buildSphere(vertices, indices, 24, 16);
            Cylinder = buildCylinder(vertices, indices, 24);

            Vertices = vertices.ToArray();
            Indices = indices.ToArray();
        }

        /// <summary>
        /// The built-in stylised controller. Grip space: the handle runs along Z (-Z towards the thumb, where the head with the
        /// thumbstick is) and +Y points forward out of the fist (where the trigger is).
        /// </summary>
        /// <param name="hand">The controller: its grip pose, and the inputs its parts move and light up with.</param>
        /// <param name="active">Whether it's the controller driving the pointer, which lights its accent band up.</param>
        /// <param name="accent">The accent colour.</param>
        /// <param name="parts">Receives the parts. Must have room for <see cref="MAX_PARTS"/>.</param>
        /// <returns>The number of parts written.</returns>
        public static int BuildController(in XrHandState hand, bool active, Vector3 accent, Span<ShapePart> parts)
        {
            var grip = Matrix4x4.CreateFromQuaternion(hand.GripOrientation) * Matrix4x4.CreateTranslation(hand.GripPosition);

            var body = new Vector3(0.085f, 0.09f, 0.10f);
            var bodyDark = new Vector3(0.05f, 0.055f, 0.065f);
            float accentLevel = active ? 0.85f : 0.3f;
            int count = 0;

            // Handle: a tapered capsule along Z.
            parts[count++] = new ShapePart(Cylinder, part(new Vector3(0.016f, 0.019f, 0.085f), Quaternion.Identity, new Vector3(0, 0, 0.005f)) * grip, body, Vector3.Zero);
            parts[count++] = new ShapePart(Sphere, part(new Vector3(0.016f, 0.019f, 0.012f), Quaternion.Identity, new Vector3(0, 0, 0.0475f)) * grip, body, Vector3.Zero);

            // Head: a flattened ellipsoid on top of the handle, reaching forward over the trigger and tilted towards the thumb.
            var headTilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f);
            var headCentre = new Vector3(0, 0.012f, -0.052f);
            parts[count++] = new ShapePart(Sphere, part(new Vector3(0.031f, 0.039f, 0.016f), headTilt, headCentre) * grip, body, Vector3.Zero);

            // Accent band around the head.
            parts[count++] = new ShapePart(Cylinder, part(new Vector3(0.0318f, 0.0398f, 0.006f), headTilt, headCentre) * grip, bodyDark, accent * accentLevel);

            // Thumbstick on the head's top face, leaning with the stick.
            var stickTilt = headTilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, hand.Thumbstick.X * 0.35f)
                                     * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -hand.Thumbstick.Y * 0.35f);
            var stickBase = headCentre + Vector3.Transform(new Vector3(0, -0.004f, -0.016f), headTilt);
            parts[count++] = new ShapePart(Cylinder,
                part(new Vector3(0.004f, 0.004f, 0.012f), stickTilt, stickBase + Vector3.Transform(new Vector3(0, 0, -0.004f), stickTilt)) * grip, bodyDark, Vector3.Zero);
            parts[count++] = new ShapePart(Sphere,
                part(new Vector3(0.008f, 0.008f, 0.003f), stickTilt, stickBase + Vector3.Transform(new Vector3(0, 0, -0.0105f), stickTilt)) * grip, body * 1.4f, Vector3.Zero);

            // Trigger: under the head at the front, pulled in and lit as it's pressed.
            float trigger = Math.Clamp(hand.Trigger, 0, 1);
            parts[count++] = new ShapePart(Sphere, part(new Vector3(0.0075f, 0.009f, 0.015f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.5f - 0.25f * trigger),
                new Vector3(0, 0.03f - 0.005f * trigger, -0.03f)) * grip, bodyDark, accent * (0.08f + 0.9f * trigger));

            // Grip button: along the front of the handle, lit as it's squeezed.
            float squeeze = Math.Clamp(hand.Squeeze, 0, 1);
            parts[count++] = new ShapePart(Sphere, part(new Vector3(0.011f, 0.007f, 0.024f), Quaternion.Identity, new Vector3(0, 0.0165f - 0.002f * squeeze, 0.008f)) * grip,
                bodyDark, accent * (0.05f + 0.8f * squeeze));

            return count;
        }

        /// <summary>
        /// How a surface is lit: a soft key light from above, a little fill, and a rim outlining the shape against dark surroundings.
        /// The same look as <see cref="OpenXRSceneRenderer"/>'s shaders give, for drawing which lights per vertex.
        /// </summary>
        /// <param name="normal">The surface's normal (normalised).</param>
        /// <param name="toViewer">The direction from the surface to the viewer (normalised).</param>
        /// <param name="baseColour">The surface's own colour.</param>
        /// <param name="emissive">Light the surface gives off.</param>
        /// <param name="rim">The colour of the rim light.</param>
        public static Vector3 Shade(Vector3 normal, Vector3 toViewer, Vector3 baseColour, Vector3 emissive, Vector3 rim)
        {
            float facing = Vector3.Dot(normal, toViewer);

            // Seen from behind (the mesh is drawn two-sided): lit as its other side.
            if (facing < 0)
            {
                normal = -normal;
                facing = -facing;
            }

            float diffuse = MathF.Max(Vector3.Dot(normal, light_direction), 0);
            float specular = MathF.Pow(MathF.Max(Vector3.Dot(normal, Vector3.Normalize(light_direction + toViewer)), 0), 40) * 0.35f;
            float rimLevel = MathF.Pow(1 - facing, 3);

            return baseColour * (0.45f + 0.9f * diffuse) + new Vector3(specular) + rim * rimLevel + emissive;
        }

        /// <summary>
        /// The rim light of the built-in shapes.
        /// </summary>
        public static readonly Vector3 DEFAULT_RIM = new Vector3(0.18f, 0.22f, 0.28f);

        private static readonly Vector3 light_direction = Vector3.Normalize(new Vector3(0.3f, 1.0f, 0.4f));

        private static Matrix4x4 part(Vector3 scale, Quaternion rotation, Vector3 translation)
            => Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

        private static MeshRange buildSphere(List<float> vertices, List<ushort> indices, int segments, int rings)
        {
            int firstVertex = vertices.Count / FLOATS_PER_VERTEX;
            int firstIndex = indices.Count;

            for (int r = 0; r <= rings; r++)
            {
                float theta = MathF.PI * r / rings;

                for (int s = 0; s <= segments; s++)
                {
                    float phi = 2 * MathF.PI * s / segments;
                    var n = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta));
                    vertices.AddRange(new[] { n.X, n.Y, n.Z, n.X, n.Y, n.Z });
                }
            }

            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = firstVertex + r * (segments + 1) + s;
                    int b = a + segments + 1;
                    // Counter-clockwise seen from outside (s turns anticlockwise about +Z, r moves away from the +Z pole).
                    indices.AddRange(new[] { (ushort)a, (ushort)b, (ushort)(a + 1), (ushort)(a + 1), (ushort)b, (ushort)(b + 1) });
                }
            }

            return new MeshRange(firstIndex, indices.Count - firstIndex);
        }

        private static MeshRange buildCylinder(List<float> vertices, List<ushort> indices, int segments)
        {
            int firstIndex = indices.Count;

            // Side.
            int side = vertices.Count / FLOATS_PER_VERTEX;

            for (int s = 0; s <= segments; s++)
            {
                float phi = 2 * MathF.PI * s / segments;
                float x = MathF.Cos(phi), y = MathF.Sin(phi);
                vertices.AddRange(new[] { x, y, -0.5f, x, y, 0 });
                vertices.AddRange(new[] { x, y, 0.5f, x, y, 0 });
            }

            for (int s = 0; s < segments; s++)
            {
                int a = side + s * 2;
                indices.AddRange(new[] { (ushort)a, (ushort)(a + 2), (ushort)(a + 1), (ushort)(a + 2), (ushort)(a + 3), (ushort)(a + 1) });
            }

            // Caps.
            foreach (float z in new[] { -0.5f, 0.5f })
            {
                int centre = vertices.Count / FLOATS_PER_VERTEX;
                vertices.AddRange(new[] { 0, 0, z, 0, 0, MathF.Sign(z) });

                for (int s = 0; s <= segments; s++)
                {
                    float phi = 2 * MathF.PI * s / segments;
                    vertices.AddRange(new[] { MathF.Cos(phi), MathF.Sin(phi), z, 0, 0, MathF.Sign(z) });
                }

                for (int s = 0; s < segments; s++)
                {
                    ushort a = (ushort)(centre + 1 + s), b = (ushort)(centre + 2 + s);

                    if (z > 0)
                        indices.AddRange(new[] { (ushort)centre, a, b });
                    else
                        indices.AddRange(new[] { (ushort)centre, b, a });
                }
            }

            return new MeshRange(firstIndex, indices.Count - firstIndex);
        }
    }
}
