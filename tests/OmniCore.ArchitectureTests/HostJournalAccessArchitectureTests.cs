using System.Reflection;
using OmniCore.Abstractions;
using OmniCore.Host;

namespace OmniCore.ArchitectureTests;

public sealed class HostJournalAccessArchitectureTests
{
    [Fact]
    public void OmniServer_journal_handles_are_not_publicly_acquirable()
    {
        var server = typeof(OmniServer);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var store = Assert.Single(server.GetMethods(instance), method => method.Name == "AcquireStore");
        var codecs = Assert.Single(server.GetMethods(instance), method => method.Name == "AcquireCodecs");
        Assert.False(store.IsPublic);
        Assert.False(codecs.IsPublic);
        Assert.Equal(typeof(IEventStore), store.ReturnType);
        Assert.Equal(typeof(IEventCodecRegistry), codecs.ReturnType);
        // Also prevent a public replacement accessor under another name, including a property.
        Assert.DoesNotContain(server.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public),
            method => typeof(IEventStore).IsAssignableFrom(method.ReturnType)
                || typeof(IEventCodecRegistry).IsAssignableFrom(method.ReturnType));
        Assert.DoesNotContain(server.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public),
            field => typeof(IEventStore).IsAssignableFrom(field.FieldType)
                || typeof(IEventCodecRegistry).IsAssignableFrom(field.FieldType));
    }

    [Fact]
    public void Cli_queues_followups_only_through_the_internal_command_boundary()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "OmniCore.Host", "OmniCliRuntime.cs"));

        Assert.Contains("var followUp = server.QueueFollowUpPromptCommand(sessionId, runId, laneId, prompt, promptOrigin);",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (QueuePromptForOpenTurn(server.AcquireStore()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FollowUpQueue.TryQueue(server.AcquireStore()", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OmniCore.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
