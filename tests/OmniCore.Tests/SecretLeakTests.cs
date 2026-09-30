using System.Text.Json;
using OmniCore.Abstractions;

namespace OmniCore.Tests;

public sealed class SecretLeakTests
{
    private const string Raw = "sk-super-secret-value";

    private sealed record Holder(string Name, Secret Key);

    [Fact]
    public void ToString_is_redacted()
    {
        var text = Secret.Of(Raw).ToString();
        Assert.Equal("***", text);
        Assert.DoesNotContain(Raw, text);
    }

    [Fact]
    public void Serializing_a_secret_throws()
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(Secret.Of(Raw)));
    }

    [Fact]
    public void Serializing_an_object_with_a_secret_property_throws()
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new Holder("x", Secret.Of(Raw))));
    }

    [Fact]
    public void Interpolation_does_not_contain_the_value()
    {
        var secret = Secret.Of(Raw);
        Assert.DoesNotContain(Raw, $"{secret}");
    }
}
