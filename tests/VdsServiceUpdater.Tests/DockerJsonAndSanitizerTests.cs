using Xunit;
using VdsServiceUpdater.Docker;

namespace VdsServiceUpdater.Tests;

public class DockerJsonTests
{
    [Fact]
    public void Parses_container_with_and_without_health()
    {
        var a = DockerJson.ParseContainers(DockerTestData.Inspect("running", "healthy", 2, 0, "app:2")).Single();
        Assert.Equal(("running", "healthy", 2, "app:2"), (a.Status, a.Health, a.RestartCount, a.Image));

        var b = DockerJson.ParseContainers(DockerTestData.Inspect("exited", null, 0, 137, "app:2")).Single();
        Assert.Null(b.Health);
        Assert.Equal(137, b.ExitCode);
    }

    [Fact]
    public void Empty_output_means_no_containers() =>
        Assert.Empty(DockerJson.ParseContainers("  "));

    [Fact]
    public void Parses_service_with_update_status()
    {
        var s = DockerJson.ParseService(DockerTestData.Svc("app:2@sha256:abc", "completed", "T1", "done"));
        Assert.Equal(("app:2@sha256:abc", "completed", "T1", "done"), (s.Image, s.UpdateState, s.UpdateStartedAt, s.UpdateMessage));
    }

    [Fact]
    public void Parses_service_without_update_status()
    {
        var s = DockerJson.ParseService(DockerTestData.Svc("app:1", null, null));
        Assert.Equal("app:1", s.Image);
        Assert.Null(s.UpdateState);
    }
}

public class OutputSanitizerTests
{
    [Fact]
    public void Masks_known_secret_and_patterns()
    {
        var text = OutputSanitizer.Mask("login failed, secret-value-123 used; TOKEN=abc123 password: hunter2", ["secret-value-123"]);
        Assert.DoesNotContain("secret-value-123", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("TOKEN=***", text);
    }

    [Fact]
    public void Tail_keeps_the_end()
    {
        var t = OutputSanitizer.Tail(new string('a', 100) + "END", 10);
        Assert.EndsWith("END", t);
        Assert.True(t.Length <= 11);
    }
}
