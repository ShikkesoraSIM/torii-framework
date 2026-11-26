// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Numerics;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// A model as it was made, and how it's placed where it's drawn: e.g. a model of a controller, whatever way round and wherever
    /// its origin is, with what puts it in the hand.
    /// </summary>
    /// <param name="Glb">The model, a binary glTF in metres. Used as it is.</param>
    /// <param name="Position">Where the model's origin goes, in the frame of the pose the model is placed at (for a controller: its grip pose).</param>
    /// <param name="Rotation">How the model is turned in that frame.</param>
    public sealed record XrPlacedModel(byte[] Glb, Vector3 Position, Quaternion Rotation)
    {
        /// <summary>
        /// Takes the model's own coordinates to the frame of the pose it's placed at (row-vector convention).
        /// </summary>
        internal Matrix4x4 Placement => Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
    }
}
