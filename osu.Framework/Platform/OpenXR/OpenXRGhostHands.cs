// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;
using System.Threading.Tasks;
using osu.Framework.Extensions;
using osu.Framework.Logging;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// A second pair of controllers drawn in the scene where the game says, e.g. those of the player of a replay being watched.
    /// See <see cref="OpenXRHostController.GhostHands"/>.
    /// </summary>
    /// <remarks>
    /// Set from any thread; drawn with the next frame.
    /// </remarks>
    public sealed class OpenXRGhostHands
    {
        private readonly object stateLock = new object();

        private XrHandState left;
        private XrHandState right;
        private int activeHand;
        private bool shown;
        private byte[]? heldModel;

        /// <summary>
        /// Shows the hands at the given poses (in the session's LOCAL space), until <see cref="Hide"/> or the next call.
        /// </summary>
        /// <remarks>
        /// A hand is drawn if its grip pose is valid: as a controller there, with trigger and squeeze as given; or, with a
        /// <see cref="HeldModel"/>, as that model at its aim pose (which must be valid too).
        /// </remarks>
        /// <param name="left">The left hand.</param>
        /// <param name="right">The right hand.</param>
        /// <param name="activeHand">The hand to highlight (<see cref="OpenXRInput.LEFT"/> or <see cref="OpenXRInput.RIGHT"/>), or -1.</param>
        public void Show(in XrHandState left, in XrHandState right, int activeHand = -1)
        {
            lock (stateLock)
            {
                this.left = left;
                this.right = right;
                this.activeHand = activeHand;
                shown = true;
            }
        }

        public void Hide()
        {
            lock (stateLock)
                shown = false;
        }

        /// <summary>
        /// A binary glTF model drawn in place of the controllers, or null. Its origin is placed at the aim pose (-Z along the pointer
        /// ray), like <see cref="OpenXRHostController.HeldModel"/>.
        /// </summary>
        public byte[]? HeldModel
        {
            get
            {
                lock (stateLock) return heldModel;
            }
            set
            {
                lock (stateLock) heldModel = value;
            }
        }

        #region Render thread

        /// <summary>
        /// The hands for the frame being drawn.
        /// </summary>
        internal readonly XrHandState[] Hands = new XrHandState[2];

        /// <summary>
        /// The held model per hand, posed for the frame being drawn; null where the built-in controller is drawn.
        /// </summary>
        internal readonly OpenXRControllerModels.HandModel?[] Models = new OpenXRControllerModels.HandModel?[2];

        internal int ActiveHand { get; private set; }

        /// <summary>
        /// Whether the hands are drawn holding the <see cref="HeldModel"/> this frame (its pointer ray comes out of its tip).
        /// </summary>
        internal bool HoldingModel => Models[0] != null;

        private byte[]? loadedSource;
        private Task<GltfModel>? pending;
        private byte[]? pendingSource;

        /// <summary>
        /// Takes what was last set as the hands of the frame about to be drawn.
        /// </summary>
        /// <param name="release">Releases the GPU resources of a model no longer drawn.</param>
        /// <returns>Whether there is anything to draw.</returns>
        internal bool Prepare(Action<GltfModel> release)
        {
            byte[]? source;
            bool visible;

            lock (stateLock)
            {
                Hands[0] = left;
                Hands[1] = right;
                ActiveHand = activeHand;
                source = heldModel;
                visible = shown;
            }

            if (!visible)
            {
                Hands[0] = Hands[1] = default;
                return false;
            }

            updateModel(source, release);

            for (int hand = 0; hand < 2; hand++)
            {
                if (Models[hand] is not { } model)
                    continue;

                ref var state = ref Hands[hand];

                model.Visible = state.GripValid && state.AimValid;

                if (model.Visible)
                {
                    model.Root = Matrix4x4.CreateFromQuaternion(state.AimOrientation) * Matrix4x4.CreateTranslation(state.AimPosition);
                    model.Model.ComputeTransforms(model.NodeWorld, model.NodeVisible);
                }

                // The model stands in for the controller, also while it can't be placed.
                state.GripValid &= model.Visible;
            }

            // A hand which isn't drawn has no pointer either.
            for (int hand = 0; hand < 2; hand++)
                Hands[hand].AimValid &= Hands[hand].GripValid;

            return Hands[0].GripValid || Hands[1].GripValid;
        }

        private void updateModel(byte[]? source, Action<GltfModel> release)
        {
            if (pending is { IsCompleted: true } task)
            {
                pending = null;

                if (task.IsCompletedSuccessfully && ReferenceEquals(pendingSource, source))
                {
                    var gltf = task.GetResultSafely();
                    Models[0] = new OpenXRControllerModels.HandModel(gltf, "ghost held model") { AtAim = true };
                    Models[1] = new OpenXRControllerModels.HandModel(gltf, "ghost held model") { AtAim = true };
                }
                else if (task.IsFaulted)
                    Logger.Log($"OpenXR: couldn't read the ghost hands' held model: {task.Exception?.InnerException?.Message}");

                // A model which can't be read isn't tried again.
                loadedSource = pendingSource;
                pendingSource = null;
            }

            if (ReferenceEquals(source, loadedSource) || pending != null)
                return;

            if (Models[0] is { } old)
            {
                Models[0] = Models[1] = null;
                release(old.Model);
            }

            if (source == null)
            {
                loadedSource = null;
                return;
            }

            // Parsed in the background; the built-in controllers are drawn until it's ready.
            pendingSource = source;
            pending = Task.Run(() => GltfModel.FromGlb(source));
        }

        #endregion
    }
}
