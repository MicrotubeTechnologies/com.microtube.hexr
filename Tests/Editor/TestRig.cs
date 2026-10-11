using System.Collections.Generic;
using System.Reflection;
using HaptGlove;
using UnityEngine;

namespace HexR.Tests
{
    /// <summary>
    /// Just enough of a HexR rig, built in edit mode, for components to find their gloves: a
    /// HexRManager with a glove handler per hand (each carrying the HexRTrackedHand and
    /// FingerUseTracking the rig prefabs put there), and a Pressure Controller per hand.
    ///
    /// Nothing here talks to hardware. The handlers are left in Quest buffering mode, so a send
    /// is queued rather than written, and what each channel was last told is read back from
    /// <see cref="Haptics.GetChannelState"/>, which records every command as it is built.
    /// </summary>
    internal sealed class TestRig : System.IDisposable
    {
        public readonly HexRManager Manager;
        public readonly HaptGloveHandler Left, Right;
        public readonly PressureTrackerMain LeftTracker, RightTracker;

        private readonly List<GameObject> made = new List<GameObject>();

        public TestRig()
        {
            Manager = Make("HexR Main").AddComponent<HexRManager>();
            Left = MakeHand("Left Hand Physics", HaptGloveHandler.HandType.Left);
            Right = MakeHand("Right Hand Physics", HaptGloveHandler.HandType.Right);
            Manager.leftHand = Left;
            Manager.rightHand = Right;
            SetInstance(Manager);

            LeftTracker = MakeTracker("Left Pressure Controller", PressureTrackerMain.HandType.Left, Left);
            RightTracker = MakeTracker("Right Pressure Controller", PressureTrackerMain.HandType.Right, Right);
        }

        public GameObject Make(string name)
        {
            GameObject go = new GameObject(name);
            made.Add(go);
            return go;
        }

        /// <summary>A fingertip collider as Auto Setup leaves it, already wired to its glove.</summary>
        public HapticFingerTrigger MakeFingertip(string name, HapticFingerTrigger.HandType side,
                                                 HapticFingerTrigger.FingerType finger)
        {
            GameObject go = Make(name);
            go.AddComponent<SphereCollider>().isTrigger = true;
            HapticFingerTrigger trigger = go.AddComponent<HapticFingerTrigger>();
            trigger.handType = side;
            trigger.fingertype = finger;
            trigger.HexrLeftOrRight = side == HapticFingerTrigger.HandType.Left ? Left.gameObject : Right.gameObject;
            Private.Call(trigger, "Start");
            return trigger;
        }

        public static FingerUseTracking FingersOf(HaptGloveHandler hand)
        {
            return hand.GetComponent<FingerUseTracking>();
        }

        public static void SetFingers(FingerUseTracking use, float openness)
        {
            use.ThumbUse = use.IndexUse = use.MiddleUse = use.RingUse = use.LittleUse = openness;
        }

        public static void SetInstance(HexRManager manager)
        {
            typeof(HexRManager).GetProperty("Instance").SetValue(null, manager);
        }

        public void Dispose()
        {
            SetInstance(null);
            foreach (GameObject go in made)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            // HexRGrabbable makes these in Start.
            foreach (string name in new[] { "RightHandParent", "LeftHandParent" })
            {
                for (GameObject leftover = GameObject.Find(name); leftover != null; leftover = GameObject.Find(name))
                {
                    Object.DestroyImmediate(leftover);
                }
            }
        }

        private HaptGloveHandler MakeHand(string name, HaptGloveHandler.HandType side)
        {
            GameObject go = Make(name);
            HaptGloveHandler hand = go.AddComponent<HaptGloveHandler>();
            hand.whichHand = side;
            hand.haptics.whichHand = side.ToString();
            hand.isQuest = true;
            go.AddComponent<FingerUseTracking>();
            return hand;
        }

        private PressureTrackerMain MakeTracker(string name, PressureTrackerMain.HandType side, HaptGloveHandler hand)
        {
            PressureTrackerMain tracker = Make(name).AddComponent<PressureTrackerMain>();
            tracker.handType = side;
            tracker.gloveHandler = hand;
            return tracker;
        }
    }

    /// <summary>
    /// Calls and fields the tests reach behind the public API: Unity messages (Start, Update,
    /// OnDisable...), which edit mode never sends, and the state they leave behind.
    /// </summary>
    internal static class Private
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static object Call(object target, string method, params object[] args)
        {
            MethodInfo info = target.GetType().GetMethod(method, Any);
            try
            {
                return info.Invoke(target, args);
            }
            catch (TargetInvocationException e)
            {
                // Rethrow what the method itself threw, not reflection's wrapper.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        public static T Get<T>(object target, string field)
        {
            return (T)target.GetType().GetField(field, Any).GetValue(target);
        }

        public static void Set(object target, string field, object value)
        {
            target.GetType().GetField(field, Any).SetValue(target, value);
        }
    }
}
