using Tamp.Observer.Domain;
using Xunit;

namespace Tamp.Observer.Domain.Tests;

/// <summary>
/// Privacy gating (TOBS-28): credential-bearing attributes are replaced with the "not captured" sentinel while
/// their key is preserved; correlation/diagnostic attributes are left untouched. Fast lane.
/// </summary>
public sealed class AttributeRedactionTests
{
    [Theory]
    [InlineData("http.request.header.authorization")]
    [InlineData("Authorization")]
    [InlineData("set-cookie")]
    [InlineData("db.password")]
    [InlineData("x-api-key")]
    [InlineData("auth.token")]
    [InlineData("aws.secret")]
    public void Sensitive_keys_are_flagged(string key) => Assert.True(AttributeRedaction.IsSensitive(key));

    [Theory]
    [InlineData("tamp.session.id")]
    [InlineData("exception.type")]
    [InlineData("exception.stacktrace")]
    [InlineData("http.route")]
    [InlineData("service.version")]
    public void Benign_keys_are_not_flagged(string key) => Assert.False(AttributeRedaction.IsSensitive(key));

    [Fact]
    public void Redact_replaces_values_but_keeps_keys_and_leaves_others()
    {
        var attrs = new Dictionary<string, string>
        {
            ["authorization"] = "Bearer super-secret",
            ["http.route"] = "/orders/{id}",
            ["tamp.session.id"] = "sess-1",
            ["x-api-key"] = "k-123",
        };

        AttributeRedaction.Redact(attrs);

        Assert.Equal(AttributeRedaction.NotCaptured, attrs["authorization"]);
        Assert.Equal(AttributeRedaction.NotCaptured, attrs["x-api-key"]);
        Assert.Equal("/orders/{id}", attrs["http.route"]);     // untouched
        Assert.Equal("sess-1", attrs["tamp.session.id"]);       // correlation key untouched
        Assert.True(attrs.ContainsKey("authorization"));        // key preserved, not dropped
    }
}
