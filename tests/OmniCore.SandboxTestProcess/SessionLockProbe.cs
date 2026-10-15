namespace OmniCore.SandboxTestProcess;

using System.Runtime.CompilerServices;
using OmniCore.Domain;
using OmniCore.Infrastructure;

// Extension packages intentionally copy only the fixture's own files. Keep the lease
// probe out of the top-level async state machine so other modes can start without
// loading Infrastructure and its dependencies. The session-lock mode still uses the
// real production store in the complete test output directory.
internal static class SessionLockProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string journal, string session)
    {
        using var store = new SqliteEventStore(journal);
        store.AcquireSessionWriterLease(SessionId.Parse(session));
        Console.WriteLine("READY");
        Console.Out.Flush();
        Console.ReadLine();
        return 0;
    }
}
