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
        /// A hand is drawn if its grip pose is valid: as a controller there (one of the <see cref="ControllerModels"/>, or a plain
        /// built-in shape with trigger and squeeze as given); or, with a <see cref="HeldModel"/>, as that model at its aim pose (which
        /// must be valid too).
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

        private (XrPlacedModel? left, XrPlacedModel? right) controllerModels;

        /// <summary>
        /// Models of a controller for the left and right hand, placed at the grip pose; or null for the plain built-in shape.
        /// </summary>
        public (XrPlacedModel? left, XrPlacedModel? right) ControllerModels
        {
            get
            {
                lock (stateLock) return controllerModels;
            }
            set
            {
                lock (stateLock) controllerModels = value;
            }
        }

        #region Render thread

        /// <summary>
        /// The hands for the frame being drawn.
        /// </summary>
        internal readonly XrHandState[] Hands = new XrHandState[2];

        /// <summary>
        /// The model per hand, posed for the frame being drawn; null where the built-in controller is drawn.
        /// </summary>
        internal readonly OpenXRControllerModels.HandModel?[] Models = new OpenXRControllerModels.HandModel?[2];

        internal int ActiveHand { get; private set; }

        /// <summary>
        /// Whether the hands are drawn holding the <see cref="HeldModel"/> this frame (its pointer ray comes out of its tip).
        /// </summary>
        internal bool HoldingModel => Models[0] is { AtAim: true };

        // What each hand's model is of, and the reading of one still under way.
        private readonly byte[]?[] loadedSource = new byte[]?[2];
        private readonly Task<GltfModel>?[] pending = new Task<GltfModel>?[2];
        private readonly byte[]?[] pendingSource = new byte[]?[2];

        /// <summary>
        /// Takes what was last set as the hands of the frame about to be drawn.
        /// </summary>
        /// <param name="release">Releases the GPU resources of a model no longer drawn.</param>
        /// <returns>Whether there is anything to draw.</returns>
        internal bool Prepare(Action<GltfModel> release)
        {
            byte[]? held;
            (XrPlacedModel? left, XrPlacedModel? right) controllers;
            bool visible;

            lock (stateLock)
            {
                Hands[0] = left;
                Hands[1] = right;
                ActiveHand = activeHand;
                held = heldModel;
                controllers = controllerModels;
                visible = shown;
            }

            if (!visible)
            {
                Hands[0] = Hands[1] = default;
                return false;
            }

            for (int hand = 0; hand < 2; hand++)
            {
                var controller = hand == 0 ? controllers.left : controllers.right;

                updateModel(hand, held ?? controller?.Glb, held != null, held == null && controller != null ? controller.Placement : Matrix4x4.Identity, release);

                if (Models[hand] is not { } model)
                    continue;

                ref var state = ref Hands[hand];

                model.Visible = state.GripValid && (!model.AtAim || state.AimValid);

                if (model.Visible)
                {
                    model.Root = model.AtAim
                        ? Matrix4x4.CreateFromQuaternion(state.AimOrientation) * Matrix4x4.CreateTranslation(state.AimPosition)
                        : model.Placement * Matrix4x4.CreateFromQuaternion(state.GripOrientation) * Matrix4x4.CreateTranslation(state.GripPosition);

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

        private void updateModel(int hand, byte[]? source, bool atAim, Matrix4x4 placement, Action<GltfModel> release)
        {
            if (pending[hand] is { IsCompleted: true } task)
            {
                pending[hand] = null;

                if (task.IsCompletedSuccessfully && ReferenceEquals(pendingSource[hand], source))
                    Models[hand] = new OpenXRControllerModels.HandModel(task.GetResultSafely(), "ghost model") { AtAim = atAim, Placement = placement };
                else if (task.IsFaulted)
                    Logger.Log($"OpenXR: couldn't read a ghost hand's model: {task.Exception?.InnerException?.Message}");

                // A model which can't be read isn't tried again.
                loadedSource[hand] = pendingSource[hand];
                pendingSource[hand] = null;
            }

            if (ReferenceEquals(source, loadedSource[hand]) || pending[hand] != null)
                return;

            if (Models[hand] is { } old)
            {
                Models[hand] = null;

                // (Both hands hold the same held model.)
                if (!ReferenceEquals(Models[1 - hand]?.Model, old.Model))
                    release(old.Model);
            }

            if (source == null)
            {
                loadedSource[hand] = null;
                return;
            }

            // Read in the background, once for both hands if it's the same; the built-in controllers are drawn until it's ready.
            pendingSource[hand] = source;
            pending[hand] = ReferenceEquals(pendingSource[1 - hand], source) && pending[1 - hand] is { } other ? other : Task.Run(() => GltfModel.FromGlb(source));
        }

        #endregion
    }
}
