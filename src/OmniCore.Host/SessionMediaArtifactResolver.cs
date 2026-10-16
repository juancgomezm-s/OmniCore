namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Abstractions;
using OmniCore.Infrastructure;

/// <summary>Finds an image reference only in the currently selected Session's canonical events.</summary>
internal static class SessionMediaArtifactResolver
{
    internal static ArtifactRef? Resolve(IEventStore store, Func<SessionId?> currentSession,
        EventCodecs codecs, string hash)
    {
        if (currentSession() is not { } session) return null;
        foreach (var evt in store.ReadFrom(session, 1))
        {
            var reference = codecs.Decode(evt) switch
            {
                UserInputReceived input => input.Attachments?.FirstOrDefault(item => item.Hash.Value == hash),
                ToolCallSucceeded success => success.ResultArtifact?.Hash.Value == hash ? success.ResultArtifact : null,
                AssistantImagesRecorded images => images.Images.FirstOrDefault(item => item.Artifact.Hash.Value == hash)?.Artifact,
                _ => null,
            };
            if (reference is not null) return reference;
        }
        return null;
    }
}
