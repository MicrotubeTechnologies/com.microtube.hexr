using System.Runtime.CompilerServices;

// AutoSetup and the setup validator live in HexR.Editor -- they are editor-only and have no runtime
// caller -- but the scene-wiring helpers they drive have to stay here, because RebindHand calls them
// on every scene load. Rather than make those helpers public and pin six implementation details to
// the package's supported surface forever, the editor assembly is named as a friend.
[assembly: InternalsVisibleTo("HexR.Editor")]
