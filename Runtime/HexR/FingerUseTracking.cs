using UnityEngine;
using TMPro;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace HexR
{
    // How curled each finger is, from 0 (curled) to 1 (extended): the distance from each
    // fingertip to its knuckle, normalised against the widest and narrowest it has seen.
    // HexRUsable, HexRGrabbable's open-hand release, SpecialHaptics' Hand Squeeze and
    // HexRInteractableHaptics' grip-scaled pressure all read it.
    //
    // The joints are the tracked hand's, found through the HexRTrackedHand on the same object.
    // Any reference left empty is filled in at runtime -- and filled in again once a scene load
    // destroys the hand it pointed at -- so nothing has to be wired per scene. Assign one by
    // hand only to override it.
    [RequireComponent(typeof(HexRTrackedHand))]
    public class FingerUseTracking : MonoBehaviour
    {
        public GameObject IndexTip, IndexKnuckle, MiddleTip, MiddleKnuckle, RingTip, RingKnuckle, LittleTip, LittleKnuckle, ThumbTip, ThumbKnuckle;
        public TextMeshProUGUI DebugText;

        private float IndexDistance, MiddleDistance, RingDistance, LittleDistance, ThumbDistance;
        private float IndexLargest, MiddleLargest, RingLargest, LittleLargest, ThumbLargest;
        private float IndexSmallest, MiddleSmallest, RingSmallest, LittleSmallest, ThumbSmallest;

        [HideInInspector]
        public float IndexUse, MiddleUse, RingUse, LittleUse, ThumbUse;

        private HexRTrackedHand trackedHand;

        // Resolution walks the hand's hierarchy, so while the hand is missing (between scenes,
        // or before tracking starts) it is retried a few times a second rather than every frame.
        private const float ResolveInterval = 0.25f;
        private float nextResolveTime;

        void Start()
        {
            trackedHand = GetComponent<HexRTrackedHand>();

            // Initialize smallest values to a very large number
            IndexSmallest = MiddleSmallest = RingSmallest = LittleSmallest = ThumbSmallest = float.MaxValue;

            // Initialize largest values to a very small number
            IndexLargest = MiddleLargest = RingLargest = LittleLargest = ThumbLargest = float.MinValue;
        }

        void Update()
        {
            if (!HasAllJoints())
            {
                if (Time.unscaledTime < nextResolveTime)
                {
                    return;
                }
                nextResolveTime = Time.unscaledTime + ResolveInterval;
                FillFromTrackedHand(false);
                if (!HasAllJoints())
                {
                    return;
                }
            }

            // Calculate distances
            IndexDistance = Vector3.Distance(IndexTip.transform.position, IndexKnuckle.transform.position);
            MiddleDistance = Vector3.Distance(MiddleTip.transform.position, MiddleKnuckle.transform.position);
            RingDistance = Vector3.Distance(RingTip.transform.position, RingKnuckle.transform.position);
            LittleDistance = Vector3.Distance(LittleTip.transform.position, LittleKnuckle.transform.position);
            ThumbDistance = Vector3.Distance(ThumbTip.transform.position, ThumbKnuckle.transform.position);

            // Check and update max and min distances
            CheckMaxDistance();
            CheckMinDistance();

            // Calculate normalized "use" values
            CheckFingerUse();

            // Update debug text
            if(DebugText != null)
            {
                DebugText.text = $"{IndexUse:F2} | {MiddleUse:F2} | {RingUse:F2} | {LittleUse:F2} | {ThumbUse:F2}";
            }
        }

        private bool HasAllJoints()
        {
            return IndexTip != null && IndexKnuckle != null && MiddleTip != null && MiddleKnuckle != null
                && RingTip != null && RingKnuckle != null && LittleTip != null && LittleKnuckle != null
                && ThumbTip != null && ThumbKnuckle != null;
        }

        /// <summary>
        /// Fills the joint references from the tracked hand. With <paramref name="overwrite"/>
        /// false only empty (or destroyed) references are filled, so a hand-assigned one wins.
        /// Returns false when the tracked hand isn't available to resolve against.
        /// </summary>
        public bool FillFromTrackedHand(bool overwrite)
        {
            if (trackedHand == null)
            {
                trackedHand = GetComponent<HexRTrackedHand>();
            }
            if (trackedHand == null || trackedHand.handRoot == null)
            {
                return false;
            }

            Fill(ref IndexTip, ref IndexKnuckle, HapticFingerTrigger.FingerType.Index, overwrite);
            Fill(ref MiddleTip, ref MiddleKnuckle, HapticFingerTrigger.FingerType.Middle, overwrite);
            Fill(ref RingTip, ref RingKnuckle, HapticFingerTrigger.FingerType.Ring, overwrite);
            Fill(ref LittleTip, ref LittleKnuckle, HapticFingerTrigger.FingerType.Little, overwrite);
            Fill(ref ThumbTip, ref ThumbKnuckle, HapticFingerTrigger.FingerType.Thumb, overwrite);
            return true;
        }

        private void Fill(ref GameObject tip, ref GameObject knuckle, HapticFingerTrigger.FingerType finger, bool overwrite)
        {
            if (overwrite || tip == null)
            {
                Transform t = trackedHand.ResolveRawFingerJoint(finger);
                tip = t != null ? t.gameObject : null;
            }
            if (overwrite || knuckle == null)
            {
                Transform k = trackedHand.ResolveKnuckleJoint(finger);
                knuckle = k != null ? k.gameObject : null;
            }
        }

        private void CheckMaxDistance()
        {
            IndexLargest = Mathf.Max(IndexLargest, IndexDistance);
            MiddleLargest = Mathf.Max(MiddleLargest, MiddleDistance);
            RingLargest = Mathf.Max(RingLargest, RingDistance);
            LittleLargest = Mathf.Max(LittleLargest, LittleDistance);
            ThumbLargest = Mathf.Max(ThumbLargest, ThumbDistance);
        }

        private void CheckMinDistance()
        {
            IndexSmallest = Mathf.Min(IndexSmallest, IndexDistance);
            MiddleSmallest = Mathf.Min(MiddleSmallest, MiddleDistance);
            RingSmallest = Mathf.Min(RingSmallest, RingDistance);
            LittleSmallest = Mathf.Min(LittleSmallest, LittleDistance);
            ThumbSmallest = Mathf.Min(ThumbSmallest, ThumbDistance);
        }

        private void CheckFingerUse()
        {
            IndexUse = Normalize(IndexDistance, IndexSmallest, IndexLargest);
            MiddleUse = Normalize(MiddleDistance, MiddleSmallest, MiddleLargest);
            RingUse = Normalize(RingDistance, RingSmallest, RingLargest);
            LittleUse = Normalize(LittleDistance, LittleSmallest, LittleLargest);
            ThumbUse = Normalize(ThumbDistance, ThumbSmallest, ThumbLargest);
        }

        private float Normalize(float value, float min, float max)
        {
            if (Mathf.Approximately(max, min))
                return 0; // Avoid division by zero
            return (value - min) / (max - min);
        }

        public bool isHandOpen()
        {
            if(IndexUse > 0.95
                && MiddleUse > 0.95
                && RingUse > 0.95
                && ThumbUse > 0.9
                && LittleUse > 0.95)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(FingerUseTracking))]
    public class FingerUseTrackingEditorSetting : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Empty joints are found on the tracked hand at runtime, and found again after a scene load. "
                + "Assign one only to override it.", MessageType.None);

            DrawDefaultInspector();

            GUILayout.Space(10);

            if (GUILayout.Button(new GUIContent("Fill From Tracked Hand", "Resolve every joint from this hand's HexRTrackedHand.handRoot now, replacing what's assigned.")))
            {
                FingerUseTracking tracking = (FingerUseTracking)target;
                Undo.RecordObject(tracking, "Fill FingerUseTracking joints");
                if (!tracking.FillFromTrackedHand(true))
                {
                    Debug.LogWarning("[HexR] " + tracking.name + ": HexRTrackedHand.handRoot isn't assigned -- run HexR > Troubleshoot > Re-run Auto Setup first.", tracking);
                }
                EditorUtility.SetDirty(tracking);
            }
        }
    }
#endif
}
