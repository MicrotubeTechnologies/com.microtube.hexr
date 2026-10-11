using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using HexR;
using TMPro;
using UnityEngine.Events;
using System;
using HaptGlove;

namespace HexR
{
    /// <summary>
    /// HexR's own grab: the object is picked up by the glove's finger and palm colliders closing
    /// on it, pinch or palm, and the haptics are sent straight to the glove as the fingers touch.
    ///
    /// One of two grab mechanisms, not a legacy one. <see cref="HexRInteractableHaptics"/> is the
    /// other: it adds HexR haptics to your SDK's own interactable (XRGrabInteractable on OpenXR,
    /// Grabbable/HandGrabInteractable on Meta), so the object also grabs without gloves and like
    /// everything else in the scene. Use this one where the grab should come from the HexR hand
    /// itself -- it needs a Rigidbody and a trigger collider, and no interaction SDK at all.
    /// </summary>
    public class HexRGrabbable : MonoBehaviour
    {
        public enum Options { PinchGrab, PalmGrab }
        [Header("General Settings")]
        [Space(10)]
        public Options TypeOfGrab;
        public enum Option { On, Off }
        public Option Gravity;

        [Tooltip("Assign the target gameobject, this is to allow the grab collider to be seperated from other colliders in the gameobject.)")]
        public GameObject TheObject; //Optional, if you want to seperate the grab zone from the action object, which will allow you to not include nested collider in child

        [Tooltip("How firmly each touching finger is pressed while held: 10 is barely there, 60 is firm, 0 is no haptics. The same scale as Interactable Haptics.")]
        [Range(0f, 60f)]
        public float HapticStrength = 10f;

        [Tooltip("Trigger functions on grab and release event.  ")]
        [Space(5)]
        public UnityEvent OnGrab, OnRelease;


        private GameObject RHandParent, LHandParent;
        // Null for an object at the root of the scene, and SetParent(null) puts it back there.
        private Transform OriginalParent;
        // The Pressure Controller of the hand holding the object, so disabling the object
        // mid-grab can release that glove.
        private PressureTrackerMain grabbingTracker;
        private Coroutine resetGrab;
        #region Bool Fields
        bool RThumb, RIndex, RLittle, RMiddle, RRing, RPalm; // if finger is touching
        bool LThumb, LIndex, LLittle, LMiddle, LRing, LPalm;
        bool RThumbHaptics, RIndexHaptics, RLittleHaptics, RMiddleHaptics, RRingHaptics, RPalmHaptics; // if haptics were triggered
        bool LThumbHaptics, LIndexHaptics, LLittleHaptics, LMiddleHaptics, LRingHaptics, LPalmHaptics;
        #endregion

        private FingerUseTracking RfingerUseTracking, LfingeruseTracking;
        private PressureTrackerMain RightPressureTracker, LeftPressureTracker;
        private Rigidbody objectRigidbody;
        [HideInInspector]
        public bool isGrab = false, InvokeEventReady = true;
        private bool ReadyToActivateGrab = true;
        // Start is called before the first frame update
        void Start()
        {
            // Create new empty objects with unique names for right and left hand parents
            RHandParent = new GameObject("RightHandParent");
            LHandParent = new GameObject("LeftHandParent");

            objectRigidbody = gameObject.GetComponent<Rigidbody>();
            if (TheObject == null)
            {
                TheObject = gameObject;
            }

            // Was TheObject.transform.parent.gameObject, which threw for any object at the root
            // of the scene -- the cube Create Demo Scene adds among them.
            OriginalParent = TheObject.transform.parent;
            SetUpBool();

            ResolveHands();
        }

        // Through the rig, and allowed to come up empty: a scene with no rig, or a rig missing a
        // hand, leaves that hand unfelt instead of throwing out of Start.
        private void ResolveHands()
        {
            HexRManager manager = HexRManager.Instance;
            if (manager == null)
            {
                Debug.LogWarning("[HexR] " + name + ": no HexRManager in the scene, so this object can be grabbed but not felt.", this);
                return;
            }

            if (manager.rightHand != null) { RfingerUseTracking = manager.rightHand.GetComponent<FingerUseTracking>(); }
            else { Debug.Log("Right hand is not found"); }
            if (manager.leftHand != null) { LfingeruseTracking = manager.leftHand.GetComponent<FingerUseTracking>(); }
            else { Debug.Log("Left hand is not found"); }

            RightPressureTracker = FindPressureTracker(PressureTrackerMain.HandType.Right);
            LeftPressureTracker = FindPressureTracker(PressureTrackerMain.HandType.Left);
        }

        // By name first, as before, then by the side Auto Setup records on each one.
        private static PressureTrackerMain FindPressureTracker(PressureTrackerMain.HandType side)
        {
            string label = side == PressureTrackerMain.HandType.Left ? "Left" : "Right";
            GameObject named = GameObject.Find(label + " Pressure Controller");
            PressureTrackerMain tracker = named != null ? named.GetComponent<PressureTrackerMain>() : null;
            if (tracker != null)
            {
                return tracker;
            }

            foreach (PressureTrackerMain candidate in HexRCompat.FindAll<PressureTrackerMain>(true))
            {
                if (candidate.handType == side)
                {
                    return candidate;
                }
            }

            Debug.Log(label + " pressuretracker is not found");
            return null;
        }

        private void OnDisable()
        {
            // Disabled or destroyed mid-grab: let go of the glove too, not just the object.
            if (!InvokeEventReady && grabbingTracker != null)
            {
                grabbingTracker.HandGrabbingCheck(false);
                RemoveHaptics(grabbingTracker);
                InvokeEventReady = true;
            }

            // Coroutines outlive a disabled component (only deactivating the GameObject stops
            // them), so stop this one rather than let it release a grab that is already over.
            if (resetGrab != null)
            {
                StopCoroutine(resetGrab);
                resetGrab = null;
            }
            grabbingTracker = null;
        }

        // Update is called once per frame
        void Update()
        {

            if (TypeOfGrab == Options.PinchGrab)
            {
                if (RThumb)
                {
                    if (RIndex || RMiddle || RRing || RLittle)
                    {
                        isGrab = true;
                        IsGrab(RHandParent, RfingerUseTracking, RightPressureTracker, false);
                    }
                    else
                    {
                        isGrab = false;
                        NotGrab(RightPressureTracker);
                    }

                }
                if (LThumb)
                {
                    if (LIndex || LMiddle || LRing || LLittle)
                    {
                        isGrab = true;
                        IsGrab(LHandParent, LfingeruseTracking, LeftPressureTracker, true);
                    }
                    else
                    {
                        isGrab = false;
                        NotGrab(LeftPressureTracker);
                    }

                }
                if (!RThumb && !LThumb)
                {
                    TheObject.transform.SetParent(OriginalParent);
                }
            }
            else if (TypeOfGrab == Options.PalmGrab)
            {
                if (RPalm)
                {
                    if (RIndex || RMiddle || RRing || RLittle)
                    {
                        isGrab = true;
                        IsGrab(RHandParent, RfingerUseTracking, RightPressureTracker, false);
                    }
                    else
                    {
                        isGrab = false;
                        NotGrab(RightPressureTracker);
                    }

                }
                if (LPalm)
                {
                    if (LIndex || LMiddle || LRing || LLittle)
                    {
                        isGrab = true;
                        IsGrab(LHandParent, LfingeruseTracking, LeftPressureTracker, true);
                    }
                    else
                    {
                        isGrab = false;
                        NotGrab(LeftPressureTracker);
                    }

                }
                if (!RPalm && !LPalm)
                {
                    TheObject.transform.SetParent(OriginalParent);
                }
            }

        }

        private void OnTriggerEnter(Collider collision)
        {
            HandleTriggerTouch(collision);
        }
        private void OnTriggerStay(Collider collision)
        {
            HandleTriggerTouch(collision);
        }
        private void OnTriggerExit(Collider collision)
        {
            if (!collision.gameObject.TryGetComponent(out HapticFingerTrigger trigger)) return;

            SetFingerState(trigger.fingertype, trigger.handType == HapticFingerTrigger.HandType.Right, false);
        }

        // Identifies which finger/hand touched via the HapticFingerTrigger on the raw
        // tracked hand's fingertip/palm colliders (added by HexRManager's Auto Setup),
        // instead of matching ghost-rig transform names.
        private void HandleTriggerTouch(Collider collision)
        {
            if (!collision.gameObject.TryGetComponent(out HapticFingerTrigger trigger)) return;

            bool isRight = trigger.handType == HapticFingerTrigger.HandType.Right;

            bool isGrabAnchor = (trigger.fingertype == HapticFingerTrigger.FingerType.Thumb && TypeOfGrab == Options.PinchGrab)
                || (trigger.fingertype == HapticFingerTrigger.FingerType.Palm && TypeOfGrab == Options.PalmGrab);

            if (isGrabAnchor && !isGrab)
            {
                GameObject handParent = isRight ? RHandParent : LHandParent;
                Vector3 contactPoint = collision.ClosestPoint(transform.position);
                handParent.transform.position = contactPoint;
                handParent.transform.parent = collision.transform;
            }

            SetFingerState(trigger.fingertype, isRight, true);
        }

        private void SetFingerState(HapticFingerTrigger.FingerType finger, bool isRight, bool state)
        {
            switch (finger)
            {
                case HapticFingerTrigger.FingerType.Thumb:
                    if (isRight) RThumb = state; else LThumb = state;
                    break;
                case HapticFingerTrigger.FingerType.Index:
                    if (isRight) RIndex = state; else LIndex = state;
                    break;
                case HapticFingerTrigger.FingerType.Middle:
                    if (isRight) RMiddle = state; else LMiddle = state;
                    break;
                case HapticFingerTrigger.FingerType.Ring:
                    if (isRight) RRing = state; else LRing = state;
                    break;
                case HapticFingerTrigger.FingerType.Little:
                    if (isRight) RLittle = state; else LLittle = state;
                    break;
                case HapticFingerTrigger.FingerType.Palm:
                    if (isRight) RPalm = state; else LPalm = state;
                    break;
            }
        }

        private void IsGrab(GameObject HandParent, FingerUseTracking fingerUseTracking, PressureTrackerMain ThePressureTracker, bool IsLeft)
        {
            ThePressureTracker?.HandGrabbingCheck(true); // To take note which hand left or right is grabbing
            TheObject.transform.SetParent(HandParent.transform); // move parent to hand so object sticks to hand

            #region Rigidbody Settings
            objectRigidbody.isKinematic = true;
            objectRigidbody.useGravity = false;
            objectRigidbody.interpolation = RigidbodyInterpolation.None;
            #endregion

            //Trigger Events
            if (isGrab && InvokeEventReady)
            {
                OnGrab?.Invoke();
                InvokeEventReady = false;
            }

            grabbingTracker = ThePressureTracker;
            TriggerHaptics(ThePressureTracker, IsLeft);

            if (resetGrab == null)
            {
                resetGrab = StartCoroutine(ResetGrab(fingerUseTracking, ThePressureTracker));
            }
        }
        private void NotGrab(PressureTrackerMain ThePressureTracker)
        {
            TheObject.transform.SetParent(OriginalParent);
            ThePressureTracker?.HandGrabbingCheck(false); // change grab state back to false

            #region Rigidbody Settings
            objectRigidbody.isKinematic = false;
            if (Gravity == Option.On) { objectRigidbody.useGravity = true; }
            objectRigidbody.interpolation = RigidbodyInterpolation.Extrapolate;
            #endregion

            if (!InvokeEventReady)
            {
                OnRelease?.Invoke();
                InvokeEventReady = true;
                RemoveHaptics(ThePressureTracker);
            }
        }
        private void TriggerHaptics(PressureTrackerMain pressureTrackerMain, bool IsLeft)
        {
            if (ReadyToActivateGrab)
            {
                ReadyToActivateGrab = false;
                if (HapticStrength != 0)
                {
                    byte[][] ClutchState = new byte[0][]; // Start with an empty array

                    if (IsLeft) // Left hand hexr trigger
                    {
                        // Check and update left hand fingers directly
                        bool[] fingerStates = { LThumb, LIndex, LMiddle, LRing, LLittle, LPalm };

                        for (int i = 0; i < fingerStates.Length; i++)
                        {
                            switch (i)
                            {
                                case 0: // Thumb
                                    UpdateHapticState(ref LThumbHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 1: // Index
                                    UpdateHapticState(ref LIndexHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 2: // Middle
                                    UpdateHapticState(ref LMiddleHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 3: // Ring
                                    UpdateHapticState(ref LRingHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 4: // Little
                                    UpdateHapticState(ref LLittleHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 5: // Palm
                                    UpdateHapticState(ref LPalmHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                            }
                        }
                        if (ClutchState.Length > 0)
                        {
                            SendClutchStates(pressureTrackerMain, ClutchState);
                        }
                    }
                    else
                    {
                        // Check and update right hand fingers directly
                        bool[] fingerStates = { RThumb, RIndex, RMiddle, RRing, RLittle, RPalm };

                        for (int i = 0; i < fingerStates.Length; i++)
                        {
                            switch (i)
                            {
                                case 0: // Thumb
                                    UpdateHapticState(ref RThumbHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 1: // Index
                                    UpdateHapticState(ref RIndexHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 2: // Middle
                                    UpdateHapticState(ref RMiddleHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 3: // Ring
                                    UpdateHapticState(ref RRingHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 4: // Little
                                    UpdateHapticState(ref RLittleHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                                case 5: // Palm
                                    UpdateHapticState(ref RPalmHaptics, fingerStates[i], ref ClutchState, i);
                                    break;
                            }
                        }
                        // Send haptics data if there are any clutch states
                        if (ClutchState.Length > 0)
                        {
                            SendClutchStates(pressureTrackerMain, ClutchState);
                        }
                    } // Right hand hexr trigger

                }
                ReadyToActivateGrab = true;
            }

        }
        private void RemoveHaptics(PressureTrackerMain pressureTrackerMain)
        {
            if (HapticStrength == 0) return;

            byte[][] ClutchState = new byte[][] { new byte[] { 0, 2 }, new byte[] { 1, 2 }, new byte[] { 2, 2 }, new byte[] { 3, 2 }, new byte[] { 4, 2 }, new byte[] { 5, 2 } };
            SendClutchStates(pressureTrackerMain, ClutchState);
            // Reset all haptic states
            LThumbHaptics = LIndexHaptics = LMiddleHaptics = LRingHaptics = LLittleHaptics = LPalmHaptics = false;
            RThumbHaptics = RIndexHaptics = RMiddleHaptics = RRingHaptics = RLittleHaptics = RPalmHaptics = false;
        }

        // Each clutch state is { channel, 0 = press | 2 = release }, channels in Haptics.Finger
        // order. Until 0.9.4 these went out through the deprecated ApplyHaptics with
        // HapticStrength as raw kPa -- which the glove ignores below 15, so the default of 10
        // was never felt -- to a glove handler looked up on the Pressure Controller, which never
        // has one, so every grab threw. Now it is the current pressure command, on the same
        // 10-60 scale as Interactable Haptics, to the handler for that hand.
        private void SendClutchStates(PressureTrackerMain pressureTrackerMain, byte[][] clutchStates)
        {
            HaptGloveHandler gloveHandler = pressureTrackerMain != null ? pressureTrackerMain.ResolveGloveHandler() : null;
            if (gloveHandler == null || gloveHandler.haptics == null)
            {
                return; // no glove for this hand: nothing to feel, and nothing to throw into the grab
            }

            float intensity = PressureTrackerMain.StrengthToIntensity(HapticStrength);
            List<byte> frame = new List<byte>();
            foreach (byte[] clutch in clutchStates)
            {
                frame.AddRange(gloveHandler.haptics.HEXRPressure((Haptics.Finger)clutch[0], clutch[1] == 0, intensity, 1f));
            }
            gloveHandler.BTSend(frame.ToArray());
        }

        IEnumerator ResetGrab(FingerUseTracking fingerUseTracking, PressureTrackerMain ThePressureTracker)
        {
            // One of these per grab. IsGrab runs every frame of a grab and used to start a new
            // copy each time, so a held object piled up one coroutine for every frame it was held.
            yield return new WaitForSeconds(0.5f);

            // Then once a frame. Update sets isGrab again for as long as the grab holds, so
            // finding it still cleared here means the fingers have let go on their own.
            // Without a FingerUseTracking on the hand there is no open-hand signal, and
            // the release comes from the grab interactor instead.
            while (isGrab)
            {
                if (fingerUseTracking != null && fingerUseTracking.isHandOpen())
                {
                    NotGrab(ThePressureTracker);
                    break;
                }

                isGrab = false;
                yield return null;
            }

            resetGrab = null;
        }
        private void OnValidate()
        {
            // Snap HapticStrength to the nearest increment of 10
            HapticStrength = Mathf.Round(HapticStrength / 10) * 10;
        }

        private void SetUpBool()
        {
            RThumb = false; LThumb = false;
            RIndex = false; LIndex = false;
            RMiddle = false; LMiddle = false;
            RRing = false; LRing = false;
            RLittle = false; LLittle = false;
            RThumbHaptics = false; LThumbHaptics = false;
            RIndexHaptics = false; LIndexHaptics = false;
            RMiddleHaptics = false; LMiddleHaptics = false;
            RRingHaptics = false; LRingHaptics = false;
            RLittleHaptics = false; LLittleHaptics = false;
        }

        #region Helper functions
        private void UpdateHapticState(ref bool hapticState, bool fingerState, ref byte[][] clutchState, int fingerIndex)
        {
            if (fingerState && !hapticState) // Finger activated + no haptic
            {
                hapticState = true;
                AddToClutchState(ref clutchState, fingerIndex, 0);
            }
            else if (!fingerState && hapticState) // Finger deactivated + haptic active
            {
                hapticState = false;
                AddToClutchState(ref clutchState, fingerIndex, 2);
            }
        }

        private void AddToClutchState(ref byte[][] clutchState, int fingerIndex, byte state)
        {
            Array.Resize(ref clutchState, clutchState.Length + 1);
            clutchState[clutchState.Length - 1] = new byte[] { (byte)fingerIndex, state };
        }

        #endregion
    }
}
