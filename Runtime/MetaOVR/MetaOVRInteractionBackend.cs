using System.Collections.Generic;
using System.Text;
using Oculus.Interaction;
using UnityEngine;

namespace HexR.MetaOVR
{
    /// <summary>
    /// Drives <see cref="HexRInteractableHaptics"/> from the Meta Interaction SDK.
    ///
    /// The mirror image of <c>OpenXRInteractionBackend</c>. Registered by
    /// <see cref="MetaOVRBackendSetup"/>, and excluded from the build entirely when the Meta SDK
    /// isn't installed, because this whole assembly is gated on <c>HEXR_META_OVR</c>.
    ///
    /// Binds to <see cref="IPointable"/> directly rather than requiring a
    /// <see cref="PointableUnityEventWrapper"/>, so authoring matches the OpenXR side: drop the one
    /// component on the prop and it works. One interface covers everything worth listening to --
    /// <c>Grabbable</c> and <c>PointableElement</c> implement it, and so does every
    /// <c>PointerInteractable</c> subclass (<c>HandGrabInteractable</c>, <c>PokeInteractable</c>,
    /// <c>RayInteractable</c>). An existing wrapper is picked up too, so scenes already authored
    /// that way keep working.
    /// </summary>
    internal sealed class MetaOVRInteractionBackend : IHexRInteractionBackend
    {
        public HexRManager.Options Framework { get { return HexRManager.Options.MetaOVR; } }

        public string DisplayName { get { return "Meta Interaction SDK"; } }

        public bool CanBind(GameObject target)
        {
            if (target == null)
            {
                return false;
            }

            List<IPointable> pointables;
            List<PointableUnityEventWrapper> wrappers;
            Find(target, out pointables, out wrappers);
            return pointables.Count > 0 || wrappers.Count > 0;
        }

        public IHexRInteractionBinding Bind(GameObject target, IHexRInteractionSink sink)
        {
            if (target == null || sink == null)
            {
                return HexRInteractionBackends.Unbound;
            }

            List<IPointable> pointables;
            List<PointableUnityEventWrapper> wrappers;
            Find(target, out pointables, out wrappers);

            return pointables.Count == 0 && wrappers.Count == 0
                ? HexRInteractionBackends.Unbound
                : new MetaOVRInteractionBinding(pointables, wrappers, sink);
        }

        // Children matter here in a way they do not on the OpenXR side: the standard Meta authoring
        // puts Grabbable on the prop root and HandGrabInteractable on a child, and both are worth
        // hearing from.
        private static void Find(GameObject target, out List<IPointable> pointables,
                                 out List<PointableUnityEventWrapper> wrappers)
        {
            pointables = new List<IPointable>();
            wrappers = new List<PointableUnityEventWrapper>();

            MonoBehaviour[] all = target.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (MonoBehaviour mb in all)
            {
                if (mb == null)
                {
                    continue;
                }

                PointableUnityEventWrapper wrapper = mb as PointableUnityEventWrapper;
                if (wrapper != null)
                {
                    // A wrapper can legally point at an IPointable that is not on this object. That
                    // is unusual enough to be intentional, so it is honoured rather than filtered.
                    wrappers.Add(wrapper);
                    continue;
                }

                IPointable pointable = mb as IPointable;
                if (pointable != null)
                {
                    pointables.Add(pointable);
                }
            }
        }
    }

    internal sealed class MetaOVRInteractionBinding : IHexRInteractionBinding
    {
        private struct State
        {
            public bool hovering;
            public bool selecting;
        }

        private readonly List<IPointable> pointables;
        private readonly List<PointableUnityEventWrapper> wrappers;
        private readonly IHexRInteractionSink sink;

        private readonly Dictionary<int, State> states = new Dictionary<int, State>();

        // The sink keys on object identity, and PointerEvent.Identifier is an int -- so box each
        // identifier exactly once and reuse it, rather than allocating a fresh box per event.
        private readonly Dictionary<int, object> keys = new Dictionary<int, object>();

        private bool disposed;

        internal MetaOVRInteractionBinding(List<IPointable> pointables,
                                           List<PointableUnityEventWrapper> wrappers,
                                           IHexRInteractionSink sink)
        {
            this.pointables = pointables;
            this.wrappers = wrappers;
            this.sink = sink;

            foreach (IPointable pointable in pointables)
            {
                pointable.WhenPointerEventRaised += OnPointerEvent;
            }

            foreach (PointableUnityEventWrapper wrapper in wrappers)
            {
                if (wrapper == null)
                {
                    continue;
                }

                wrapper.WhenHover.AddListener(OnPointerEvent);
                wrapper.WhenUnhover.AddListener(OnPointerEvent);
                wrapper.WhenSelect.AddListener(OnPointerEvent);
                wrapper.WhenUnselect.AddListener(OnPointerEvent);
                wrapper.WhenCancel.AddListener(OnPointerEvent);
            }
        }

        public bool IsBound { get { return !disposed && (pointables.Count > 0 || wrappers.Count > 0); } }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();

            foreach (IPointable pointable in pointables)
            {
                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(pointable.GetType().Name);
            }

            foreach (PointableUnityEventWrapper wrapper in wrappers)
            {
                if (wrapper == null)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                sb.Append("PointableUnityEventWrapper");
            }

            return sb.Length > 0 ? sb.ToString() : "nothing";
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            foreach (IPointable pointable in pointables)
            {
                pointable.WhenPointerEventRaised -= OnPointerEvent;
            }

            foreach (PointableUnityEventWrapper wrapper in wrappers)
            {
                if (wrapper == null)
                {
                    continue;
                }

                wrapper.WhenHover.RemoveListener(OnPointerEvent);
                wrapper.WhenUnhover.RemoveListener(OnPointerEvent);
                wrapper.WhenSelect.RemoveListener(OnPointerEvent);
                wrapper.WhenUnselect.RemoveListener(OnPointerEvent);
                wrapper.WhenCancel.RemoveListener(OnPointerEvent);
            }

            states.Clear();
            keys.Clear();
        }

        private void OnPointerEvent(PointerEvent evt)
        {
            if (disposed)
            {
                return;
            }

            State state;
            if (!states.TryGetValue(evt.Identifier, out state))
            {
                state = default(State);
            }

            switch (evt.Type)
            {
                case PointerEventType.Hover:
                    state.hovering = true;
                    break;

                case PointerEventType.Unhover:
                    state.hovering = false;
                    break;

                case PointerEventType.Select:
                    state.selecting = true;
                    break;

                case PointerEventType.Unselect:
                    state.selecting = false;
                    break;

                case PointerEventType.Cancel:
                    states.Remove(evt.Identifier);
                    sink.ClearInteractor(KeyFor(evt.Identifier));
                    return;

                default:
                    // Move: position changed, engagement did not.
                    return;
            }

            if (!state.hovering && !state.selecting)
            {
                states.Remove(evt.Identifier);
            }
            else
            {
                states[evt.Identifier] = state;
            }

            sink.SetInteractorState(KeyFor(evt.Identifier), MetaOVRHandIndex.Resolve(evt),
                                    state.hovering, state.selecting);
        }

        private object KeyFor(int identifier)
        {
            object key;
            if (!keys.TryGetValue(identifier, out key))
            {
                key = identifier;           // boxed once, reused thereafter
                keys[identifier] = key;
            }

            return key;
        }
    }

    /// <summary>
    /// Maps a <see cref="PointerEvent.Identifier"/> back to a hand.
    ///
    /// The Meta Interaction SDK has no handedness concept -- an identifier is an opaque per-instance
    /// int -- so the answer has to be built from the rig. Two sources, both of which the package
    /// already relies on elsewhere: the interactors wired onto each Pressure Controller (which
    /// <see cref="MetaOVRHandNearSource"/> reads for grab/poke gating), and anything interactor-like
    /// under each hand's tracked root.
    ///
    /// Shared statically rather than per binding, because the map is per rig, not per prop.
    /// </summary>
    internal static class MetaOVRHandIndex
    {
        private static readonly Dictionary<int, HexRHandSide> byIdentifier = new Dictionary<int, HexRHandSide>();
        private static int rebuiltFrame = -1;

        internal static HexRHandSide Resolve(PointerEvent evt)
        {
            HexRHandSide side;
            if (byIdentifier.TryGetValue(evt.Identifier, out side))
            {
                return side;
            }

            // Identifiers are handed out in Interactor.Awake, so a rig that spawned after the last
            // rebuild is the usual reason for a miss. Rebuild at most once a frame: a genuinely
            // unknown pointer (a ray from a UI panel, say) must not cost a scene scan per event.
            if (rebuiltFrame != Time.frameCount)
            {
                rebuiltFrame = Time.frameCount;
                Rebuild();

                if (byIdentifier.TryGetValue(evt.Identifier, out side))
                {
                    return side;
                }
            }

            // Last resort. Interactor.Start assigns `_data = this` when nothing else set it, so Data
            // is usually the interactor itself -- but the SDK gives it no formal contract, which is
            // why this is the fallback rather than the mechanism.
            Component component = evt.Data as Component;
            return component != null ? HexRHandSideResolver.FromTransform(component.transform)
                                     : HexRHandSide.Unknown;
        }

        private static void Rebuild()
        {
            byIdentifier.Clear();

            PressureTrackerMain[] trackers = HexRCompat.FindAll<PressureTrackerMain>(true);
            foreach (PressureTrackerMain tracker in trackers)
            {
                if (tracker == null)
                {
                    continue;
                }

                HexRHandSide side = tracker.handType == PressureTrackerMain.HandType.Left
                    ? HexRHandSide.Left
                    : HexRHandSide.Right;

                // The two the scene wires explicitly, and that Auto Setup fills in.
                Add(tracker.handGrabInteractor, side);
                Add(tracker.pokeInteractor, side);

                // Everything else on the hand -- distance/touch grab, ray, and any custom
                // interactor the two fields above do not name.
                PhysicsHandTracking tracking = tracker.GetComponentInParent<PhysicsHandTracking>();
                Transform handRoot = tracking != null ? tracking.handRoot : null;
                if (handRoot == null)
                {
                    continue;
                }

                MonoBehaviour[] candidates = handRoot.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (MonoBehaviour candidate in candidates)
                {
                    Add(candidate, side);
                }
            }
        }

        private static void Add(MonoBehaviour candidate, HexRHandSide side)
        {
            IInteractorView view = candidate as IInteractorView;
            if (view != null)
            {
                byIdentifier[view.Identifier] = side;
            }
        }
    }
}
