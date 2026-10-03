using Xunit;
using VdsServiceUpdater.Compose;

namespace VdsServiceUpdater.Tests;

public class ComposeFileScannerTests
{
    internal const string Sample = """
        # main stack
        services:
          app:
            image: ghcr.io/org/app:1.0   # keep me
            ports:
              - "80:80"
          worker:
            image: "ghcr.io/org/worker:1.0"
          db:
            image: postgres:16
        """;

    [Fact]
    public void Finds_services_with_line_numbers()
    {
        var r = ComposeFileScanner.Scan(Sample).ToDictionary(x => x.Service);
        Assert.Equal(4, r["app"].Line);
        Assert.Equal(8, r["worker"].Line);
        Assert.Equal(10, r["db"].Line);
        Assert.Equal("ghcr.io/org/app:1.0", r["app"].RawValue);
        Assert.Equal("ghcr.io/org/worker:1.0", r["worker"].RawValue); // кавычки сняты
        Assert.Equal("docker.io/library/postgres", r["db"].Reference!.Repository);
    }

    [Fact]
    public void Ignores_services_without_image()
    {
        var r = ComposeFileScanner.Scan("services:\n  web:\n    build: .\n  app:\n    image: a/b:1\n");
        Assert.Single(r);
        Assert.Equal("app", r[0].Service);
    }

    [Fact]
    public void Variable_in_tag_keeps_repository_match()
    {
        var r = ComposeFileScanner.Scan("services:\n  app:\n    image: ghcr.io/org/app:${TAG}\n").Single();
        Assert.True(r.UsesVariable);
        Assert.Equal("ghcr.io/org/app", r.Reference!.Repository);
    }

    [Fact]
    public void Fully_variable_image_has_no_reference()
    {
        var r = ComposeFileScanner.Scan("services:\n  app:\n    image: ${IMAGE}\n").Single();
        Assert.True(r.UsesVariable);
        Assert.Null(r.Reference);
    }

    [Theory]
    [InlineData("services:\n  app: [unclosed\n", "invalid_yaml")]
    [InlineData("version: '3'\n", "no_services")]
    [InlineData("", "no_services")]
    [InlineData("services:\n  a:\n    image: x:1\n---\nservices: {}\n", "unsupported_format")]
    public void Throws_with_code_on_bad_files(string yaml, string code) =>
        Assert.Equal(code, Assert.Throws<ComposeFileException>(() => ComposeFileScanner.Scan(yaml)).Code);
}

public class GlobMatcherTests
{
    [Theory]
    [InlineData("postgres*", "postgres-main", true)]
    [InlineData("*-db", "app-db", true)]
    [InlineData("*-db", "db", false)]
    [InlineData("redis", "Redis", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a.c", "abc", false)]
    public void Matches(string pattern, string input, bool expected) =>
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, input));
}
