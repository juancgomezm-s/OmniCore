using System.Reflection;
using System.Reflection.Emit;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// ADR-0046 §4: canonical journal writes stay behind EventStream, and provider deltas stay outside
/// DomainEventPayload. This IL guard deliberately scans compiled production assemblies rather than
/// grepping source text, so calls from generated async state machines are included.
/// </summary>
public sealed class CanonicalWriterArchitectureTests
{
    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static CanonicalWriterArchitectureTests()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode) continue;
            var value = unchecked((ushort)opcode.Value);
            if (value < 0x100) OneByteOpCodes[value] = opcode;
            else if ((value & 0xff00) == 0xfe00) TwoByteOpCodes[value & 0xff] = opcode;
        }
    }

    [Fact]
    public void Production_event_store_calls_are_confined_to_EventStream_or_store_delegation()
    {
        var violations = new List<string>();
        foreach (var assembly in ProductionAssemblies())
        foreach (var call in CallsIn(assembly))
        {
            var targetType = call.Target.DeclaringType;
            if (targetType == typeof(IEventStore)
                && call.Target.Name is nameof(IEventStore.Append) or nameof(IEventStore.AppendBatch))
            {
                if (call.Caller.DeclaringType != typeof(EventStream))
                    violations.Add(Format(call, "IEventStore writer call must be inside EventStream"));
                continue;
            }

            if (targetType is not null && typeof(IEventStore).IsAssignableFrom(targetType)
                && targetType != typeof(IEventStore)
                && call.Target.Name is nameof(IEventStore.Append) or nameof(IEventStore.AppendBatch))
            {
                // A store may delegate Append to its own atomic AppendBatch implementation.
                if (call.Caller.DeclaringType != targetType)
                    violations.Add(Format(call, "concrete store writer may only be called by its own implementation"));
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void EventStream_writer_api_is_not_called_or_constructed_outside_Engine_and_Host()
    {
        var violations = new List<string>();
        foreach (var assembly in ProductionAssemblies())
        foreach (var call in CallsIn(assembly))
        {
            if (call.Target.DeclaringType != typeof(EventStream)
                || call.Target.Name is not (".ctor" or nameof(EventStream.Append) or nameof(EventStream.AppendBatch)))
                continue;

            var callerAssembly = call.Caller.Module.Assembly.GetName().Name ?? "";
            if (callerAssembly is not ("OmniCore.Engine" or "OmniCore.Host"))
                violations.Add(Format(call, "EventStream writer boundary is outside Engine/Host"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Model_stream_events_are_not_canonical_payloads_and_model_step_completion_is()
    {
        var streamEventTypes = ProductionAssemblies().SelectMany(SafeTypes)
            .Where(type => !type.IsAbstract && typeof(ModelStreamEvent).IsAssignableFrom(type))
            .ToArray();
        Assert.NotEmpty(streamEventTypes);
        Assert.All(streamEventTypes, type => Assert.False(typeof(DomainEventPayload).IsAssignableFrom(type),
            type.FullName + " must not be a canonical journal payload"));

        Assert.True(typeof(DomainEventPayload).IsAssignableFrom(typeof(ModelStepCompleted)));
        Assert.False(typeof(ModelStreamEvent).IsAssignableFrom(typeof(ModelStepCompleted)));
        Assert.Equal("model_step.completed", new ModelStepCompleted(TurnId.New(), 0,
            new TokenUsage(1, 1, 0, 0, 0), StopReason.EndTurn, null, "2026-10-06", null).Type().ToString());
    }

    [Fact]
    public void Scanner_control_positive_finds_forbidden_store_and_EventStream_writer_call_sites()
    {
        var directStoreMethod = typeof(ForbiddenCallFixtures).GetMethod(nameof(ForbiddenCallFixtures.DirectStoreAppend),
            BindingFlags.Public | BindingFlags.Static)!;
        var directStreamMethod = typeof(ForbiddenCallFixtures).GetMethod(nameof(ForbiddenCallFixtures.DirectEventStreamAppend),
            BindingFlags.Public | BindingFlags.Static)!;

        Assert.Contains(CallsIn(directStoreMethod), call => call.Target.DeclaringType == typeof(IEventStore)
            && call.Target.Name == nameof(IEventStore.Append));
        var streamCalls = CallsIn(directStreamMethod).ToArray();
        Assert.Contains(streamCalls, call => call.Target.DeclaringType == typeof(EventStream)
            && call.Target.Name == ".ctor");
        Assert.Contains(streamCalls, call => call.Target.DeclaringType == typeof(EventStream)
            && call.Target.Name == nameof(EventStream.Append));

        var asyncMethod = typeof(ForbiddenCallFixtures).GetMethod(nameof(ForbiddenCallFixtures.DirectStoreAppendAfterAwait))!;
        var stateMachine = asyncMethod.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
        Assert.NotNull(stateMachine);
        var moveNext = stateMachine.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Contains(CallsIn(moveNext), call => call.Target.DeclaringType == typeof(IEventStore)
            && call.Target.Name == nameof(IEventStore.Append));
    }

    private static IEnumerable<Assembly> ProductionAssemblies()
    {
        var directory = AppContext.BaseDirectory;
        var paths = Directory.EnumerateFiles(directory, "OmniCore.*.dll").ToArray();
        foreach (var required in new[] { "OmniCore.Engine.dll", "OmniCore.Host.dll", "OmniCore.Infrastructure.dll" })
            Assert.Contains(paths, path => Path.GetFileName(path) == required);
        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".Tests", StringComparison.Ordinal)
                || name.Equals("OmniCore.SandboxTestProcess", StringComparison.Ordinal))
                continue;

            var assembly = Assembly.LoadFrom(path);
            if (assembly.GetName().Name?.StartsWith("OmniCore.", StringComparison.Ordinal) == true)
                yield return assembly;
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        // A partial inventory cannot prove the boundary. Loading failures fail the test.
        return assembly.GetTypes();
    }

    private static IEnumerable<IlCall> CallsIn(Assembly assembly)
    {
        foreach (var type in SafeTypes(assembly))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (var call in CallsIn(method)) yield return call;
        }

        foreach (var type in SafeTypes(assembly))
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (var call in CallsIn(constructor)) yield return call;
        }
    }

    private static IEnumerable<IlCall> CallsIn(MethodBase caller)
    {
        var body = caller.GetMethodBody();
        var bytes = body?.GetILAsByteArray();
        if (bytes is null) yield break;
        var module = caller.Module;
        var typeArguments = caller.DeclaringType?.GetGenericArguments();
        var methodArguments = caller.IsGenericMethod ? caller.GetGenericArguments() : null;
        var offset = 0;

        while (offset < bytes.Length)
        {
            var instructionStart = offset;
            var first = bytes[offset++];
            var opcode = first == 0xfe
                ? TwoByteOpCodes[ReadByte(bytes, ref offset)]
                : OneByteOpCodes[first];
            if (opcode.Size == 0)
                throw new InvalidOperationException("Unknown IL opcode in " + caller);

            MemberInfo? target = null;
            if (opcode.OperandType == OperandType.InlineMethod
                || opcode.OperandType == OperandType.InlineTok)
            {
                var token = ReadInt32(bytes, ref offset);
                target = module.ResolveMember(token, typeArguments, methodArguments);
            }
            else
            {
                SkipOperand(bytes, ref offset, opcode.OperandType);
            }

            if (target is MethodBase methodTarget
                && opcode.OperandType is OperandType.InlineMethod or OperandType.InlineTok)
                yield return new IlCall(caller, methodTarget);

            if (offset <= instructionStart)
                throw new InvalidOperationException("IL decoder did not advance in " + caller);
        }
    }

    private static void SkipOperand(byte[] bytes, ref int offset, OperandType operandType)
    {
        var count = operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineSig
                or OperandType.InlineString or OperandType.InlineSwitch or OperandType.InlineTok
                or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineMethod => 4,
            _ => throw new InvalidOperationException("Unsupported IL operand " + operandType),
        };

        if (operandType == OperandType.InlineSwitch)
        {
            var switchCount = ReadInt32(bytes, ref offset);
            if (switchCount < 0 || switchCount > (bytes.Length - offset) / 4)
                throw new InvalidOperationException("Invalid IL switch table");
            offset += switchCount * 4;
            return;
        }
        EnsureAvailable(bytes, offset, count);
        offset += count;
    }

    private static byte ReadByte(byte[] bytes, ref int offset)
    {
        EnsureAvailable(bytes, offset, 1);
        return bytes[offset++];
    }

    private static int ReadInt32(byte[] bytes, ref int offset)
    {
        EnsureAvailable(bytes, offset, 4);
        var value = BitConverter.ToInt32(bytes, offset);
        offset += 4;
        return value;
    }

    private static void EnsureAvailable(byte[] bytes, int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > bytes.Length - count)
            throw new InvalidOperationException("Truncated IL method body");
    }

    private static string Format(IlCall call, string reason) =>
        (call.Caller.DeclaringType?.FullName ?? "?") + "." + call.Caller.Name + " -> "
        + (call.Target.DeclaringType?.FullName ?? "?") + "." + call.Target.Name + ": " + reason;

    private sealed record IlCall(MethodBase Caller, MethodBase Target);

    // Never invoked: these intentionally forbidden call sites verify that the IL scanner reports
    // both direct store writes and EventStream writes from outside the allowed production boundary.
    public static class ForbiddenCallFixtures
    {
        public static void DirectStoreAppend(IEventStore store, SessionId session, DomainEvent evt) =>
            store.Append(session, evt, DurabilityClass.Standard, CancellationToken.None);

        public static async System.Threading.Tasks.Task DirectStoreAppendAfterAwait(IEventStore store,
            SessionId session, DomainEvent evt)
        {
            await System.Threading.Tasks.Task.Yield();
            store.Append(session, evt, DurabilityClass.Standard, CancellationToken.None);
        }

        public static void DirectEventStreamAppend(IEventStore store, IEventCodecRegistry codecs,
            SessionId session, DomainEventPayload payload)
        {
            var stream = new EventStream(store, codecs, session);
            stream.Append(payload, DurabilityClass.Standard);
        }
    }
}
