namespace OmniCore.Host;

using OmniCore.Protocol;

/// <summary>Host-only failure carrier; the wire ACK and its outcome vocabulary remain unchanged.</summary>
internal sealed record InternalCommandResult(CommandAck Ack, Exception? Failure = null)
{
    public string CommandId => Ack.CommandId;
    public string Status => Ack.Status;
    public string? Error => Ack.Error;
    public RuntimeCommandOutcome? Outcome => Ack.Outcome;
    public long? FirstSeq => Ack.FirstSeq;
    public long? LastSeq => Ack.LastSeq;

    public void ThrowIfFailure()
    {
        if (Failure is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    public static implicit operator InternalCommandResult(CommandAck ack) => new(ack);
}
