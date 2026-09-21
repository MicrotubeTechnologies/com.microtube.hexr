using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace HexR.OpenXR
{
    /// <summary>
    /// Drives <see cref="HexRInteractableHaptics"/> from the XR Interaction Toolkit.
    ///
    /// The mirror image of <c>MetaOVRInteractionBackend</c>, and the reason the haptics component
    /// itself carries no XRI reference. Registered by <see cref="OpenXRBackendSetup"/>; excluded
    /// from the build entirely when XRI isn't installed, because this whole assembly is gated on
    /// <c>HEXR_XRI</c>.
    /// </summary>
    internal sealed class OpenXRInteractionBackend : IHexRInteractionBackend
    {
        public HexRManager.Options Framework { get { return HexRManager.Options.OpenXR; } }

        public string DisplayName { get { return "OpenXR (XR Interaction Toolkit)"; } }

        public bool CanBind(GameObject target)
        {
            return target != null && Find(target).Length > 0;
        }

        public IHexRInteractionBinding Bind(GameObject target, IHexRInteractionSink sink)
        {
            if (target == null || sink == null)
            {
                return HexRInteractionBackends.Unbound;
            }

            XRBaseInteractable[] interactables = Find(target);
            return interactables.Length == 0
                ? HexRInteractionBackends.Unbound
                : new OpenXRInteractionBinding(interactables, sink);
        }

        // Includes the object itself. Children matter because the common XRI authoring puts the
        // interactable on the prop root but nothing stops a project nesting it, and binding the
        // whole subtree costs nothing when there is only one.
        private static XRBaseInteractable[] Find(GameObject target)
        {
            return target.GetComponentsInChildren<XRBaseInteractable>(true);
        }
    }

    internal sealed class OpenXRInteractionBinding : IHexRInteractionBinding
    {
        private struct State
        {
            public bool hovering;
            public bool selecting;
        }

        private readonly XRBaseInteractable[] interactables;
        private readonly IHexRInteractionSink sink;

        // XRI reports one edge at a time, but the sink wants the whole state, so the missing half
        // has to be remembered here.
        private readonly Dictionary<IXRInteractor, State> states = new Dictionary<IXRInteractor, State>();

        private bool disposed;

        internal OpenXRInteractionBinding(XRBaseInteractable[] interactables, IHexRInteractionSink sink)
        {
            this.interactables = interactables;
            this.sink = sink;

            foreach (XRBaseInteractable interactable in interactables)
            {
                if (interactable == null)
                {
                    continue;
                }

                interactable.hoverEntered.AddListener(OnHoverEntered);
                interactable.hoverExited.AddListener(OnHoverExited);
                interactable.selectEntered.AddListener(OnSelectEntered);
                interactable.selectExited.AddListener(OnSelectExited);
            }
        }

        public bool IsBound { get { return !disposed && interactables.Length > 0; } }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            foreach (XRBaseInteractable interactable in interactables)
            {
                if (interactable == null)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(interactable.GetType().Name);
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

            foreach (XRBaseInteractable interactable in interactables)
            {
                if (interactable == null)
                {
                    continue;
                }

                interactable.hoverEntered.RemoveListener(OnHoverEntered);
                interactable.hoverExited.RemoveListener(OnHoverExited);
                interactable.selectEntered.RemoveListener(OnSelectEntered);
                interactable.selectExited.RemoveListener(OnSelectExited);
            }

            states.Clear();
        }

        private void OnHoverEntered(HoverEnterEventArgs args) { Set(args.interactorObject, true, null); }

        private void OnHoverExited(HoverExitEventArgs args) { Set(args.interactorObject, false, null); }

        private void OnSelectEntered(SelectEnterEventArgs args) { Set(args.interactorObject, null, true); }

        private void OnSelectExited(SelectExitEventArgs args) { Set(args.interactorObject, null, false); }

        private void Set(IXRInteractor interactor, bool? hovering, bool? selecting)
        {
            if (interactor == null || disposed)
            {
                return;
            }

            State state;
            if (!states.TryGetValue(interactor, out state))
            {
                state = default(State);
            }

            if (hovering.HasValue)
            {
                state.hovering = hovering.Value;
            }

            if (selecting.HasValue)
            {
                state.selecting = selecting.Value;
            }

            if (!state.hovering && !state.selecting)
            {
                states.Remove(interactor);
            }
            else
            {
                states[interactor] = state;
            }

            sink.SetInteractorState(interactor, Handedness(interactor), state.hovering, state.selecting);
        }

        /// <summary>
        /// XRI knows which hand an interactor is, so ask it. The shared resolver is only a fallback
        /// for an interactor left at <see cref="InteractorHandedness.None"/> -- which a hand rig
        /// should not be, but a custom or simulated interactor can be.
        /// </summary>
        private static HexRHandSide Handedness(IXRInteractor interactor)
        {
            switch (interactor.handedness)
            {
                case InteractorHandedness.Left:
                    return HexRHandSide.Left;

                case InteractorHandedness.Right:
                    return HexRHandSide.Right;

                default:
                    return HexRHandSideResolver.FromTransform(interactor.transform);
            }
        }
    }
}
