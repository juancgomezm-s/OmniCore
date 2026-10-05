using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.ArchitectureTests;

public sealed class TelemetryArchitectureTests
{
    [Fact]
    public void Telemetry_types_are_not_domain_payloads_or_part_of_the_canonical_journal_entrypoints()
    {
        Assert.False(typeof(DomainEventPayload).IsAssignableFrom(typeof(TelemetryRecord)));
        Assert.NotEqual(typeof(DomainEventPayload).Assembly, typeof(TelemetryRecord).Assembly);

        AssertNoTelemetrySurface(typeof(EventStream));
        AssertNoTelemetrySurface(typeof(IEventStore));
        AssertNoTelemetrySurface(typeof(IEventCodecRegistry));
        AssertNoTelemetrySurface(typeof(IDomainEventCodec));
        AssertNoCanonicalJournalSurface(typeof(ITelemetrySink));
        AssertNoCanonicalJournalSurface(typeof(InMemoryTelemetrySink));

        var root = RepositoryRoot();
        var codecSource = File.ReadAllText(Path.Combine(root, "src", "OmniCore.Infrastructure", "EventCodecs.cs"));
        var streamSource = File.ReadAllText(Path.Combine(root, "src", "OmniCore.Engine", "EventStream.cs"));
        Assert.DoesNotContain("Telemetry", codecSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ITelemetrySink", streamSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Telemetry_records_have_only_closed_vocabularies_numeric_values_UTC_and_entity_IDs()
    {
        var properties = typeof(TelemetryRecord).GetProperties();
        Assert.Equal(new[]
        {
            nameof(TelemetryRecord.Kind), nameof(TelemetryRecord.Signal), nameof(TelemetryRecord.Value),
            nameof(TelemetryRecord.TimestampUtc), nameof(TelemetryRecord.RunId), nameof(TelemetryRecord.TaskId),
            nameof(TelemetryRecord.LaneId), nameof(TelemetryRecord.TurnId), nameof(TelemetryRecord.ToolCallId),
            nameof(TelemetryRecord.ExecutionId),
        }.Order(StringComparer.Ordinal), properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(properties, property => Assert.NotEqual(typeof(string), property.PropertyType));
        Assert.Equal(typeof(long), typeof(TelemetryRecord).GetProperty(nameof(TelemetryRecord.Value))!.PropertyType);
        Assert.Equal(typeof(DateTimeOffset), typeof(TelemetryRecord).GetProperty(nameof(TelemetryRecord.TimestampUtc))!.PropertyType);
        Assert.Equal(typeof(TelemetryKind), typeof(TelemetryRecord).GetProperty(nameof(TelemetryRecord.Kind))!.PropertyType);
        Assert.Equal(typeof(TelemetrySignal), typeof(TelemetryRecord).GetProperty(nameof(TelemetryRecord.Signal))!.PropertyType);
    }

    private static void AssertNoTelemetrySurface(Type entryPoint)
    {
        foreach (var method in entryPoint.GetMethods())
        {
            Assert.DoesNotContain(typeof(TelemetryRecord), method.GetParameters().Select(parameter => parameter.ParameterType));
            Assert.DoesNotContain(typeof(ITelemetrySink), method.GetParameters().Select(parameter => parameter.ParameterType));
            Assert.NotEqual(typeof(TelemetryRecord), method.ReturnType);
            Assert.NotEqual(typeof(ITelemetrySink), method.ReturnType);
        }
    }

    private static void AssertNoCanonicalJournalSurface(Type entryPoint)
    {
        foreach (var method in entryPoint.GetMethods())
        {
            Assert.DoesNotContain(typeof(EventStream), method.GetParameters().Select(parameter => parameter.ParameterType));
            Assert.DoesNotContain(typeof(IEventStore), method.GetParameters().Select(parameter => parameter.ParameterType));
            Assert.NotEqual(typeof(EventStream), method.ReturnType);
            Assert.NotEqual(typeof(IEventStore), method.ReturnType);
        }
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
