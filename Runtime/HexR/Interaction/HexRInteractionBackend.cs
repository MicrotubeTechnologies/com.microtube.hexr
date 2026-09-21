using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Which hand an interaction came from.
    ///
    /// This enum is the whole abstraction boundary for handedness. A backend adapter works it out
    /// however its SDK allows -- XRI simply asks the interactor, Meta has no handedness concept at
    /// all and has to map the pointer back to a rig -- and the core never sees a Transform it would
    /// have to parse. That matters because the old name-walk ("does an ancestor contain 'Left'?")
    /// was only ever right by luck on a rig nobody had renamed.
    /// </summary>
    public enum HexRHandSide
    {
        Unknown,
        Left,
        Right,
    }

    /// <summary>
    /// Which interaction system drives a component.
    ///
    /// <see cref="Auto"/> is ordinal 0 so that anything deserialized from a scene authored before
    /// this field existed lands on it, which is also the right answer in almost every project.
    /// </summary>
    public enum HexRBackendChoice
    {
        /// <summary>Read the rig's <see cref="HexRManager.XRFramework"/>.</summary>
        Auto,
        OpenXR,
        MetaOVR,
    }

    /// <summary>
    /// What an adapter pushes interaction state into.
    ///
    /// Deliberately <i>absolute</i> state rather than edges: an adapter says "this interactor is
    /// hovering and not selecting" rather than "a hover started". Two reasons, both of which bit the
    /// edge-driven version this replaces:
    ///
    /// <list type="bullet">
    /// <item>XRI and Meta do not agree on the order of hover/select callbacks, so any logic that
    /// counts edges behaves differently on the two backends;</item>
    /// <item>the Meta adapter can legitimately hear the same event twice -- once from the
    /// <c>IPointable</c> it bound to and once from a <c>PointableUnityEventWrapper</c> pointed at
    /// that same pointable. Setting a state twice is free; counting an edge twice is a bug.</item>
    /// </list>
    /// </summary>
    public interface IHexRInteractionSink
    {
        /// <param name="interactorKey">
        /// Anything that is stable and equatable for the lifetime of one interactor. The OpenXR
        /// adapter uses the interactor object; the Meta adapter uses the boxed pointer identifier.
        /// The core only ever compares these, never inspects them.
        /// </param>
        void SetInteractorState(object interactorKey, HexRHandSide hand, bool hovering, bool selecting);

        /// <summary>Forget this interactor entirely -- it has gone away or been cancelled.</summary>
        void ClearInteractor(object interactorKey);
    }

    /// <summary>
    /// A live subscription to one object's interaction events. Disposing it unsubscribes.
    /// </summary>
    public interface IHexRInteractionBinding : IDisposable
    {
        bool IsBound { get; }

        /// <summary>What this ended up bound to, for the Inspector and for warnings. Diagnostics only.</summary>
        string Describe();
    }

    /// <summary>
    /// One interaction SDK, as the backend-neutral half of the package sees it.
    ///
    /// Implementations live in the per-backend assemblies (<c>HexR.Runtime.OpenXR</c>,
    /// <c>HexR.Runtime.MetaOVR</c>) and announce themselves through
    /// <see cref="HexRInteractionBackends.Register"/>. The dependency runs that way round on
    /// purpose: <c>HexR.Runtime</c> referencing either satellite would drag that SDK back into the
    /// core and undo the assembly split that makes the package installable without it.
    /// </summary>
    public interface IHexRInteractionBackend
    {
        /// <summary>
        /// Which <see cref="HexRManager.XRFramework"/> value this backend serves. Reusing the
        /// existing enum rather than declaring a parallel one means there is nothing to drift.
        /// </summary>
        HexRManager.Options Framework { get; }

        string DisplayName { get; }

        /// <summary>Whether this backend can find anything to listen to on <paramref name="target"/>.</summary>
        bool CanBind(GameObject target);

        /// <summary>
        /// Subscribe. Always returns a binding -- one whose <see cref="IHexRInteractionBinding.IsBound"/>
        /// is false means "nothing here for me", which is not an error.
        /// </summary>
        IHexRInteractionBinding Bind(GameObject target, IHexRInteractionSink sink);

        /// <summary>
        /// Make an object the user can pick up and move, using whatever this SDK calls that.
        ///
        /// For the shared menu panel, which builds itself at runtime and so cannot have an
        /// interactable authored onto it. The caller has already added the Rigidbody and the
        /// collider -- those are the same on both backends -- so this adds only the SDK's own
        /// interactable on top.
        /// </summary>
        void MakeGrabbable(GameObject target);

        /// <summary>
        /// Make a world-space canvas respond to this SDK's pointers, so its buttons can be pressed.
        /// </summary>
        void MakeCanvasPointable(Canvas canvas);

        /// <summary>
        /// Whether the user is currently holding this object. The menu asks before recentring
        /// itself, because yanking the panel out of someone's grip mid-move is worse than leaving
        /// it where they put it.
        /// </summary>
        bool IsGrabbed(GameObject target);
    }

    /// <summary>
    /// The registry the satellites announce themselves into, and that the neutral component asks
    /// when it needs to know who should drive it.
    /// </summary>
    public static class HexRInteractionBackends
    {
        private static readonly Dictionary<HexRManager.Options, IHexRInteractionBackend> backends =
            new Dictionary<HexRManager.Options, IHexRInteractionBackend>();

        /// <summary>
        /// Backends in a fixed order, so a project with both SDKs installed resolves the same way
        /// every run. Dictionary order is not guaranteed; this is.
        /// </summary>
        private static readonly HexRManager.Options[] probeOrder =
        {
            HexRManager.Options.OpenXR,
            HexRManager.Options.MetaOVR,
        };

        /// <summary>
        /// Idempotent: a domain reload re-runs every satellite's registration hook, and both the
        /// runtime and the editor attribute can fire in one session, so registering twice has to be
        /// harmless rather than a duplicate.
        /// </summary>
        public static void Register(IHexRInteractionBackend backend)
        {
            if (backend == null)
            {
                return;
            }

            backends[backend.Framework] = backend;
        }

        public static IHexRInteractionBackend Get(HexRManager.Options framework)
        {
            IHexRInteractionBackend backend;
            return backends.TryGetValue(framework, out backend) ? backend : null;
        }

        public static bool IsRegistered(HexRManager.Options framework)
        {
            return backends.ContainsKey(framework);
        }

        public static IEnumerable<IHexRInteractionBackend> All
        {
            get
            {
                foreach (HexRManager.Options framework in probeOrder)
                {
                    IHexRInteractionBackend backend;
                    if (backends.TryGetValue(framework, out backend))
                    {
                        yield return backend;
                    }
                }
            }
        }

        /// <summary>
        /// Which backend should drive <paramref name="target"/>.
        ///
        /// The order mirrors the rig checks the two backend-setup classes already make
        /// (<c>IsOpenXRRig</c> / <c>IsMetaRig</c>), so the package answers "which backend is this?"
        /// exactly one way:
        ///
        /// <list type="number">
        /// <item>an explicit choice on the component, if that backend is compiled in;</item>
        /// <item>the rig's <see cref="HexRManager.XRFramework"/> -- the normal answer;</item>
        /// <item>whichever compiled backend can actually find something to bind to.</item>
        /// </list>
        ///
        /// Step 3 exists for a scene with no HexR Manager yet, which is common enough while
        /// authoring that failing outright would be unhelpful.
        /// </summary>
        public static IHexRInteractionBackend Resolve(HexRBackendChoice choice, GameObject target,
                                                      out string diagnostic)
        {
            diagnostic = null;

            if (choice != HexRBackendChoice.Auto)
            {
                HexRManager.Options wanted = choice == HexRBackendChoice.MetaOVR
                    ? HexRManager.Options.MetaOVR
                    : HexRManager.Options.OpenXR;

                IHexRInteractionBackend explicitBackend = Get(wanted);
                if (explicitBackend != null)
                {
                    return explicitBackend;
                }

                // Asking for a backend whose SDK isn't installed is worth saying out loud -- the
                // assembly is excluded from the build, so nothing registered and the component
                // would otherwise just be silent.
                diagnostic = "Backend is set to " + choice + ", but that SDK is not installed in this "
                           + "project, so nothing can drive this object. Set it back to Auto, or install "
                           + (wanted == HexRManager.Options.MetaOVR
                                ? "the Meta Interaction SDK (com.meta.xr.sdk.interaction)."
                                : "the XR Interaction Toolkit (com.unity.xr.interaction.toolkit).");
            }

            HexRManager manager = target != null ? target.GetComponentInParent<HexRManager>() : null;
            if (manager == null)
            {
                manager = HexRManager.Instance;
            }

            if (manager == null)
            {
                manager = HexRCompat.FindAny<HexRManager>(true);
            }

            if (manager != null)
            {
                IHexRInteractionBackend byFramework = Get(manager.XRFramework);
                if (byFramework != null)
                {
                    return byFramework;
                }
            }

            foreach (IHexRInteractionBackend backend in All)
            {
                if (target != null && backend.CanBind(target))
                {
                    return backend;
                }
            }

            if (diagnostic == null)
            {
                diagnostic = backends.Count == 0
                    ? "No interaction backend is installed. Add the XR Interaction Toolkit or the Meta "
                      + "Interaction SDK, then put your SDK's interactable on this object."
                    : "No installed interaction backend found anything to bind to on this object.";
            }

            return null;
        }

        /// <summary>A binding that holds nothing, so <c>Bind</c> never has to return null.</summary>
        public static readonly IHexRInteractionBinding Unbound = new NullBinding();

        private sealed class NullBinding : IHexRInteractionBinding
        {
            public bool IsBound { get { return false; } }

            public string Describe() { return "nothing"; }

            public void Dispose() { }
        }
    }
}
