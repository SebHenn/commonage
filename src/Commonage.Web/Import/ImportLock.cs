namespace Commonage.Web.Import;

/// <summary>An import runs one at a time, whatever it reads: two of them would each be sure that an item is not there yet.</summary>
public static class ImportLock
{
    /// <summary>Taken with <c>WaitAsync(0)</c> and released in a <c>finally</c>.</summary>
    public static readonly SemaphoreSlim OneAtATime = new(1, 1);
}
