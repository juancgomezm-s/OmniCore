namespace OmniCore.Infrastructure;

using System.Text.Json.Serialization;
using OmniCore.Domain;

[JsonSerializable(typeof(ArtifactRef))]
internal sealed partial class ModelQualificationJsonContext : JsonSerializerContext;
