using System;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Works out which hand a Transform belongs to, for backends whose SDK cannot say.
    ///
    /// Both adapters share this so there is one fallback rule rather than one per backend. It is a
    /// fallback: when an SDK answers handedness directly -- XRI's <c>IXRInteractor.handedness</c>
    /// does -- the adapter should use that and never come here.
    ///
    /// Two strategies, in order of how much they can be trusted:
    ///
    /// <list type="number">
    /// <item>ancestry under the rig's own hand roots, which HexR knows independently of any SDK;</item>
    /// <item>the object's name containing "Left"/"Right", which is what the loose project copies of
    /// this code did and is kept only because it still rescues rigs the first strategy misses --
    /// interactors parented outside the HexR hand, for instance.</item>
    /// </list>
    /// </summary>
    public static class HexRHandSideResolver
    {
        // Resolving the hand roots means walking the manager and two components, so the answer is
        // cached. It is invalidated by frame rather than held forever: the Pressure Controllers ride
        // the persistent rig while scenes come and go beneath them, so a root that was right last
        // scene can be destroyed by this one.
        private static Transform cachedLeftRoot;
        private static Transform cachedRightRoot;
        private static int cachedFrame = -1;

        /// <summary>
        /// Which hand <paramref name="t"/> sits under, or <see cref="HexRHandSide.Unknown"/>.
        /// </summary>
        public static HexRHandSide FromTransform(Transform t)
        {
            if (t == null)
            {
                return HexRHandSide.Unknown;
            }

            RefreshRoots();

            if (cachedLeftRoot != null && t.IsChildOf(cachedLeftRoot))
            {
                return HexRHandSide.Left;
            }

            if (cachedRightRoot != null && t.IsChildOf(cachedRightRoot))
            {
                return HexRHandSide.Right;
            }

            return FromName(t);
        }

        /// <summary>
        /// The name walk on its own, for callers that have already ruled out ancestry.
        /// </summary>
        public static HexRHandSide FromName(Transform t)
        {
            while (t != null)
            {
                string n = t.name;

                if (n.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return HexRHandSide.Left;
                }

                if (n.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return HexRHandSide.Right;
                }

                t = t.parent;
            }

            return HexRHandSide.Unknown;
        }

        /// <summary>
        /// Drop the cached hand roots. Only needed if a rig is rebuilt within a single frame.
        /// </summary>
        public static void Invalidate()
        {
            cachedFrame = -1;
            cachedLeftRoot = null;
            cachedRightRoot = null;
        }

        private static void RefreshRoots()
        {
            if (cachedFrame == Time.frameCount)
            {
                return;
            }

            cachedFrame = Time.frameCount;

            HexRManager manager = HexRManager.Instance;
            if (manager == null)
            {
                cachedLeftRoot = null;
                cachedRightRoot = null;
                return;
            }

            cachedLeftRoot = HandRootOf(manager.leftHand);
            cachedRightRoot = HandRootOf(manager.rightHand);
        }

        // leftHand/rightHand are HaptGloveHandlers sitting on the Pressure Controllers, and the
        // tracked hand root is on the PhysicsHandTracking above them -- the same hop
        // MetaOVRHandNearSource makes to find its interactors.
        private static Transform HandRootOf(Component hand)
        {
            if (hand == null)
            {
                return null;
            }

            PhysicsHandTracking tracking = hand.GetComponentInParent<PhysicsHandTracking>();
            return tracking != null ? tracking.handRoot : null;
        }
    }
}
