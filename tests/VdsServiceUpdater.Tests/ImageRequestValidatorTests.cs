using Xunit;
using Microsoft.Extensions.Options;
using VdsServiceUpdater.Images;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Tests;

public class ImageRequestValidatorTests
{
    private static ImageRequestValidator Create(bool allowLatest = false, params string[] prefixes)
    {
        var o = new UpdaterOptions();
        o.Deploy.AllowLatestTag = allowLatest;
        o.Deploy.AllowedImagePrefixes = prefixes.Length == 0 ? ["ghcr.io/myorg/"] : prefixes.ToList();
        return new ImageRequestValidator(Microsoft.Extensions.Options.Options.Create(o));
    }

    [Fact]
    public void Allows_image_under_prefix() =>
        Assert.NotNull(Create().Validate("ghcr.io/myorg/app:1.0").Image);

    [Theory]
    [InlineData("ghcr.io/otherorg/app:1.0")]
    [InlineData("ghcr.io/myorg2/app:1.0")]   // граница префикса по '/'
    [InlineData("docker.io/myorg/app:1.0")]
    public void Forbids_image_outside_prefix(string image)
    {
        var r = Create().Validate(image);
        Assert.Null(r.Image);
        Assert.Equal(403, r.StatusCode);
    }

    [Fact]
    public void Prefix_without_trailing_slash_does_not_match_sibling() =>
        Assert.Equal(403, Create(false, "ghcr.io/myorg").Validate("ghcr.io/myorgevil/app:1").StatusCode);

    [Fact]
    public void Docker_hub_prefix_matches_normalized_names()
    {
        var v = Create(false, "myorg/");
        Assert.NotNull(v.Validate("myorg/app:1").Image);
        Assert.NotNull(v.Validate("docker.io/myorg/app:1").Image);
        Assert.Equal(403, v.Validate("ghcr.io/myorg/app:1").StatusCode);
    }

    [Fact]
    public void Requires_tag() =>
        Assert.Equal("tag_required", Create().Validate("ghcr.io/myorg/app").Code);

    [Fact]
    public void Latest_is_forbidden_unless_allowed()
    {
        Assert.Equal("latest_tag_forbidden", Create().Validate("ghcr.io/myorg/app:latest").Code);
        Assert.NotNull(Create(true).Validate("ghcr.io/myorg/app:latest").Image);
    }

    [Fact]
    public void Invalid_format_is_422() =>
        Assert.Equal(422, Create().Validate("ghcr.io/myorg/app:1;ls").StatusCode);
}
