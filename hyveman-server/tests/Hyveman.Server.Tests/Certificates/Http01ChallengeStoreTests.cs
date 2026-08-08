using Hyveman.Server.Certificates;

namespace Hyveman.Server.Tests.Certificates;

public sealed class Http01ChallengeStoreTests
{
    [Fact]
    public void Set_ThenTryGet_ReturnsKeyAuthorization()
    {
        var store = new Http01ChallengeStore();
        store.Set("tok_abc", "abc.keyAuth");

        Assert.True(store.TryGet("tok_abc", out var value));
        Assert.Equal("abc.keyAuth", value);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Overwrite_ReplacesValue()
    {
        var store = new Http01ChallengeStore();
        store.Set("tok_abc", "first");
        store.Set("tok_abc", "second");

        Assert.True(store.TryGet("tok_abc", out var value));
        Assert.Equal("second", value);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Remove_DeletesToken()
    {
        var store = new Http01ChallengeStore();
        store.Set("tok_abc", "value");

        store.Remove("tok_abc");

        Assert.False(store.TryGet("tok_abc", out _));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void UnknownToken_IsNotFound()
    {
        var store = new Http01ChallengeStore();
        Assert.False(store.TryGet("never-set", out _));
    }
}
