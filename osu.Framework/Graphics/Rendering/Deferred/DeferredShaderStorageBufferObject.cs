// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using osu.Framework.Development;
using osu.Framework.Graphics.Rendering.Deferred.Allocation;
using osu.Framework.Graphics.Rendering.Deferred.Events;
using osu.Framework.Graphics.Veldrid.Buffers;
using Veldrid;

namespace osu.Framework.Graphics.Rendering.Deferred
{
    internal class DeferredShaderStorageBufferObject<TData> : IShaderStorageBufferObject<TData>, IDeferredShaderStorageBufferObject, IVeldridUniformBuffer
        where TData : unmanaged, IEquatable<TData>
    {
        public int Size { get; }

        private readonly TData[] data;
        private readonly DeviceBuffer buffer;
        private readonly DeferredRenderer renderer;

        private readonly int elementSize;

        // Preserve committed bytes between frames so sparse dirty ranges retain untouched entries.
        private readonly byte[] pendingData;
        private int dirtyStart = int.MaxValue;
        private int dirtyEnd;

        public DeferredShaderStorageBufferObject(DeferredRenderer renderer, int ssboSize)
        {
            Trace.Assert(ThreadSafety.IsDrawThread);

            this.renderer = renderer;

            elementSize = Unsafe.SizeOf<TData>();

            Size = ssboSize;
            buffer = renderer.Factory.CreateBuffer(new BufferDescription((uint)(elementSize * Size), BufferUsage.StructuredBufferReadOnly | BufferUsage.Dynamic, (uint)elementSize, true));

            data = new TData[Size];
            pendingData = new byte[elementSize * Size];
        }

        public TData this[int index]
        {
            get => data[index];
            set
            {
                if (value.Equals(data[index]))
                    return;

                data[index] = value;
                renderer.Context.EnqueueEvent(SetShaderStorageBufferObjectDataEvent.Create(renderer, this, index, value));
            }
        }

        public void Write(int index, MemoryReference memory)
        {
            ThreadSafety.EnsureDrawThread();

            if (dirtyEnd == 0)
                renderer.Context.PendingStorageBuffers.Add(this);

            int offset = index * elementSize;
            renderer.Context.GetRegion(memory).CopyTo(pendingData.AsSpan(offset));
            dirtyStart = Math.Min(dirtyStart, offset);
            dirtyEnd = Math.Max(dirtyEnd, offset + memory.Length);
        }

        public void Commit()
        {
            ThreadSafety.EnsureDrawThread();

            if (dirtyEnd == 0)
                return;

            // All deferred storage writes precede rendering. Upload the final changed range once,
            // retaining untouched entries between sparse writes instead of mapping for every mask.
            renderer.Context.Device.UpdateBuffer(buffer, (uint)dirtyStart, pendingData.AsSpan(dirtyStart, dirtyEnd - dirtyStart));
            dirtyStart = int.MaxValue;
            dirtyEnd = 0;
        }

        public ResourceSet GetResourceSet(ResourceLayout layout)
            => renderer.Factory.CreateResourceSet(new ResourceSetDescription(layout, buffer));

        public void ResetCounters()
        {
        }

        public void Dispose()
            => buffer.Dispose();
    }
}
