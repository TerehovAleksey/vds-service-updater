using Xunit;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Tests;

public class UpdaterOptionsValidatorTests
{
    private static UpdaterOptions Valid()
    {
        var o = new UpdaterOptions();
        o.Deploy.AllowedImagePrefixes = ["ghcr.io/org/"];
        o.Targets.Add(new TargetOptions { Name = "main", Type = TargetType.Compose, File = "/opt/stacks/main/compose.yml" });
        return o;
    }

    private static string? Failure(UpdaterOptions o) =>
        new UpdaterOptionsValidator().Validate(null, o).FailureMessage;

    [Fact]
    public void Valid_configuration_passes() => Assert.Null(Failure(Valid()));

    [Fact]
    public void Valid_token_and_networks_pass()
    {
        var o = Valid();
        o.Auth.Token = new string('x', 16);
        o.Proxy.KnownProxies.Add("172.18.0.1");
        o.Proxy.KnownNetworks.Add("172.16.0.0/12");
        Assert.Null(Failure(o));
    }

    [Fact]
    public void Invalid_configurations_are_rejected_with_a_helpful_message()
    {
        var cases = new Dictionary<string, (Action<UpdaterOptions> Break, string Expected)>
        {
            ["no prefixes"] = (o => o.Deploy.AllowedImagePrefixes.Clear(), "AllowedImagePrefixes"),
            ["blank prefix"] = (o => o.Deploy.AllowedImagePrefixes.Add(" "), "AllowedImagePrefixes"),
            ["no targets"] = (o => o.Targets.Clear(), "Targets"),
            ["timeout too small"] = (o => o.Deploy.WaitTimeoutSeconds = 5, "WaitTimeoutSeconds"),
            ["short token"] = (o => o.Auth.Token = "short", "Token"),
            ["token with edge spaces"] = (o => o.Auth.Token = " " + new string('x', 20), "Token"),
            ["rate limit zero"] = (o => o.Security.RequestsPerMinute = 0, "RequestsPerMinute"),
            ["body limit tiny"] = (o => o.Security.MaxBodyBytes = 10, "MaxBodyBytes"),
            ["bad proxy ip"] = (o => o.Proxy.KnownProxies.Add("not-an-ip"), "KnownProxies"),
            ["bad network"] = (o => o.Proxy.KnownNetworks.Add("10.0.0.0/99"), "KnownNetworks"),
            ["relative backup dir"] = (o => o.Deploy.BackupDirectory = "backups", "BackupDirectory"),
            ["duplicate name"] = (o => o.Targets.Add(new TargetOptions { Name = "MAIN", File = "/opt/other.yml" }), "повторяется"),
            ["duplicate file"] = (o => o.Targets.Add(new TargetOptions { Name = "other", File = "/opt/stacks/main/compose.yml" }), "нескольких целях"),
            ["relative file"] = (o => o.Targets[0].File = "compose.yml", "абсолютным"),
            ["stack without name"] = (o => o.Targets[0].Type = TargetType.Stack, "StackName"),
            ["path traversal in name"] = (o => o.Targets[0].Name = "../x", "Name допускает"),
            ["blank protected mask"] = (o => o.Targets[0].Protected.Add(""), "Protected"),
        };

        foreach (var (name, (breakIt, expected)) in cases)
        {
            var o = Valid();
            breakIt(o);
            var failure = Failure(o);
            Assert.True(failure is not null && failure.Contains(expected), $"{name}: ожидалась ошибка с '{expected}', получено: {failure ?? "успех"}");
        }
    }
}
