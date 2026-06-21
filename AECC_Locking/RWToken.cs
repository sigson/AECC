using System;

namespace AECC.Locking
{
    /// <summary>
    /// Base for any container that owns lock state (a per-entity bag, a slim dictionary, ...).
    /// The value-type token routes its release back to the host so the host can locate the
    /// exact <c>ref long</c> lock-state field and run <see cref="RWCell.Exit"/> on it.
    /// There is exactly ONE host reference shared by all tokens of that container — no per-token
    /// allocation.
    /// </summary>
    public abstract class LockHost
    {
        /// <summary>
        /// Release the lock previously acquired for (container, slot). Mode is informational;
        /// the actual mode is recovered from the thread-static accounting inside <see cref="RWCell"/>.
        /// </summary>
        internal abstract void ReleaseSlot(object container, int slot, byte mode);
    }

    /// <summary>
    /// Disposable lock token. A <c>struct</c>, so the hot path allocates nothing.
    /// <para>
    /// A <c>default(RWToken)</c> is a genuine no-op (used for OneThreadMode and for the
    /// cross-mode "dummy" path). A real/reentrant token carries the owning host + identity and,
    /// on <see cref="Dispose"/>, decrements depth and performs the real release at depth 0.
    /// </para>
    /// <para>
    /// IMPORTANT: like <see cref="System.Threading.ReaderWriterLockSlim"/>, a token must be
    /// disposed on the SAME thread that acquired it. The synchronous API guarantees this; do not
    /// carry a token across an <c>await</c> boundary.
    /// </para>
    /// </summary>
    public struct RWToken : IDisposable
    {
        private LockHost _host;
        private readonly object _container;
        private readonly int _slot;
        private readonly byte _mode; // 0 = read, 1 = write

        internal RWToken(LockHost host, object container, int slot, byte mode)
        {
            _host = host;
            _container = container;
            _slot = slot;
            _mode = mode;
        }

        /// <summary>True for a real or reentrant token; false for the no-op dummy.</summary>
        public bool IsReal { get { return _host != null; } }

        /// <summary>Idempotent: a second Dispose does nothing.</summary>
        public void Dispose()
        {
            LockHost h = _host;
            if (h != null)
            {
                _host = null;
                h.ReleaseSlot(_container, _slot, _mode);
            }
        }
    }
}
