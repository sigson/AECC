// AECC.Locking — minimal Defines used by the lock core.
// In the real tree this is the existing `public static partial class Defines`.
// Only the flag that the lock core branches on is reproduced here.
namespace AECC.Locking
{
    public static class Defines
    {
        /// <summary>
        /// Global single-thread mode (bridge to Unity/Godot main loop).
        /// When true, every Enter*/Exit* on the lock core degenerates to a no-op:
        /// no CAS state touch, no parking, no thread-static accounting.
        /// Mirrors Defines.OneThreadMode in the real codebase.
        /// </summary>
        public static bool OneThreadMode = false;
    }
}
