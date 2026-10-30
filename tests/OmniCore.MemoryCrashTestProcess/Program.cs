using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Memory;

// Isolated fixture only. The parent owns and terminates this process at an acknowledged
// durability boundary; it never attaches to an existing OmniCore process or user profile.
if (args.Length != 5) return 2;
var root = Path.GetFullPath(args[0]);
var session = SessionId.Parse(args[1]);
var scopeName = args[2];
var operation = args[3];
var cut = args[4];
if (operation is not ("promote" or "forget")
    || cut is not ("requested" or "approved" or "prepared" or "database" or "receipt" or "completed" or "transaction")) return 2;
var workspace = Path.Combine(root, "workspace");
Directory.CreateDirectory(workspace);
var paths = OmniHost.CreatePlatformPaths(Path.Combine(root, "data"));
var scope = scopeName switch
{
    "global" => MemoryScope.ForUser(),
    "workspace" => MemoryScope.ForWorkspace(WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
        ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspace)))),
    "project" => MemoryScope.ForProject(ProjectIdentity.FromWorkspaceRoot(workspace)),
    _ => throw new ArgumentException("Invalid fixture scope."),
};
using var journal = SqliteEventStore.OpenUnpooled(Path.Combine(root, "journal.db"));
var codecs = EventCodecs.Create();
var stream = new EventStream(journal, codecs, session);
var memories = new ScopedMemoryStore(paths.DataDirectory);
var armed = operation == "promote";
IReadOnlyList<DomainEventPayload> ReadJournal() => journal.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
bool Allowed(MemoryScope candidate) => candidate == scope && (scope.Level != ScopeLevel.Project
    || new WorkspaceTrustStore(paths).IsTrusted(workspace));

void Pause()
{
    Console.WriteLine("ready:" + cut);
    Console.Out.Flush();
    if (cut == "transaction")
    {
        var input = Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        if (input != "continue") throw new InvalidDataException("The fixture handshake was interrupted.");
        return;
    }
    // A lost parent must not leave a permanent hidden process behind.
    Thread.Sleep(TimeSpan.FromSeconds(45));
    throw new TimeoutException("The parent did not terminate its crash fixture.");
}

void Emit(DomainEventPayload payload)
{
    if (armed && cut == "database" && (operation == "promote" && payload is MemoryPromoted
        || operation == "forget" && payload is MemoryForgotten)) Pause();
    if (armed && cut == "receipt" && payload is ScopedMemoryMutationCompleted) Pause();
    stream.Append(payload, DurabilityClass.Barrier);
    if (!armed) return;
    if (cut == "requested" && payload is InteractionRequested
        || cut is "prepared" or "transaction" && payload is ScopedMemoryMutationPrepared
        || cut == "completed" && payload is ScopedMemoryMutationCompleted) Pause();
}

var policy = new ScopedMemoryPolicy(session, memories, ReadJournal, Emit, Allowed);
var proposal = policy.Propose(scope, "Use the copper checklist before a release.", MemoryKind.Convention);
var request = ReadJournal().OfType<InteractionRequested>().Single();
stream.Append(new InteractionResolved(request.InteractionId, "promote", InteractionCause.User), DurabilityClass.Barrier);
if (armed && cut == "approved") Pause();
policy.Promote(proposal.Id);
if (operation == "forget")
{
    armed = true;
    var record = memories.Read(scope, CancellationToken.None).Single();
    policy.Forget(scope, record.Id);
}
return 0;
