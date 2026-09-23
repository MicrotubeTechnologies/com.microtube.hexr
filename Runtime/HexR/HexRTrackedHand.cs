using UnityEngine;

namespace HexR
{
    // Which tracked hand a HexR hand belongs to, and where its joints are.
    //
    // HexR doesn't draw or drive a hand of its own. The fingertip and palm trigger colliders
    // that fire haptics sit directly on the XR SDK's tracked hand -- Meta's XRHand_* skeleton
    // on Quest, Unity XR Hands' L_*/R_* skeleton on PICO and other OpenXR runtimes -- and this
    // component is how the rest of the package finds that hand: Auto Setup and the scene-load
    // rebind place the colliders through the resolvers below, the validator checks them, and
    // the backends search under handRoot for their grab/poke interactors.
    //
    // This used to be PhysicsHandTracking, which also drove a physics "ghost" copy of the hand
    // from the tracked one. The ghost is gone on both backends, so what remained was renamed
    // for what it does. The script GUID is unchanged, so scenes and prefabs that carried a
    // PhysicsHandTracking keep this component and its handType/handRoot values.
    [DisallowMultipleComponent]
    public class HexRTrackedHand : MonoBehaviour
    {
        public enum HandType
        {
            Left,
            Right
        };

        public HandType handType;

        [Tooltip("The tracked hand to follow: Meta's OpenXRLeftHand/OpenXRRightHand, or XR Hands' L_Wrist/R_Wrist. HexR > Auto Setup Scene fills this in, and HexRManager re-points it after every scene load.")]
        public Transform handRoot;

        // Two OpenXR hand rigs turn up in practice and they name joints differently: Unity's XR
        // Hands rig prefixes with handedness ("L_ThumbMetacarpal"), while Meta's OpenXR hand --
        // which com.meta.xr.sdk.interaction v201+ switches every OVR rig onto, see
        // HandVisual.Awake -- uses a handedness-free "XRHand_ThumbMetacarpal" nested one level
        // down under XRHand_Wrist. Both are tried, with a recursive search so handRoot can be
        // either the mesh root (OpenXRLeftHand) or the wrist itself.
        private const string MetaOpenXRPrefix = "XRHand_";

        private string HandPrefix
        {
            get { return handType == HandType.Left ? "L_" : "R_"; }
        }

        // Resolves the fingertip joint on the tracked hand, for placing a collider +
        // HapticFingerTrigger. Needs nothing but handRoot, so it's safe from editor code.
        public Transform ResolveRawFingerJoint(HapticFingerTrigger.FingerType finger)
        {
            if (handRoot == null || finger == HapticFingerTrigger.FingerType.Palm) return null;

            Transform metacarpal = FindJointByName(HandPrefix + finger + "Metacarpal")
                ?? FindJointByName(MetaOpenXRPrefix + finger + "Metacarpal");
            if (metacarpal == null) return null;

            // Below the metacarpal it's a plain parent-child bone chain, so walk down to the
            // tip: 3 steps for the thumb's 4-joint chain, 4 for the other fingers' 5. Both rigs
            // share that shape.
            return finger == HapticFingerTrigger.FingerType.Thumb
                ? WalkChain(metacarpal, 0, 0, 0)
                : WalkChain(metacarpal, 0, 0, 0, 0);
        }

        public Transform ResolveRawPalmJoint()
        {
            if (handRoot == null) return null;

            return FindJointByName(HandPrefix + "Palm")
                ?? FindJointByName(MetaOpenXRPrefix + "Palm");
        }

        private Transform FindJointByName(string jointName)
        {
            if (handRoot == null) return null;
            if (handRoot.name == jointName) return handRoot;

            Transform direct = handRoot.Find(jointName);
            if (direct != null) return direct;

            GameObject nested = FindChildRecursive(handRoot.gameObject, jointName);
            return nested != null ? nested.transform : null;
        }

        public static GameObject FindChildRecursive(GameObject parent, string childName)
        {
            foreach (Transform child in parent.transform)
            {
                if (child.gameObject.name == childName)
                {
                    return child.gameObject;
                }
                GameObject result = FindChildRecursive(child.gameObject, childName);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        // Returns the deepest transform actually reached rather than null on a short chain --
        // landing on a slightly-shallower-than-expected joint is a better fallback than no
        // joint at all.
        private static Transform WalkChain(Transform start, params int[] childSteps)
        {
            Transform current = start;
            foreach (int step in childSteps)
            {
                if (current == null || step >= current.childCount) break;
                current = current.GetChild(step);
            }
            return current;
        }
    }
}
