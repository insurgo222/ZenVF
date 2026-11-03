namespace ZenVF.Interop;

/// <summary>
/// Bookkeeping for "which FMax must be restored", kept free of driver calls
/// so the rules can be unit tested:
/// <list type="bullet">
/// <item>Before the first write of a dirty period the current ceiling is
/// captured as <see cref="Target"/>; if it cannot be read, the write must be
/// refused.</item>
/// <item>Later writes in the same dirty period never replace the target, so a
/// failed restore cannot turn a test value into the "original".</item>
/// <item>The target is cleared only by a successful write of the target value
/// whose read-back equals it.</item>
/// </list>
/// Not thread safe; <see cref="SmuController"/> serialises access.
/// </summary>
public sealed class FMaxRestoreTracker
{
    /// <summary>Ceiling to restore, or null when ZenVF has nothing to undo.</summary>
    public uint? Target { get; private set; }

    public bool HasPendingRestore => Target is not null;

    /// <summary>
    /// Captures the current ceiling if no restore is pending. Returns false
    /// when it had to read and the read failed; the caller must then refuse
    /// the write.
    /// </summary>
    public bool EnsureCaptured(Func<uint?> readCurrent)
    {
        if (Target is not null)
        {
            return true;
        }

        uint? current = readCurrent();
        if (current is null)
        {
            return false;
        }

        Target = current;
        return true;
    }

    /// <summary>Records the outcome of a write; clears the target on a verified restore.</summary>
    public void RecordWrite(uint requested, bool success, uint readBack)
    {
        if (success && Target == requested && readBack == requested)
        {
            Target = null;
        }
    }
}
