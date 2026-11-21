// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using osu.Framework.Graphics.Rendering.Deferred.Allocation;

namespace osu.Framework.Graphics.Rendering.Deferred
{
    internal interface IDeferredShaderStorageBufferObject
    {
        /// <summary>
        /// Stages data for the buffer at the given index.
        /// </summary>
        /// <param name="index">The element index in the buffer at which to begin writing.</param>
        /// <param name="memory">The data to write.</param>
        void Write(int index, MemoryReference memory);

        /// <summary>
        /// Uploads all staged writes before rendering begins. Does nothing when there are no pending writes.
        /// </summary>
        void Commit();
    }
}
