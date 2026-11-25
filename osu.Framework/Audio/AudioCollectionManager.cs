// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;

namespace osu.Framework.Audio
{
    /// <summary>
    /// A collection of audio components which need central property control.
    /// </summary>
    public class AudioCollectionManager<T> : AdjustableAudioComponent, IBassAudio
        where T : AudioComponent
    {
        internal List<T> Items = new List<T>();
        private readonly HashSet<T> itemSet = new HashSet<T>(ReferenceEqualityComparer.Instance);

        public void AddItem(T item)
        {
            EnqueueAction(delegate
            {
                if (!itemSet.Add(item)) return;

                if (item is IAdjustableAudioComponent adjustable)
                    adjustable.BindAdjustments(this);

                Items.Add(item);
                ItemAdded(item);
            });
        }

        public void RemoveItem(T item)
        {
            EnqueueAction(() =>
            {
                if (!itemSet.Remove(item)) return;

                if (item is IAdjustableAudioComponent adjustable)
                    adjustable.UnbindAdjustments(this);

                int index = Items.IndexOf(item);
                if (index >= 0)
                {
                    int last = Items.Count - 1;
                    Items[index] = Items[last];
                    Items.RemoveAt(last);
                }

                ItemRemoved(item);
            });
        }

        void IBassAudio.UpdateDevice(int deviceIndex) => UpdateDevice(deviceIndex);

        internal virtual void UpdateDevice(int deviceIndex)
        {
            foreach (var item in Items.OfType<IBassAudio>())
                item.UpdateDevice(deviceIndex);
        }

        protected override void UpdateChildren()
        {
            base.UpdateChildren();

            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var item = Items[i];

                if (!item.IsAlive)
                {
                    int last = Items.Count - 1;
                    Items[i] = Items[last];
                    Items.RemoveAt(last);
                    itemSet.Remove(item);
                    ItemRemoved(item);
                    continue;
                }

                item.Update();
            }
        }

        /// <summary>
        /// An item has been added to <see cref="Items"/>.
        /// </summary>
        /// <param name="item">The item which was added.</param>
        protected virtual void ItemAdded(T item)
        {
        }

        /// <summary>
        /// An item has been removed from <see cref="Items"/>. Implementations should dispose the removed item if required.
        /// </summary>
        /// <param name="item">The item which was removed.</param>
        protected virtual void ItemRemoved(T item)
        {
        }

        protected override void Dispose(bool disposing)
        {
            // make the items queue their disposal, so they get disposed when UpdateChildren updates them.
            foreach (var i in Items)
                i.Dispose();

            base.Dispose(disposing);
        }
    }
}
