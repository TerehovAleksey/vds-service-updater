using Xunit;
using VdsServiceUpdater.Images;

namespace VdsServiceUpdater.Tests;

public class ImageReferenceTests
{
    [Theory]
    [InlineData("ghcr.io/org/app:1.4.2", "ghcr.io", "org/app", "1.4.2")]
    [InlineData("nginx:1.27", "docker.io", "library/nginx", "1.27")]
    [InlineData("myorg/app:v1", "docker.io", "myorg/app", "v1")]
    [InlineData("index.docker.io/myorg/app:v1", "docker.io", "myorg/app", "v1")]
    [InlineData("GHCR.io/org/app:Tag_1", "ghcr.io", "org/app", "Tag_1")]
    [InlineData("localhost:5000/app:1", "localhost:5000", "app", "1")]
    [InlineData("registry.local:5000/a/b/c:2", "registry.local:5000", "a/b/c", "2")]
    public void Parses_valid_references(string input, string registry, string path, string tag)
    {
        Assert.True(ImageReference.TryParse(input, out var r, out _));
        Assert.Equal(registry, r!.Registry);
        Assert.Equal(path, r.Path);
        Assert.Equal(tag, r.Tag);
    }

    [Fact]
    public void Tag_is_optional_in_parser_and_registry_port_is_not_a_tag()
    {
        Assert.True(ImageReference.TryParse("registry:5000/a/b", out var r, out _));
        Assert.Null(r!.Tag);
        Assert.Equal("registry:5000/a/b", r.Repository);
    }

    [Fact]
    public void Parses_digest()
    {
        var d = "sha256:" + new string('a', 64);
        Assert.True(ImageReference.TryParse($"ghcr.io/org/app@{d}", out var r, out _));
        Assert.Equal(d, r!.Digest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" app:1")]
    [InlineData("app:")]
    [InlineData("ghcr.io/Org/app:1")]
    [InlineData("ghcr.io/org/app:1 && rm -rf /")]
    [InlineData("app:1;ls")]
    [InlineData("../app:1")]
    [InlineData("ghcr.io//app:1")]
    [InlineData("app@sha256:abc")]
    [InlineData("app:1\nX")]
    public void Rejects_invalid_references(string input)
    {
        Assert.False(ImageReference.TryParse(input, out var r, out var error));
        Assert.Null(r);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Rejects_too_long_input() =>
        Assert.False(ImageReference.TryParse("ghcr.io/org/" + new string('a', 300) + ":1", out _, out _));
}
