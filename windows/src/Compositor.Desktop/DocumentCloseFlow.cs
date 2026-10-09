namespace Compositor.Desktop;

/// <summary>
/// The policy between an unsaved-changes question and the save operation it may request. Keeping this small
/// boundary independent of Avalonia lets the close path be tested without pretending a file picker succeeded.
/// </summary>
internal static class DocumentCloseFlow
{
    /// <summary>
    /// Returns whether a document may be closed. A clean document never asks; a failed or cancelled save is a
    /// refusal to close, just like Cancel.
    /// </summary>
    public static async Task<bool> ConfirmAsync(bool isModified, Func<Task<UnsavedChangesChoice>> ask,
        Func<Task<bool>> save)
    {
        if (!isModified) return true;
        return await ask() switch
        {
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Save => await save(),
            _ => false,
        };
    }

    /// <summary>
    /// Confirms a close set in the order supplied. The caller chooses that order (the active tab first for a
    /// window close); stopping at the first refusal keeps every remaining document alive and avoids silently
    /// walking through more unsaved work after Cancel.
    /// </summary>
    public static async Task<bool> ConfirmAllAsync<T>(IEnumerable<T> documents, Func<T, Task<bool>> confirm)
    {
        foreach (var document in documents)
        {
            if (!await confirm(document)) return false;
        }
        return true;
    }
}

/// <summary>
/// Owns the monotonic token used by delayed preview work. A timer callback captures a token when it is queued;
/// disposing, hiding, or replacing that preview advances the token before freeing its bitmap, making an already
/// queued callback a harmless no-op.
/// </summary>
internal sealed class PreviewCallbackGeneration
{
    private long _current;

    public long Current => _current;

    /// <summary>Issues a token for newly queued preview work.</summary>
    public long Schedule() => ++_current;

    /// <summary>Invalidates every token issued before this point.</summary>
    public void Invalidate() => ++_current;

    public bool IsCurrent(long token) => token == _current;
}
