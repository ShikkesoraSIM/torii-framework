// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using osu.Framework.Extensions;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// Gets models of the actual controllers in use from the runtime, and poses and animates them every frame.
    /// </summary>
    /// <remarks>
    /// Sources, in order of preference:
    /// <list type="number">
    /// <item>XR_EXT_interaction_render_model (cross-vendor): the runtime provides the model, the space it's tracked in, and the poses of
    /// its moving parts (buttons, trigger, thumbstick).</item>
    /// <item>XR_FB_render_model (Meta): the runtime provides the model, placed at the grip pose. Parts are animated if the model follows
    /// the WebXR input profile naming convention (<c>&lt;part&gt;_value</c> nodes moving between <c>_min</c> and <c>_max</c>).</item>
    /// <item>For development, <c>controller-left.glb</c> / <c>controller-right.glb</c> in the debug directory, handled like (2).</item>
    /// <item><see cref="GenericModels"/>: models of some controller, given by the game for when there's nothing better.</item>
    /// </list>
    /// A <see cref="HeldModel"/> (e.g. a pen) replaces all of these, in both hands, placed at the pointer's aim pose.
    /// Hands without a model are drawn with <see cref="OpenXRSceneRenderer"/>'s built-in stylised model.
    /// Runtime calls happen on the calling (render) thread; parsing and image decoding happen in the background.
    /// </remarks>
    internal sealed unsafe class OpenXRControllerModels : IDisposable
    {
        /// <summary>
        /// A model for one hand, posed for the current frame by <see cref="Update"/>.
        /// </summary>
        public sealed class HandModel
        {
            public required GltfModel Model { get; init; }
            public required string Source { get; init; }

            /// <summary>
            /// The space the model is tracked in (XR_EXT_render_model), or 0 to place it at the grip pose.
            /// </summary>
            public ulong Space { get; init; }

            /// <summary>
            /// Whether the model is placed at the aim pose (a <see cref="OpenXRControllerModels.HeldModel"/>) rather than the grip pose.
            /// Only applies without a <see cref="Space"/>.
            /// </summary>
            public bool AtAim { get; init; }

            /// <summary>
            /// Takes the model's own coordinates to the pose it's placed at. Only applies without a <see cref="Space"/>.
            /// </summary>
            public Matrix4x4 Placement { get; set; } = Matrix4x4.Identity;

            /// <summary>
            /// Whether this is one of the <see cref="GenericModels"/>: it gives way to a model of the actual controller when one arrives.
            /// </summary>
            public bool Generic { get; set; }

            /// <summary>
            /// The render model handle (XR_EXT_render_model), for its animated node states. 0 otherwise.
            /// </summary>
            public ulong RenderModel { get; init; }

            /// <summary>
            /// For each animatable node state reported by the runtime, the index of the glTF node it applies to (-1 if not found).
            /// </summary>
            public int[] AnimatableNodes { get; init; } = Array.Empty<int>();

            /// <summary>
            /// Parts animated by controller state, by the WebXR input profile convention.
            /// </summary>
            public WebXrAnimation[] Animations { get; init; } = Array.Empty<WebXrAnimation>();

            // Per frame.
            public Matrix4x4 Root;
            public bool Visible;
            public readonly Matrix4x4[] NodeWorld;
            public readonly bool[] NodeVisible;
            public readonly Matrix4x4[] LocalOverride;
            public readonly bool[] Overridden;
            public readonly bool[] Hidden;

            [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
            public HandModel(GltfModel model, string source)
            {
                Model = model;
                Source = source;
                int n = model.Nodes.Count;
                NodeWorld = new Matrix4x4[n];
                NodeVisible = new bool[n];
                LocalOverride = new Matrix4x4[n];
                Overridden = new bool[n];
                Hidden = new bool[n];
            }
        }

        /// <summary>
        /// A part moving between two poses with a controller input, by the WebXR input profile convention.
        /// </summary>
        public readonly record struct WebXrAnimation(int Node, WebXrInput Input, Vector3 MinT, Quaternion MinR, Vector3 MinS, Vector3 MaxT, Quaternion MaxR, Vector3 MaxS);

        public enum WebXrInput
        {
            Trigger,
            Squeeze,
            ThumbstickX,
            ThumbstickY,
            Primary,
            Secondary,
        }

        /// <summary>
        /// Models per hand (<see cref="OpenXRInput.LEFT"/>, <see cref="OpenXRInput.RIGHT"/>), or null where none is available.
        /// </summary>
        public readonly HandModel?[] Hands = new HandModel?[2];

        /// <summary>
        /// Invoked (on the render thread) when a hand's model is replaced, with the old model, so its GPU resources can be released.
        /// </summary>
        public event Action<HandModel>? ModelRemoved;

        private const double retry_interval = 3;
        private const int max_attempts = 20;

        private readonly XrFunctions xr;
        private readonly ulong instance;
        private readonly ulong session;
        private readonly ulong[] handPaths;
        private readonly bool useExt;
        private bool useFb;
        private readonly string? debugDirectory;

        private const string generic_source = "generic model";

        private readonly XrPlacedModel?[] genericModels = new XrPlacedModel?[2];

        /// <summary>
        /// Models of a controller for each hand (<see cref="OpenXRInput.LEFT"/>, <see cref="OpenXRInput.RIGHT"/>), placed at the grip
        /// pose where the runtime has no model of the actual controller; or null. See <see cref="OpenXRHostController.GenericControllerModels"/>.
        /// </summary>
        public void SetGenericModels(XrPlacedModel? left, XrPlacedModel? right)
        {
            if (ReferenceEquals(genericModels[0], left) && ReferenceEquals(genericModels[1], right))
                return;

            genericModels[0] = left;
            genericModels[1] = right;

            // Those in use are replaced (or dropped) by the next load.
            for (int hand = 0; hand < 2; hand++)
            {
                if (Hands[hand] is not { Generic: true } existing)
                    continue;

                Hands[hand] = null;
                ModelRemoved?.Invoke(existing);
            }

            needsLoad = true;
            attempts = 0;
            lastAttempt = DateTime.MinValue;
        }

        private byte[]? heldModel;

        /// <summary>
        /// A binary glTF model held in both hands in place of the controllers, or null to use the controllers' models.
        /// Its origin is placed at the aim pose (-Z along the pointer ray). See <see cref="OpenXRHostController.HeldModel"/>.
        /// </summary>
        public byte[]? HeldModel
        {
            get => heldModel;
            set
            {
                if (ReferenceEquals(value, heldModel))
                    return;

                heldModel = value;
                generation++;

                // Drop the current models right away (a load still in progress is for the old source and gets discarded):
                // the next load fills both hands from the new source, and hands it can't fill fall back to the built-in model.
                for (int hand = 0; hand < 2; hand++)
                {
                    if (Hands[hand] is not { } existing)
                        continue;

                    Hands[hand] = null;
                    ModelRemoved?.Invoke(existing);
                }

                Invalidate();
            }
        }

        // Incremented when the source of the models changes, so a load started for the previous source isn't installed.
        private int generation;
        private int pendingGeneration;

        private bool needsLoad = true;
        private bool replaceLoaded;
        private bool pendingReplaces;
        private int attempts;
        private DateTime lastAttempt = DateTime.MinValue;
        private Task<List<(int hand, HandModel model)>>? pending;

        // Resources to release when the models are replaced or on disposal (XR_EXT_render_model).
        private readonly List<ulong> extRenderModels = new List<ulong>();
        private readonly List<ulong> extSpaces = new List<ulong>();

        public OpenXRControllerModels(XrFunctions xr, ulong instance, ulong session, IReadOnlySet<string> enabledExtensions, string? debugDirectory)
        {
            this.xr = xr;
            this.instance = instance;
            this.session = session;
            this.debugDirectory = debugDirectory;

            useExt = enabledExtensions.Contains("XR_EXT_render_model") && enabledExtensions.Contains("XR_EXT_interaction_render_model");
            useFb = enabledExtensions.Contains("XR_FB_render_model");

            handPaths = new[]
            {
                XrUtil.StringToPath(xr, instance, "/user/hand/left"),
                XrUtil.StringToPath(xr, instance, "/user/hand/right"),
            };
        }

        /// <summary>
        /// Requests models to be (re)loaded, e.g. after the interaction profile or the set of models changed.
        /// </summary>
        public void Invalidate()
        {
            needsLoad = true;
            replaceLoaded = true;
            attempts = 0;
            lastAttempt = DateTime.MinValue;
        }

        /// <summary>
        /// Loads models if needed, and poses and animates them for the frame. Call once per frame on the render thread.
        /// </summary>
        public void Update(ulong baseSpace, long time, XrHandState[] hands)
        {
            completePendingLoad();
            startLoadIfNeeded();

            for (int hand = 0; hand < 2; hand++)
            {
                var model = Hands[hand];
                if (model == null)
                    continue;

                try
                {
                    pose(model, baseSpace, time, hands[hand]);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"OpenXR: posing the {(hand == 0 ? "left" : "right")} controller model failed; using the built-in model.");
                    Hands[hand] = null;
                    ModelRemoved?.Invoke(model);
                }
            }
        }

        private void pose(HandModel model, ulong baseSpace, long time, in XrHandState hand)
        {
            Array.Clear(model.Overridden);
            Array.Clear(model.Hidden);

            if (model.Space != 0)
            {
                var location = new XrSpaceLocation { type = XR_TYPE_SPACE_LOCATION };
                xr.xrLocateSpace(model.Space, baseSpace, time, &location).ThrowIfFailed(nameof(xr.xrLocateSpace));

                const ulong tracked = XrSpaceLocationFlags.XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT | XrSpaceLocationFlags.XR_SPACE_LOCATION_POSITION_TRACKED_BIT;
                model.Visible = (location.locationFlags & tracked) == tracked;
                model.Root = Matrix4x4.CreateFromQuaternion(location.pose.orientation.ToNumerics()) * Matrix4x4.CreateTranslation(location.pose.position.ToNumerics());

                if (model.Visible && model.AnimatableNodes.Length > 0)
                    applyNodeStates(model, time);
            }
            else if (model.AtAim)
            {
                model.Visible = hand.AimValid;
                model.Root = Matrix4x4.CreateFromQuaternion(hand.AimOrientation) * Matrix4x4.CreateTranslation(hand.AimPosition);
            }
            else
            {
                model.Visible = hand.GripValid;
                model.Root = model.Placement * Matrix4x4.CreateFromQuaternion(hand.GripOrientation) * Matrix4x4.CreateTranslation(hand.GripPosition);
                applyWebXrAnimations(model, hand);
            }

            if (model.Visible)
                model.Model.ComputeTransforms(model.NodeWorld, model.NodeVisible, model.LocalOverride, model.Overridden, model.Hidden);
        }

        private void applyNodeStates(HandModel model, long time)
        {
            int count = model.AnimatableNodes.Length;
            var states = stackalloc XrRenderModelNodeStateEXT[count];
            var getInfo = new XrRenderModelStateGetInfoEXT { type = XR_TYPE_RENDER_MODEL_STATE_GET_INFO_EXT, displayTime = time };
            var state = new XrRenderModelStateEXT { type = XR_TYPE_RENDER_MODEL_STATE_EXT, nodeStateCount = (uint)count, nodeStates = states };

            if (xr.xrGetRenderModelStateEXT(model.RenderModel, &getInfo, &state) != XrResult.XR_SUCCESS)
                return;

            for (int i = 0; i < count; i++)
            {
                int node = model.AnimatableNodes[i];
                if (node < 0)
                    continue;

                // The node pose replaces the node's own transform, relative to its parent.
                model.LocalOverride[node] = Matrix4x4.CreateFromQuaternion(states[i].nodePose.orientation.ToNumerics())
                                            * Matrix4x4.CreateTranslation(states[i].nodePose.position.ToNumerics());
                model.Overridden[node] = true;
                model.Hidden[node] = states[i].isVisible == 0;
            }
        }

        private static void applyWebXrAnimations(HandModel model, in XrHandState hand)
        {
            foreach (var a in model.Animations)
            {
                float t = Math.Clamp(a.Input switch
                {
                    WebXrInput.Trigger => hand.Trigger,
                    WebXrInput.Squeeze => hand.Squeeze,
                    // WebXR axes are -1..1 with up = -1; OpenXR's thumbstick Y is up = +1.
                    WebXrInput.ThumbstickX => (hand.Thumbstick.X + 1) / 2,
                    WebXrInput.ThumbstickY => (1 - hand.Thumbstick.Y) / 2,
                    WebXrInput.Primary => hand.PrimaryButton ? 1 : 0,
                    WebXrInput.Secondary => hand.SecondaryButton ? 1 : 0,
                    _ => 0,
                }, 0, 1);

                model.LocalOverride[a.Node] = Matrix4x4.CreateScale(Vector3.Lerp(a.MinS, a.MaxS, t))
                                              * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(a.MinR, a.MaxR, t))
                                              * Matrix4x4.CreateTranslation(Vector3.Lerp(a.MinT, a.MaxT, t));
                model.Overridden[a.Node] = true;
            }
        }

        #region Loading

        private void startLoadIfNeeded()
        {
            if (!needsLoad || pending != null || attempts >= max_attempts || (DateTime.UtcNow - lastAttempt).TotalSeconds < retry_interval)
                return;

            lastAttempt = DateTime.UtcNow;
            attempts++;
            pendingReplaces = replaceLoaded;
            pendingGeneration = generation;
            replaceLoaded = false;

            // Fetch the binary models from the runtime here (runtime calls stay on this thread); parse them in the background.
            var sources = new List<(int hand, byte[] glb, string source, ulong space, ulong renderModel, string[] nodeNames)>();

            try
            {
                if (heldModel != null)
                {
                    for (int hand = 0; hand < 2; hand++)
                        sources.Add((hand, heldModel, "held model", 0, 0, Array.Empty<string>()));
                }
                else
                {
                    if (useExt)
                        fetchExt(sources);

                    if (useFb)
                        fetchFb(sources);

                    fetchDebugFiles(sources);

                    // Last, and only once: the runtime is asked again for a while after, in case it has a model later.
                    for (int hand = 0; hand < 2; hand++)
                    {
                        if (genericModels[hand] is { } generic && Hands[hand] == null)
                            sources.Add((hand, generic.Glb, generic_source, 0, 0, Array.Empty<string>()));
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "OpenXR: couldn't get controller models from the runtime.");
            }

            if (sources.Count == 0)
                return;

            bool held = heldModel != null;
            var genericPlacements = new[] { genericModels[0]?.Placement ?? Matrix4x4.Identity, genericModels[1]?.Placement ?? Matrix4x4.Identity };

            pending = Task.Run(() =>
            {
                var results = new List<(int, HandModel)>();

                foreach (var s in sources)
                {
                    try
                    {
                        var gltf = GltfModel.FromGlb(s.glb);
                        var model = createHandModel(gltf, s.source, s.space, s.renderModel, s.nodeNames, held);

                        if (s.source == generic_source)
                        {
                            model.Generic = true;
                            model.Placement = genericPlacements[s.hand];
                        }

                        results.Add((s.hand, model));
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"OpenXR: couldn't read the {s.source} model: {e.Message}");
                    }
                }

                return results;
            });
        }

        private void completePendingLoad()
        {
            if (pending is not { IsCompleted: true })
                return;

            var task = pending;
            pending = null;

            // Loaded for a source that has since been replaced: a new load follows.
            if (!task.IsCompletedSuccessfully || pendingGeneration != generation)
                return;

            var installed = new bool[2];

            // Results are in order of preference: the first for each hand wins. A load caused by a change (e.g. a different controller)
            // replaces existing models; a retry only fills in hands that are still missing.
            foreach (var (hand, model) in task.GetResultSafely())
            {
                // (A generic model is replaced by a retry too, by a model of the actual controller.)
                if (installed[hand] || (Hands[hand] is { } current && !pendingReplaces && !(current.Generic && !model.Generic)))
                    continue;

                // Nothing better than what there is.
                if (model.Generic && Hands[hand] != null)
                    continue;

                if (Hands[hand] is { } existing)
                    ModelRemoved?.Invoke(existing);

                Hands[hand] = model;
                installed[hand] = true;
                Logger.Log($"OpenXR: {(hand == 0 ? "left" : "right")} controller model from {model.Source}: {model.Model}"
                           + (model.Animations.Length > 0 ? $", {model.Animations.Length} animated parts" : string.Empty)
                           + (model.AnimatableNodes.Length > 0 ? $", {model.AnimatableNodes.Length} animatable nodes" : string.Empty));
            }

            needsLoad = Hands[0] is null or { Generic: true } || Hands[1] is null or { Generic: true };
        }

        private static HandModel createHandModel(GltfModel gltf, string source, ulong space, ulong renderModel, string[] nodeNames, bool atAim)
        {
            var nodeIndex = new Dictionary<string, int>();
            for (int i = 0; i < gltf.Nodes.Count; i++)
                nodeIndex.TryAdd(gltf.Nodes[i].Name, i);

            var animatable = new int[nodeNames.Length];
            for (int i = 0; i < nodeNames.Length; i++)
                animatable[i] = nodeIndex.TryGetValue(nodeNames[i], out int n) ? n : -1;

            return new HandModel(gltf, source)
            {
                Space = space,
                AtAim = atAim,
                RenderModel = renderModel,
                AnimatableNodes = animatable,
                // Runtime-animated models are driven by the runtime; others may follow the WebXR convention.
                Animations = renderModel != 0 ? Array.Empty<WebXrAnimation>() : findWebXrAnimations(gltf, nodeIndex),
            };
        }

        private static WebXrAnimation[] findWebXrAnimations(GltfModel gltf, Dictionary<string, int> nodeIndex)
        {
            var animations = new List<WebXrAnimation>();

            for (int i = 0; i < gltf.Nodes.Count; i++)
            {
                string name = gltf.Nodes[i].Name;
                if (!name.EndsWith("_value", StringComparison.Ordinal))
                    continue;

                string prefix = name[..^"_value".Length];

                if (!nodeIndex.TryGetValue(prefix + "_min", out int min) || !nodeIndex.TryGetValue(prefix + "_max", out int max))
                    continue;

                WebXrInput? input = prefix switch
                {
                    _ when prefix.Contains("trigger", StringComparison.Ordinal) => WebXrInput.Trigger,
                    _ when prefix.Contains("squeeze", StringComparison.Ordinal) => WebXrInput.Squeeze,
                    _ when prefix.Contains("thumbstick_xaxis", StringComparison.Ordinal) => WebXrInput.ThumbstickX,
                    _ when prefix.Contains("thumbstick_yaxis", StringComparison.Ordinal) => WebXrInput.ThumbstickY,
                    _ when prefix.StartsWith("a_button", StringComparison.Ordinal) || prefix.StartsWith("x_button", StringComparison.Ordinal) => WebXrInput.Primary,
                    _ when prefix.StartsWith("b_button", StringComparison.Ordinal) || prefix.StartsWith("y_button", StringComparison.Ordinal) => WebXrInput.Secondary,
                    _ => null,
                };

                if (input == null)
                    continue;

                if (!Matrix4x4.Decompose(gltf.Nodes[min].Local, out var minS, out var minR, out var minT)
                    || !Matrix4x4.Decompose(gltf.Nodes[max].Local, out var maxS, out var maxR, out var maxT))
                    continue;

                animations.Add(new WebXrAnimation(i, input.Value, minT, minR, minS, maxT, maxR, maxS));
            }

            return animations.ToArray();
        }

        private void fetchExt(List<(int, byte[], string, ulong, ulong, string[])> sources)
        {
            releaseExt();

            var enumerateInfo = new XrInteractionRenderModelIdsEnumerateInfoEXT { type = XR_TYPE_INTERACTION_RENDER_MODEL_IDS_ENUMERATE_INFO_EXT };
            uint count;
            xr.xrEnumerateInteractionRenderModelIdsEXT(session, &enumerateInfo, 0, &count, null).ThrowIfFailed(nameof(xr.xrEnumerateInteractionRenderModelIdsEXT));

            if (count == 0)
                return;

            var ids = new ulong[count];
            fixed (ulong* p = ids)
                xr.xrEnumerateInteractionRenderModelIdsEXT(session, &enumerateInfo, count, &count, p).ThrowIfFailed(nameof(xr.xrEnumerateInteractionRenderModelIdsEXT));

            foreach (ulong id in ids)
            {
                // No glTF extensions: interaction render models must be available without any.
                var createInfo = new XrRenderModelCreateInfoEXT { type = XR_TYPE_RENDER_MODEL_CREATE_INFO_EXT, renderModelId = id };
                ulong renderModel;
                if (xr.xrCreateRenderModelEXT(session, &createInfo, &renderModel) != XrResult.XR_SUCCESS)
                    continue;

                extRenderModels.Add(renderModel);

                // Which hand it's held in.
                fixed (ulong* paths = handPaths)
                {
                    var userPathInfo = new XrInteractionRenderModelTopLevelUserPathGetInfoEXT
                    {
                        type = XR_TYPE_INTERACTION_RENDER_MODEL_TOP_LEVEL_USER_PATH_GET_INFO_EXT,
                        topLevelUserPathCount = 2,
                        topLevelUserPaths = paths,
                    };

                    ulong userPath;
                    if (xr.xrGetRenderModelPoseTopLevelUserPathEXT(renderModel, &userPathInfo, &userPath) != XrResult.XR_SUCCESS)
                        continue;

                    int hand = Array.IndexOf(handPaths, userPath);
                    if (hand < 0)
                        continue;

                    var propertiesInfo = new XrRenderModelPropertiesGetInfoEXT { type = XR_TYPE_RENDER_MODEL_PROPERTIES_GET_INFO_EXT };
                    var properties = new XrRenderModelPropertiesEXT { type = XR_TYPE_RENDER_MODEL_PROPERTIES_EXT };
                    xr.xrGetRenderModelPropertiesEXT(renderModel, &propertiesInfo, &properties).ThrowIfFailed(nameof(xr.xrGetRenderModelPropertiesEXT));

                    var assetInfo = new XrRenderModelAssetCreateInfoEXT { type = XR_TYPE_RENDER_MODEL_ASSET_CREATE_INFO_EXT, cacheId = properties.cacheId };
                    ulong asset;
                    if (xr.xrCreateRenderModelAssetEXT(session, &assetInfo, &asset) != XrResult.XR_SUCCESS)
                        continue;

                    try
                    {
                        byte[] glb = getAssetData(asset);
                        string[] nodeNames = getAnimatableNodeNames(asset, properties.animatableNodeCount);

                        var spaceInfo = new XrRenderModelSpaceCreateInfoEXT { type = XR_TYPE_RENDER_MODEL_SPACE_CREATE_INFO_EXT, renderModel = renderModel };
                        ulong space;
                        xr.xrCreateRenderModelSpaceEXT(session, &spaceInfo, &space).ThrowIfFailed(nameof(xr.xrCreateRenderModelSpaceEXT));
                        extSpaces.Add(space);

                        sources.Add((hand, glb, "XR_EXT_render_model", space, renderModel, nodeNames));
                    }
                    finally
                    {
                        // The asset is only needed to get the data.
                        xr.xrDestroyRenderModelAssetEXT(asset);
                    }
                }
            }
        }

        private byte[] getAssetData(ulong asset)
        {
            var getInfo = new XrRenderModelAssetDataGetInfoEXT { type = XR_TYPE_RENDER_MODEL_ASSET_DATA_GET_INFO_EXT };
            var data = new XrRenderModelAssetDataEXT { type = XR_TYPE_RENDER_MODEL_ASSET_DATA_EXT };
            xr.xrGetRenderModelAssetDataEXT(asset, &getInfo, &data).ThrowIfFailed(nameof(xr.xrGetRenderModelAssetDataEXT));

            byte[] buffer = new byte[data.bufferCountOutput];

            fixed (byte* p = buffer)
            {
                data.bufferCapacityInput = (uint)buffer.Length;
                data.buffer = p;
                xr.xrGetRenderModelAssetDataEXT(asset, &getInfo, &data).ThrowIfFailed(nameof(xr.xrGetRenderModelAssetDataEXT));
            }

            return buffer;
        }

        private string[] getAnimatableNodeNames(ulong asset, uint count)
        {
            if (count == 0)
                return Array.Empty<string>();

            var nodes = new XrRenderModelAssetNodePropertiesEXT[count];

            fixed (XrRenderModelAssetNodePropertiesEXT* p = nodes)
            {
                var getInfo = new XrRenderModelAssetPropertiesGetInfoEXT { type = XR_TYPE_RENDER_MODEL_ASSET_PROPERTIES_GET_INFO_EXT };
                var properties = new XrRenderModelAssetPropertiesEXT { type = XR_TYPE_RENDER_MODEL_ASSET_PROPERTIES_EXT, nodePropertyCount = count, nodeProperties = p };
                xr.xrGetRenderModelAssetPropertiesEXT(asset, &getInfo, &properties).ThrowIfFailed(nameof(xr.xrGetRenderModelAssetPropertiesEXT));
            }

            var names = new string[count];

            for (int i = 0; i < count; i++)
            {
                fixed (byte* name = nodes[i].uniqueName)
                    names[i] = XrUtil.ReadString(name, (int)XrConstants.XR_MAX_RENDER_MODEL_ASSET_NODE_NAME_SIZE_EXT);
            }

            return names;
        }

        private void fetchFb(List<(int, byte[], string, ulong, ulong, string[])> sources)
        {
            uint count;
            var result = xr.xrEnumerateRenderModelPathsFB(session, 0, &count, null);

            if (result != XrResult.XR_SUCCESS)
            {
                // E.g. the system doesn't support loading render models: don't try again.
                Logger.Log($"OpenXR: XR_FB_render_model unavailable ({result}).");
                useFb = false;
                return;
            }

            var paths = new XrRenderModelPathInfoFB[count];
            for (int i = 0; i < count; i++)
                paths[i].type = XR_TYPE_RENDER_MODEL_PATH_INFO_FB;

            fixed (XrRenderModelPathInfoFB* p = paths)
                xr.xrEnumerateRenderModelPathsFB(session, count, &count, p).ThrowIfFailed(nameof(xr.xrEnumerateRenderModelPathsFB));

            foreach (var pathInfo in paths)
            {
                string path = XrUtil.PathToString(xr, instance, pathInfo.path);

                int hand = path switch
                {
                    "/model_fb/controller/left" => OpenXRInput.LEFT,
                    "/model_fb/controller/right" => OpenXRInput.RIGHT,
                    _ => -1,
                };

                if (hand < 0 || sources.Exists(s => s.Item1 == hand) || (Hands[hand] != null && !replaceLoadedOrPending))
                    continue;

                // Subset 1: the most basic glTF the runtime offers.
                var capabilities = new XrRenderModelCapabilitiesRequestFB
                {
                    type = XR_TYPE_RENDER_MODEL_CAPABILITIES_REQUEST_FB,
                    flags = XrRenderModelFlagsFB.XR_RENDER_MODEL_SUPPORTS_GLTF_2_0_SUBSET_1_BIT_FB,
                };

                var properties = new XrRenderModelPropertiesFB { type = XR_TYPE_RENDER_MODEL_PROPERTIES_FB, next = &capabilities };

                // XR_RENDER_MODEL_UNAVAILABLE_FB (e.g. the controller isn't connected yet) is a success code: retried later.
                if (xr.xrGetRenderModelPropertiesFB(session, pathInfo.path, &properties) != XrResult.XR_SUCCESS || properties.modelKey == 0)
                    continue;

                var loadInfo = new XrRenderModelLoadInfoFB { type = XR_TYPE_RENDER_MODEL_LOAD_INFO_FB, modelKey = properties.modelKey };
                var buffer = new XrRenderModelBufferFB { type = XR_TYPE_RENDER_MODEL_BUFFER_FB };

                if (xr.xrLoadRenderModelFB(session, &loadInfo, &buffer) != XrResult.XR_SUCCESS || buffer.bufferCountOutput == 0)
                    continue;

                byte[] glb = new byte[buffer.bufferCountOutput];

                fixed (byte* b = glb)
                {
                    buffer.bufferCapacityInput = (uint)glb.Length;
                    buffer.buffer = b;
                    if (xr.xrLoadRenderModelFB(session, &loadInfo, &buffer) != XrResult.XR_SUCCESS)
                        continue;
                }

                string name = XrUtil.ReadString(properties.modelName, (int)XrConstants.XR_MAX_RENDER_MODEL_NAME_SIZE_FB);
                sources.Add((hand, glb, $"XR_FB_render_model ({name})", 0, 0, Array.Empty<string>()));
            }
        }

        private void fetchDebugFiles(List<(int, byte[], string, ulong, ulong, string[])> sources)
        {
            if (debugDirectory == null)
                return;

            for (int hand = 0; hand < 2; hand++)
            {
                string file = Path.Combine(debugDirectory, hand == OpenXRInput.LEFT ? "controller-left.glb" : "controller-right.glb");

                if ((Hands[hand] == null || replaceLoadedOrPending) && !sources.Exists(s => s.Item1 == hand) && File.Exists(file))
                    sources.Add((hand, File.ReadAllBytes(file), Path.GetFileName(file), 0, 0, Array.Empty<string>()));
            }
        }

        private bool replaceLoadedOrPending => pendingReplaces;

        private void releaseExt()
        {
            // Only release handles no current model uses.
            foreach (ulong space in extSpaces.ToArray())
            {
                if (Array.Exists(Hands, h => h?.Space == space))
                    continue;

                xr.xrDestroySpace(space);
                extSpaces.Remove(space);
            }

            foreach (ulong renderModel in extRenderModels.ToArray())
            {
                if (Array.Exists(Hands, h => h?.RenderModel == renderModel))
                    continue;

                xr.xrDestroyRenderModelEXT(renderModel);
                extRenderModels.Remove(renderModel);
            }
        }

        #endregion

        public void Dispose()
        {
            pending?.Wait(1000);

            foreach (ulong space in extSpaces)
                xr.xrDestroySpace(space);

            foreach (ulong renderModel in extRenderModels)
                xr.xrDestroyRenderModelEXT(renderModel);

            extSpaces.Clear();
            extRenderModels.Clear();
        }

        /// <summary>
        /// Lists the models' sources, for logs.
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            for (int hand = 0; hand < 2; hand++)
                sb.Append($"{(hand == 0 ? "left" : "right")}: {Hands[hand]?.Source ?? "built-in"} ");
            return sb.ToString().TrimEnd();
        }
    }
}
