// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using osu.Framework.Logging;
using osu.Framework.Platform.OpenXR.Native;
using static osu.Framework.Platform.OpenXR.Native.XrStructureType;

namespace osu.Framework.Platform.OpenXR
{
    /// <summary>
    /// The state of one tracked controller, sampled for a single frame.
    /// </summary>
    public struct XrHandState
    {
        /// <summary>
        /// Whether <see cref="AimPosition"/> and <see cref="AimOrientation"/> are valid this frame.
        /// </summary>
        public bool AimValid;

        /// <summary>
        /// Origin of the controller's aim ray, in the session's LOCAL reference space (metres). Includes
        /// <see cref="OpenXRInput.AimPositionOffset"/>.
        /// </summary>
        public Vector3 AimPosition;

        /// <summary>
        /// Orientation of the aim ray, including <see cref="OpenXRInput.AimRotationOffset"/>. The ray points along -Z of this
        /// orientation.
        /// </summary>
        public Quaternion AimOrientation;

        /// <summary>
        /// The direction of the aim ray.
        /// </summary>
        public Vector3 AimDirection;

        public float Trigger;
        public float Squeeze;
        public Vector2 Thumbstick;

        /// <summary>
        /// A (right) or X (left).
        /// </summary>
        public bool PrimaryButton;

        /// <summary>
        /// B (right) or Y (left).
        /// </summary>
        public bool SecondaryButton;

        /// <summary>
        /// The menu button (left controller only).
        /// </summary>
        public bool MenuButton;

        /// <summary>
        /// Whether <see cref="GripPosition"/> and <see cref="GripOrientation"/> are valid this frame.
        /// </summary>
        public bool GripValid;

        /// <summary>
        /// Where the controller is held (OpenXR grip pose), in the session's LOCAL reference space: the handle runs along Z
        /// (-Z towards the thumb), +Y points forward out of the fist.
        /// </summary>
        public Vector3 GripPosition;

        public Quaternion GripOrientation;
    }

    /// <summary>
    /// A controller vibration.
    /// </summary>
    /// <param name="Amplitude">Strength, 0 to 1.</param>
    /// <param name="Duration">Duration in milliseconds.</param>
    /// <param name="Frequency">Frequency in Hz, or 0 for the runtime's default.</param>
    public readonly record struct HapticPulse(float Amplitude, double Duration, float Frequency);

    /// <summary>
    /// OpenXR actions for two motion controllers (Meta Touch / Touch Plus).
    /// </summary>
    internal sealed unsafe class OpenXRInput : IDisposable
    {
        public const int LEFT = 0;
        public const int RIGHT = 1;

        /// <summary>
        /// Per-hand state from the last <see cref="Sync"/>. Indexed by <see cref="LEFT"/> / <see cref="RIGHT"/>.
        /// </summary>
        public readonly XrHandState[] Hands = new XrHandState[2];

        private readonly XrFunctions xr;
        private readonly ulong session;

        private readonly ulong actionSet;
        private readonly ulong[] handPaths;

        private readonly ulong aimAction;
        private readonly ulong gripAction;
        private readonly ulong hapticAction;
        private readonly ulong triggerAction;
        private readonly ulong squeezeAction;
        private readonly ulong thumbstickAction;
        private readonly ulong primaryAction;
        private readonly ulong secondaryAction;
        private readonly ulong menuAction;

        /// <summary>
        /// The usual grip orientation relative to the aim orientation (aim: -Z forward, +Y up), for a controller held pointing forward with its
        /// handle tilted 40 degrees forward from vertical. In grip space -Z runs up the handle towards the thumb, +Y points out of the fist
        /// (forward and down), and +X is normal to the palm, which for both hands points left.
        /// </summary>
        private static readonly Quaternion aim_to_grip = createAimToGrip(40 * MathF.PI / 180);

        private static Quaternion createAimToGrip(float tilt)
        {
            float c = MathF.Cos(tilt), s = MathF.Sin(tilt);

            // Rows are the grip axes in aim space.
            var x = new Vector3(-1, 0, 0);
            var y = new Vector3(0, -s, -c);
            var z = new Vector3(0, -c, s);

            return Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                x.X, x.Y, x.Z, 0,
                y.X, y.Y, y.Z, 0,
                z.X, z.Y, z.Z, 0,
                0, 0, 0, 1));
        }

        private readonly ulong[] aimSpaces = new ulong[2];
        private readonly ulong[] gripSpaces = new ulong[2];

        /// <summary>
        /// Rotation of the pointer ray relative to the controller's aim pose. See <see cref="OpenXRInputHandler.PointerTilt"/>.
        /// </summary>
        public Quaternion AimRotationOffset { get; set; } = Quaternion.Identity;

        /// <summary>
        /// Where the pointer ray starts, in the frame of the rotated aim pose (metres). See <see cref="OpenXRInputHandler.PointerOffset"/>.
        /// </summary>
        public Vector3 AimPositionOffset { get; set; }

        public OpenXRInput(XrFunctions xr, ulong instance, ulong session, IReadOnlySet<string> enabledExtensions)
        {
            this.xr = xr;
            this.session = session;

            handPaths = new[]
            {
                XrUtil.StringToPath(xr, instance, "/user/hand/left"),
                XrUtil.StringToPath(xr, instance, "/user/hand/right"),
            };

            actionSet = createActionSet(instance, "gameplay", "Gameplay");

            aimAction = createAction("aim", "Aim", XrActionType.XR_ACTION_TYPE_POSE_INPUT, handPaths);
            gripAction = createAction("grip", "Grip pose", XrActionType.XR_ACTION_TYPE_POSE_INPUT, handPaths);
            hapticAction = createAction("haptic", "Vibration", XrActionType.XR_ACTION_TYPE_VIBRATION_OUTPUT, handPaths);
            triggerAction = createAction("trigger", "Trigger", XrActionType.XR_ACTION_TYPE_FLOAT_INPUT, handPaths);
            squeezeAction = createAction("squeeze", "Grip", XrActionType.XR_ACTION_TYPE_FLOAT_INPUT, handPaths);
            thumbstickAction = createAction("thumbstick", "Thumbstick", XrActionType.XR_ACTION_TYPE_VECTOR2F_INPUT, handPaths);
            primaryAction = createAction("primary", "A / X", XrActionType.XR_ACTION_TYPE_BOOLEAN_INPUT, handPaths);
            secondaryAction = createAction("secondary", "B / Y", XrActionType.XR_ACTION_TYPE_BOOLEAN_INPUT, handPaths);
            menuAction = createAction("menu", "Menu", XrActionType.XR_ACTION_TYPE_BOOLEAN_INPUT, new[] { handPaths[LEFT] });

            var bindings = new (ulong action, string path)[]
            {
                (aimAction, "/user/hand/left/input/aim/pose"),
                (aimAction, "/user/hand/right/input/aim/pose"),
                (gripAction, "/user/hand/left/input/grip/pose"),
                (gripAction, "/user/hand/right/input/grip/pose"),
                (hapticAction, "/user/hand/left/output/haptic"),
                (hapticAction, "/user/hand/right/output/haptic"),
                (triggerAction, "/user/hand/left/input/trigger/value"),
                (triggerAction, "/user/hand/right/input/trigger/value"),
                (squeezeAction, "/user/hand/left/input/squeeze/value"),
                (squeezeAction, "/user/hand/right/input/squeeze/value"),
                (thumbstickAction, "/user/hand/left/input/thumbstick"),
                (thumbstickAction, "/user/hand/right/input/thumbstick"),
                (primaryAction, "/user/hand/left/input/x/click"),
                (primaryAction, "/user/hand/right/input/a/click"),
                (secondaryAction, "/user/hand/left/input/y/click"),
                (secondaryAction, "/user/hand/right/input/b/click"),
                (menuAction, "/user/hand/left/input/menu/click"),
            };

            suggestBindings(instance, "/interaction_profiles/oculus/touch_controller", bindings);

            if (enabledExtensions.Contains("XR_META_touch_controller_plus"))
                suggestBindings(instance, "/interaction_profiles/meta/touch_controller_plus", bindings);

            // Valve Index controllers (desktop SteamVR, and Monado's remote-controlled simulator): A / B on both hands, the system button as menu.
            suggestBindings(instance, "/interaction_profiles/valve/index_controller", new (ulong action, string path)[]
            {
                (aimAction, "/user/hand/left/input/aim/pose"),
                (aimAction, "/user/hand/right/input/aim/pose"),
                (gripAction, "/user/hand/left/input/grip/pose"),
                (gripAction, "/user/hand/right/input/grip/pose"),
                (hapticAction, "/user/hand/left/output/haptic"),
                (hapticAction, "/user/hand/right/output/haptic"),
                (triggerAction, "/user/hand/left/input/trigger/value"),
                (triggerAction, "/user/hand/right/input/trigger/value"),
                (squeezeAction, "/user/hand/left/input/squeeze/value"),
                (squeezeAction, "/user/hand/right/input/squeeze/value"),
                (thumbstickAction, "/user/hand/left/input/thumbstick"),
                (thumbstickAction, "/user/hand/right/input/thumbstick"),
                (primaryAction, "/user/hand/left/input/a/click"),
                (primaryAction, "/user/hand/right/input/a/click"),
                (secondaryAction, "/user/hand/left/input/b/click"),
                (secondaryAction, "/user/hand/right/input/b/click"),
                (menuAction, "/user/hand/left/input/system/click"),
            });

            // Fallback for other runtimes and devices, e.g. Monado's simulated controllers: aim, select as the trigger, menu.
            suggestBindings(instance, "/interaction_profiles/khr/simple_controller", new (ulong action, string path)[]
            {
                (aimAction, "/user/hand/left/input/aim/pose"),
                (aimAction, "/user/hand/right/input/aim/pose"),
                (gripAction, "/user/hand/left/input/grip/pose"),
                (gripAction, "/user/hand/right/input/grip/pose"),
                (hapticAction, "/user/hand/left/output/haptic"),
                (hapticAction, "/user/hand/right/output/haptic"),
                (triggerAction, "/user/hand/left/input/select/click"),
                (triggerAction, "/user/hand/right/input/select/click"),
                (menuAction, "/user/hand/left/input/menu/click"),
            });

            ulong set = actionSet;
            var attachInfo = new XrSessionActionSetsAttachInfo
            {
                type = XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO,
                countActionSets = 1,
                actionSets = &set,
            };

            xr.xrAttachSessionActionSets(session, &attachInfo).ThrowIfFailed(nameof(xr.xrAttachSessionActionSets));

            for (int hand = 0; hand < 2; hand++)
            {
                aimSpaces[hand] = createActionSpace(aimAction, handPaths[hand]);
                gripSpaces[hand] = createActionSpace(gripAction, handPaths[hand]);
            }

            Logger.Log("OpenXR: controller actions attached.");
        }

        /// <summary>
        /// Syncs action state and locates the aim poses in <paramref name="baseSpace"/> at <paramref name="time"/>
        /// (normally the predicted display time of the frame being produced).
        /// </summary>
        /// <returns>Whether the sync succeeded. Input is unavailable (e.g. session not focused) otherwise.</returns>
        public bool Sync(ulong baseSpace, long time)
        {
            var activeSet = new XrActiveActionSet { actionSet = actionSet };
            var syncInfo = new XrActionsSyncInfo
            {
                type = XR_TYPE_ACTIONS_SYNC_INFO,
                countActiveActionSets = 1,
                activeActionSets = &activeSet,
            };

            var result = xr.xrSyncActions(session, &syncInfo);

            // XR_SESSION_NOT_FOCUSED is a success code: actions are inactive while another app (e.g. the system menu) has focus.
            if (result != XrResult.XR_SUCCESS)
            {
                result.ThrowIfFailed(nameof(xr.xrSyncActions));
                Array.Clear(Hands);
                return false;
            }

            for (int hand = 0; hand < 2; hand++)
            {
                ref var state = ref Hands[hand];
                ulong subaction = handPaths[hand];

                state.Trigger = getFloat(triggerAction, subaction);
                state.Squeeze = getFloat(squeezeAction, subaction);
                state.Thumbstick = getVector2(thumbstickAction, subaction);
                state.PrimaryButton = getBool(primaryAction, subaction);
                state.SecondaryButton = getBool(secondaryAction, subaction);
                state.MenuButton = hand == LEFT && getBool(menuAction, subaction);

                var location = new XrSpaceLocation { type = XR_TYPE_SPACE_LOCATION };
                xr.xrLocateSpace(aimSpaces[hand], baseSpace, time, &location).ThrowIfFailed(nameof(xr.xrLocateSpace));

                const ulong required = XrSpaceLocationFlags.XR_SPACE_LOCATION_ORIENTATION_VALID_BIT | XrSpaceLocationFlags.XR_SPACE_LOCATION_POSITION_VALID_BIT;

                var gripLocation = new XrSpaceLocation { type = XR_TYPE_SPACE_LOCATION };
                xr.xrLocateSpace(gripSpaces[hand], baseSpace, time, &gripLocation).ThrowIfFailed(nameof(xr.xrLocateSpace));

                state.GripValid = (gripLocation.locationFlags & required) == required;

                if (state.GripValid)
                {
                    state.GripPosition = gripLocation.pose.position.ToNumerics();
                    state.GripOrientation = gripLocation.pose.orientation.ToNumerics();
                }

                state.AimValid = (location.locationFlags & required) == required;

                if (state.AimValid)
                {
                    state.AimPosition = location.pose.position.ToNumerics();
                    state.AimOrientation = location.pose.orientation.ToNumerics();

                    // Some runtimes (e.g. Monado's simulated controllers) report the grip pose identical to the aim pose. Hold the
                    // controller the usual way instead: handle tilted back from the pointing direction, a little behind the aim origin.
                    if (state.GripValid && state.GripPosition == state.AimPosition && MathF.Abs(Quaternion.Dot(state.GripOrientation, state.AimOrientation)) > 0.99999f)
                    {
                        state.GripOrientation = state.AimOrientation * aim_to_grip;
                        state.GripPosition = state.AimPosition + Vector3.Transform(new Vector3(0, -0.02f, 0.05f), state.AimOrientation);
                    }

                    // E.g. the ray coming out of the tip of a held pen rather than the front of the controller.
                    state.AimOrientation *= AimRotationOffset;
                    state.AimPosition += Vector3.Transform(AimPositionOffset, state.AimOrientation);

                    state.AimDirection = Vector3.Transform(-Vector3.UnitZ, state.AimOrientation);
                }
            }

            return true;
        }

        private ulong createActionSet(ulong instance, string name, string localizedName)
        {
            var info = new XrActionSetCreateInfo { type = XR_TYPE_ACTION_SET_CREATE_INFO };
            XrUtil.WriteString(info.actionSetName, (int)XrConstants.XR_MAX_ACTION_SET_NAME_SIZE, name);
            XrUtil.WriteString(info.localizedActionSetName, (int)XrConstants.XR_MAX_LOCALIZED_ACTION_SET_NAME_SIZE, localizedName);

            ulong result;
            xr.xrCreateActionSet(instance, &info, &result).ThrowIfFailed(nameof(xr.xrCreateActionSet));
            return result;
        }

        private ulong createAction(string name, string localizedName, XrActionType type, ulong[] subactionPaths)
        {
            var info = new XrActionCreateInfo
            {
                type = XR_TYPE_ACTION_CREATE_INFO,
                actionType = type,
                countSubactionPaths = (uint)subactionPaths.Length,
            };

            XrUtil.WriteString(info.actionName, (int)XrConstants.XR_MAX_ACTION_NAME_SIZE, name);
            XrUtil.WriteString(info.localizedActionName, (int)XrConstants.XR_MAX_LOCALIZED_ACTION_NAME_SIZE, localizedName);

            ulong result;

            fixed (ulong* paths = subactionPaths)
            {
                info.subactionPaths = paths;
                xr.xrCreateAction(actionSet, &info, &result).ThrowIfFailed($"{nameof(xr.xrCreateAction)}({name})");
            }

            return result;
        }

        private void suggestBindings(ulong instance, string profile, (ulong action, string path)[] bindings)
        {
            var suggested = new XrActionSuggestedBinding[bindings.Length];

            for (int i = 0; i < bindings.Length; i++)
                suggested[i] = new XrActionSuggestedBinding { action = bindings[i].action, binding = XrUtil.StringToPath(xr, instance, bindings[i].path) };

            fixed (XrActionSuggestedBinding* s = suggested)
            {
                var info = new XrInteractionProfileSuggestedBinding
                {
                    type = XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING,
                    interactionProfile = XrUtil.StringToPath(xr, instance, profile),
                    countSuggestedBindings = (uint)suggested.Length,
                    suggestedBindings = s,
                };

                xr.xrSuggestInteractionProfileBindings(instance, &info).ThrowIfFailed($"{nameof(xr.xrSuggestInteractionProfileBindings)}({profile})");
            }
        }

        private float getFloat(ulong action, ulong subaction)
        {
            var info = new XrActionStateGetInfo { type = XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = subaction };
            var state = new XrActionStateFloat { type = XR_TYPE_ACTION_STATE_FLOAT };
            xr.xrGetActionStateFloat(session, &info, &state).ThrowIfFailed(nameof(xr.xrGetActionStateFloat));
            return state.isActive != 0 ? state.currentState : 0;
        }

        private Vector2 getVector2(ulong action, ulong subaction)
        {
            var info = new XrActionStateGetInfo { type = XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = subaction };
            var state = new XrActionStateVector2f { type = XR_TYPE_ACTION_STATE_VECTOR2F };
            xr.xrGetActionStateVector2f(session, &info, &state).ThrowIfFailed(nameof(xr.xrGetActionStateVector2f));
            return state.isActive != 0 ? new Vector2(state.currentState.x, state.currentState.y) : Vector2.Zero;
        }

        private bool getBool(ulong action, ulong subaction)
        {
            var info = new XrActionStateGetInfo { type = XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = subaction };
            var state = new XrActionStateBoolean { type = XR_TYPE_ACTION_STATE_BOOLEAN };
            xr.xrGetActionStateBoolean(session, &info, &state).ThrowIfFailed(nameof(xr.xrGetActionStateBoolean));
            return state.isActive != 0 && state.currentState != 0;
        }

        /// <summary>
        /// Vibrates a controller. Failures are ignored: haptics are never essential.
        /// </summary>
        public void Vibrate(int hand, HapticPulse pulse)
        {
            var info = new XrHapticActionInfo { type = XR_TYPE_HAPTIC_ACTION_INFO, action = hapticAction, subactionPath = handPaths[hand] };
            var vibration = new XrHapticVibration
            {
                type = XR_TYPE_HAPTIC_VIBRATION,
                amplitude = pulse.Amplitude,
                duration = Math.Max(1, (long)(pulse.Duration * 1_000_000)),
                frequency = pulse.Frequency,
            };

            xr.xrApplyHapticFeedback(session, &info, (XrHapticBaseHeader*)&vibration);
        }

        private ulong createActionSpace(ulong action, ulong subactionPath)
        {
            var spaceInfo = new XrActionSpaceCreateInfo
            {
                type = XR_TYPE_ACTION_SPACE_CREATE_INFO,
                action = action,
                subactionPath = subactionPath,
                poseInActionSpace = XrUtil.IdentityPose,
            };

            ulong space;
            xr.xrCreateActionSpace(session, &spaceInfo, &space).ThrowIfFailed(nameof(xr.xrCreateActionSpace));
            return space;
        }

        public void Dispose()
        {
            foreach (ulong space in aimSpaces.Concat(gripSpaces))
            {
                if (space != 0)
                    xr.xrDestroySpace(space);
            }

            // Destroying the action set also destroys its actions.
            if (actionSet != 0)
                xr.xrDestroyActionSet(actionSet);
        }
    }
}
