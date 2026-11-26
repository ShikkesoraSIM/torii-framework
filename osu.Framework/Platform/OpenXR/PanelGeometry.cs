// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Placement and shape of the game panel: a section of a vertical cylinder around the viewer (or a flat quad as fallback).
    /// </summary>
    /// <remarks>
    /// All positions are in the session's base space. In the panel's own frame the viewer (and the cylinder's axis) is at the origin,
    /// the panel's centre is straight ahead along -Z at <see cref="Radius"/>, and +Y runs along the cylinder's axis.
    /// The image maps linearly to arc angle horizontally and to height vertically, exactly as a cylinder composition layer shows it.
    /// </remarks>
    public sealed class PanelGeometry
    {
        /// <summary>
        /// The cylinder's axis point (normally the viewer's head).
        /// </summary>
        public Vector3 Centre { get; set; }

        /// <summary>
        /// The panel frame's orientation: -Z towards the panel's centre, +Y along the cylinder's axis.
        /// </summary>
        public Quaternion Orientation { get; set; } = Quaternion.Identity;

        /// <summary>
        /// Distance from the axis to the panel, in metres.
        /// </summary>
        public float Radius { get; set; } = 1.5f;

        /// <summary>
        /// Horizontal angle covered by the panel, in radians.
        /// </summary>
        public float HorizontalAngle { get; set; } = 60 * MathF.PI / 180;

        /// <summary>
        /// Width / height of the panel's image.
        /// </summary>
        public float Aspect { get; set; } = 16f / 9;

        /// <summary>
        /// Whether the panel is a cylinder section (otherwise a flat quad).
        /// </summary>
        public bool Cylinder { get; set; } = true;

        /// <summary>
        /// Width of the flat fallback quad, matching <see cref="HorizontalAngle"/> at <see cref="Radius"/>.
        /// </summary>
        public float FlatWidth => 2 * Radius * MathF.Tan(HorizontalAngle / 2);

        /// <summary>
        /// Height of the panel in metres. For the cylinder: arc length / aspect, so pixels are square on the surface.
        /// </summary>
        public float Height => (Cylinder ? Radius * HorizontalAngle : FlatWidth) / Aspect;

        public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Orientation);

        public Vector3 ToPanelSpace(Vector3 point) => Vector3.Transform(point - Centre, Quaternion.Inverse(Orientation));

        public Vector3 ToPanelDirection(Vector3 direction) => Vector3.Transform(direction, Quaternion.Inverse(Orientation));

        public Vector3 FromPanelSpace(Vector3 point) => Vector3.Transform(point, Orientation) + Centre;

        /// <summary>
        /// Whether <paramref name="point"/> (in the base space) is beyond the panel's surface, as seen from the viewer.
        /// </summary>
        public bool IsBehind(Vector3 point) => isBehindLocal(ToPanelSpace(point));

        private bool isBehindLocal(Vector3 p) => Cylinder ? p.X * p.X + p.Z * p.Z > Radius * Radius : p.Z < -Radius;

        /// <summary>
        /// How far <paramref name="point"/> (in the base space) is beyond the panel's surface, as seen from the viewer, in metres.
        /// Negative in front of it.
        /// </summary>
        /// <remarks>
        /// Measured straight out from the surface (radially for the cylinder), whether or not the point is within the panel's bounds.
        /// </remarks>
        public float DepthBehind(Vector3 point)
        {
            Vector3 p = ToPanelSpace(point);
            return Cylinder ? MathF.Sqrt(p.X * p.X + p.Z * p.Z) - Radius : -Radius - p.Z;
        }

        /// <summary>
        /// Intersects a ray with the panel's surface (unbounded: <paramref name="uv"/> may fall outside [0, 1]).
        /// </summary>
        /// <remarks>
        /// A ray starting behind the surface (see <see cref="IsBehind"/>, e.g. a controller pushed through the panel) hits where its line
        /// crosses the surface nearest to the origin, in either direction, so pointing carries on through the panel.
        /// </remarks>
        /// <param name="origin">Ray origin in the base space.</param>
        /// <param name="direction">Normalised ray direction.</param>
        /// <param name="hit">The intersection in the base space.</param>
        /// <param name="uv">Panel coordinates: (0, 0) top-left, (1, 1) bottom-right.</param>
        public bool Intersect(Vector3 origin, Vector3 direction, out Vector3 hit, out Vector2 uv)
        {
            hit = default;

            if (!IntersectLocal(ToPanelSpace(origin), ToPanelDirection(direction), out Vector3 p, out uv))
                return false;

            hit = FromPanelSpace(p);
            return true;
        }

        /// <summary>
        /// <see cref="Intersect"/>, in the panel's own frame.
        /// </summary>
        /// <param name="o">Ray origin in the panel's frame.</param>
        /// <param name="d">Ray direction in the panel's frame (need not be normalised).</param>
        /// <param name="p">The intersection in the panel's frame.</param>
        /// <param name="uv">Panel coordinates: (0, 0) top-left, (1, 1) bottom-right.</param>
        public bool IntersectLocal(Vector3 o, Vector3 d, out Vector3 p, out Vector2 uv)
        {
            p = default;
            uv = default;

            if (Cylinder)
            {
                // |o.xz + t d.xz| = r.
                float a = d.X * d.X + d.Z * d.Z;
                if (a < 1e-8f)
                    return false;

                float b = o.X * d.X + o.Z * d.Z;
                float c = o.X * o.X + o.Z * o.Z - Radius * Radius;
                float discriminant = b * b - a * c;

                if (discriminant < 0)
                    return false;

                float root = MathF.Sqrt(discriminant);
                float t = (-b + root) / a;

                if (c > 0)
                {
                    // Behind the surface: the nearest crossing, ahead or behind.
                    float other = (-b - root) / a;
                    if (MathF.Abs(other) < MathF.Abs(t))
                        t = other;
                }
                else if (t <= 0)
                {
                    // In front of it (inside the cylinder) the far root is the one ahead.
                    return false;
                }

                p = o + d * t;

                float yaw = MathF.Atan2(p.X, -p.Z);
                uv = new Vector2(0.5f + yaw / HorizontalAngle, 0.5f - p.Y / Height);
            }
            else
            {
                bool behind = o.Z < -Radius;

                if (behind ? MathF.Abs(d.Z) < 1e-4f : d.Z > -1e-4f)
                    return false;

                float t = (-Radius - o.Z) / d.Z;
                if (t <= 0 && !behind)
                    return false;

                p = o + d * t;
                uv = new Vector2(p.X / FlatWidth + 0.5f, 0.5f - p.Y / Height);
            }

            return true;
        }

        /// <summary>
        /// The point on the panel's surface at panel coordinates <paramref name="uv"/>, in the panel's own frame.
        /// </summary>
        public Vector3 SurfacePointLocal(Vector2 uv)
        {
            float y = (0.5f - uv.Y) * Height;

            if (!Cylinder)
                return new Vector3((uv.X - 0.5f) * FlatWidth, y, -Radius);

            float yaw = (uv.X - 0.5f) * HorizontalAngle;
            return new Vector3(Radius * MathF.Sin(yaw), y, -Radius * MathF.Cos(yaw));
        }

        public static bool IsWithin(Vector2 uv, float tolerance)
            => uv.X >= -tolerance && uv.X <= 1 + tolerance && uv.Y >= -tolerance && uv.Y <= 1 + tolerance;

        /// <summary>
        /// The orientation of a small flat marker lying on the panel at <paramref name="hit"/>, facing the viewer (+Z towards the viewer).
        /// </summary>
        public Quaternion SurfaceOrientation(Vector3 hit)
        {
            if (!Cylinder)
                return Orientation;

            // Facing the axis: +Z onto the inward horizontal normal, +Y along the axis.
            Vector3 p = ToPanelSpace(hit);
            Vector3 z = new Vector3(-p.X, 0, -p.Z);
            z = z.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(z);
            Vector3 y = Vector3.UnitY;
            Vector3 x = Vector3.Cross(y, z);

            var rotation = new Matrix4x4(
                x.X, x.Y, x.Z, 0,
                y.X, y.Y, y.Z, 0,
                z.X, z.Y, z.Z, 0,
                0, 0, 0, 1);

            return Quaternion.Normalize(Orientation * Quaternion.CreateFromRotationMatrix(rotation));
        }

        public void CopyFrom(PanelGeometry other)
        {
            Centre = other.Centre;
            Orientation = other.Orientation;
            Radius = other.Radius;
            HorizontalAngle = other.HorizontalAngle;
            Aspect = other.Aspect;
            Cylinder = other.Cylinder;
        }
    }
}
