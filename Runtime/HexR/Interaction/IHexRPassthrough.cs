namespace HexR
{
    /// <summary>
    /// Passthrough, as the shared menu needs to see it.
    ///
    /// This is an interface rather than a component in the package because passthrough is the one
    /// part of the menu that genuinely cannot be shared. On PICO it runs through the vendored PICO
    /// OpenXR plugin, which is on no registry and cannot be a package dependency; on Meta it is
    /// <c>OVRPassthroughLayer</c>. Each side implements this, the menu asks the interface, and
    /// <c>HexR.Runtime</c> stays free of both.
    ///
    /// Nothing implementing it in the scene is a normal state, not an error -- the menu greys the
    /// button out and says so.
    /// </summary>
    public interface IHexRPassthrough
    {
        /// <summary>Whether this runtime can actually show a camera feed.</summary>
        bool IsAvailable { get; }

        bool IsOn { get; }

        void Toggle();

        /// <summary>
        /// Re-acquire the camera. The rig survives scene loads; the camera it dims does not, so the
        /// menu calls this after every load.
        /// </summary>
        void RefreshCamera();
    }
}
