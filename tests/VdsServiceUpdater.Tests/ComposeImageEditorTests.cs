using Xunit;
using VdsServiceUpdater.Compose;
using VdsServiceUpdater.Images;

namespace VdsServiceUpdater.Tests;

public class ComposeImageEditorTests
{
    private const string Sample = ComposeFileScannerTests.Sample;
    private static readonly string[] NoProtected = [];

    private static ImageReference Img(string s)
    {
        Assert.True(ImageReference.TryParse(s, out var r, out _));
        return r!;
    }

    private static int[] ChangedLines(string a, string b)
    {
        var x = a.Split('\n'); var y = b.Split('\n');
        Assert.Equal(x.Length, y.Length);
        return Enumerable.Range(0, x.Length).Where(i => x[i] != y[i]).ToArray();
    }

    [Fact]
    public void Changes_only_tag_and_keeps_comment()
    {
        var r = Apply(Sample, "ghcr.io/org/app:2.0");
        Assert.Contains("    image: ghcr.io/org/app:2.0   # keep me", r.Content);
        Assert.Equal([3], ChangedLines(Sample, r.Content));
        var c = Assert.Single(r.Changes);
        Assert.Equal((ServiceOutcome.Updated, "ghcr.io/org/app:1.0", "ghcr.io/org/app:2.0"), (c.Outcome, c.PreviousImage, c.NewImage));
    }

    [Fact]
    public void Keeps_double_quotes()
    {
        var r = Apply(Sample, "ghcr.io/org/worker:2.0");
        Assert.Contains("image: \"ghcr.io/org/worker:2.0\"", r.Content);
        Assert.Single(ChangedLines(Sample, r.Content));
    }

    [Fact]
    public void Keeps_single_quotes()
    {
        var r = Apply("services:\n  app:\n    image: 'ghcr.io/org/app:1'\n", "ghcr.io/org/app:2");
        Assert.Contains("image: 'ghcr.io/org/app:2'", r.Content);
    }

    [Fact]
    public void Preserves_crlf()
    {
        var crlf = Sample.Replace("\n", "\r\n");
        var r = Apply(crlf, "ghcr.io/org/app:2.0");
        Assert.Contains("image: ghcr.io/org/app:2.0   # keep me\r\n", r.Content);
        Assert.DoesNotContain("\n", r.Content.Replace("\r\n", ""));
    }

    [Fact]
    public void Preserves_missing_trailing_newline_and_trailing_newline()
    {
        Assert.False(Apply(Sample, "ghcr.io/org/app:2.0").Content.EndsWith('\n'));
        Assert.True(Apply(Sample + "\n", "ghcr.io/org/app:2.0").Content.EndsWith('\n'));
    }

    [Fact]
    public void Updates_every_service_with_same_image_and_skips_protected()
    {
        const string yaml = "services:\n  app:\n    image: ghcr.io/org/app:1.0\n  app-db:\n    image: ghcr.io/org/app:1.0\n  app2:\n    image: ghcr.io/org/app:1.0\n";
        var r = ComposeImageEditor.Apply(yaml, Img("ghcr.io/org/app:2.0"), ["*-db"]);
        Assert.Equal(["app:Updated", "app-db:SkippedProtected", "app2:Updated"],
            r.Changes.Select(c => $"{c.Service}:{c.Outcome}").ToArray());
        Assert.Equal([2, 6], ChangedLines(yaml, r.Content));
    }

    [Fact]
    public void Variable_tag_is_skipped_and_file_untouched()
    {
        const string yaml = "services:\n  app:\n    image: ghcr.io/org/app:${TAG}\n";
        var r = Apply(yaml, "ghcr.io/org/app:2.0");
        Assert.Equal(ServiceOutcome.SkippedVariable, Assert.Single(r.Changes).Outcome);
        Assert.Equal(yaml, r.Content);
    }

    [Fact]
    public void Same_tag_is_unchanged()
    {
        var r = Apply(Sample, "ghcr.io/org/app:1.0");
        Assert.Equal(ServiceOutcome.Unchanged, Assert.Single(r.Changes).Outcome);
        Assert.False(r.HasUpdates);
        Assert.Equal(Sample, r.Content);
    }

    [Fact]
    public void Keeps_repository_spelling_from_file()
    {
        var r = Apply("services:\n  web:\n    image: nginx:1.0\n", "docker.io/library/nginx:1.27");
        Assert.Contains("image: nginx:1.27", r.Content);
    }

    [Fact]
    public void Adds_tag_when_file_has_none()
    {
        var r = Apply("services:\n  web:\n    image: nginx\n", "nginx:1.27");
        Assert.Contains("image: nginx:1.27", r.Content);
    }

    [Fact]
    public void Handles_registry_with_port()
    {
        var r = Apply("services:\n  a:\n    image: localhost:5000/app:1\n", "localhost:5000/app:2");
        Assert.Contains("image: localhost:5000/app:2", r.Content);
    }

    [Fact]
    public void Digest_pinned_is_skipped()
    {
        var yaml = $"services:\n  a:\n    image: ghcr.io/org/app:1.0@sha256:{new string('a', 64)}\n";
        var r = Apply(yaml, "ghcr.io/org/app:2.0");
        Assert.Equal(ServiceOutcome.SkippedPinned, Assert.Single(r.Changes).Outcome);
        Assert.Equal(yaml, r.Content);
    }

    [Fact]
    public void No_matching_service_returns_no_changes()
    {
        var r = Apply(Sample, "ghcr.io/org/other:1");
        Assert.Empty(r.Changes);
        Assert.Equal(Sample, r.Content);
    }

    [Theory]
    [InlineData("services:\n  app: {image: \"ghcr.io/org/app:1.0\"}\n")]           // flow-стиль
    [InlineData("services:\n  app:\n    image: &i ghcr.io/org/app:1.0\n")]          // якорь на значении
    [InlineData("services:\n  app:\n    image: >-\n      ghcr.io/org/app:1.0\n")]   // block scalar
    public void Unsupported_layouts_throw_instead_of_guessing(string yaml)
    {
        var ex = Assert.Throws<ComposeFileException>(() => Apply(yaml, "ghcr.io/org/app:2.0"));
        Assert.Equal("unsupported_format", ex.Code);
    }

    private static EditResult Apply(string yaml, string image) =>
        ComposeImageEditor.Apply(yaml, Img(image), NoProtected);
}
