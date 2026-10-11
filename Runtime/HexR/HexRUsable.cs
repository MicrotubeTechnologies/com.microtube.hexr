using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using HexR;
using UnityEngine.Events;

public class HexRUsable : MonoBehaviour
{
    [Tooltip("Select the finger that is use to trigger an event")]
    public enum Options { Needed, NotNeeded }

    public Options Thumb, IndexFinger, MiddleFinger, RingFinger, LittleFinger;

    [Tooltip("0 is when finger is fully curled, 1 is when finger is fully extended")]
    [Range(0f, 1f)] // Creates a slider from 0 to 100
    public float UseThreshold = 0.3f;

    public UnityEvent WhenUseTriggerEvents, WhenStopUsingTriggerEvents;

    private FingerUseTracking RfingerUseTracking, LfingeruseTracking, Currentfingerusetracking;
    private bool beinguse = false;

    // Start is called before the first frame update
    void Start()
    {
        FingerUseTrackingSetUp();
        Currentfingerusetracking = null;
    }

    // Update is called once per frame
    void Update()
    {
        if (Currentfingerusetracking != null)
        {
            if (beinguse == false)
            {
                UseChecker();
            }
            else
            {
                NotUseChecker();
            }
        }

    }
    private void OnTriggerEnter(Collider other)
    {
        TrackHandOf(other);
    }
    private void OnTriggerStay(Collider other)
    {
        TrackHandOf(other);
    }

    // Which hand touched, from the HapticFingerTrigger Auto Setup puts on every fingertip and
    // palm collider -- the way SpecialHaptics and HexRGrabbable already tell. Until 0.9.4 this
    // read "L_"/"R_" from the collider's name, which only Unity XR Hands' joints carry; Meta's
    // are named XRHand_..., so on Meta no touch was ever matched to a hand. The name test is
    // kept as a fallback for colliders added by hand without a HapticFingerTrigger.
    private void TrackHandOf(Collider other)
    {
        if (other.TryGetComponent(out HapticFingerTrigger trigger))
        {
            Currentfingerusetracking = trigger.handType == HapticFingerTrigger.HandType.Right
                ? RfingerUseTracking
                : LfingeruseTracking;
            return;
        }

        if (other.name.Contains("R_"))
        {
            Currentfingerusetracking = RfingerUseTracking;
        }
        if (other.name.Contains("L_"))
        {
            Currentfingerusetracking = LfingeruseTracking;
        }
    }
    public void UseChecker()
    {
        bool Check = false;
        if(Thumb == Options.Needed)
        {
            if(Currentfingerusetracking.ThumbUse < UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (IndexFinger == Options.Needed)
        {
            if (Currentfingerusetracking.IndexUse < UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (MiddleFinger == Options.Needed)
        {
            if (Currentfingerusetracking.MiddleUse < UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (RingFinger == Options.Needed)
        {
            if (Currentfingerusetracking.RingUse < UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (LittleFinger == Options.Needed)
        {
            if (Currentfingerusetracking.LittleUse < UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if(Check ==true)
        {
            beinguse = true;
            WhenUseTriggerEvents?.Invoke();
            Currentfingerusetracking = null;
        }
        // NotUseChecker used to be called here as well. Right after a use it read the tracking
        // just cleared above and threw; without a use it fired WhenStopUsingTriggerEvents every
        // frame the finger stayed open. Update calls it once the object is actually in use.
    }
    public void NotUseChecker()
    {
        bool Check = false;
        if (Thumb == Options.Needed)
        {
            if (Currentfingerusetracking.ThumbUse > UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (IndexFinger == Options.Needed)
        {
            if (Currentfingerusetracking.IndexUse > UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (MiddleFinger == Options.Needed)
        {
            if (Currentfingerusetracking.MiddleUse > UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (RingFinger == Options.Needed)
        {
            if (Currentfingerusetracking.RingUse > UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (LittleFinger == Options.Needed)
        {
            if (Currentfingerusetracking.LittleUse > UseThreshold)
            {
                Check = true;
            }
            else
            {
                Check = false;
            }
        }

        if (Check == true)
        {
            beinguse = false;
            WhenStopUsingTriggerEvents?.Invoke();
            Currentfingerusetracking = null;
        }
    }
    // Via HexRManager.Instance rather than GameObject.Find, for the same reason SpecialHaptics
    // does: every scene instantiates its own HexR Main prefab, whose duplicate is not actually
    // gone until the end of the load frame, so a name lookup here can latch onto the copy that
    // is about to be destroyed.
    private void FingerUseTrackingSetUp()
    {
        HexRManager manager = HexRManager.Instance;
        if (manager == null)
        {
            Debug.Log("HexRManager is not in the scene");
            return;
        }

        if (manager.rightHand != null) { RfingerUseTracking = manager.rightHand.GetComponent<FingerUseTracking>(); }
        else { Debug.Log("Right hand is not found"); }

        if (manager.leftHand != null) { LfingeruseTracking = manager.leftHand.GetComponent<FingerUseTracking>(); }
        else { Debug.Log("Left hand is not found"); }
    }
    
}
