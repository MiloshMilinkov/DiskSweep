namespace DiskSweep.Core.Cleanup;

/// <summary>Runs work on a single-threaded-apartment thread, which the shell's file operations expect.</summary>
public static class StaThread
{
    public static T Run<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return work();

        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "DiskSweep shell operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("A shell operation failed.", error);
        return result;
    }
}
