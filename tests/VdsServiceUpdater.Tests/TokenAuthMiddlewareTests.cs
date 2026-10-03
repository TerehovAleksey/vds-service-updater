using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VdsServiceUpdater.Options;
using VdsServiceUpdater.Security;

namespace VdsServiceUpdater.Tests;

public class TokenAuthMiddlewareTests
{
    private const string Token = "correct-horse-battery-staple";

    private static async Task<(int Status, bool NextCalled)> Run(string? configured, string path, string? header)
    {
        var o = new UpdaterOptions { Auth = new AuthOptions { Token = configured } };
        var called = false;
        var mw = new TokenAuthMiddleware(_ => { called = true; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(o), NullLogger<TokenAuthMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = path;
        if (header is not null) ctx.Request.Headers[TokenAuthMiddleware.HeaderName] = header;

        await mw.InvokeAsync(ctx);
        return (ctx.Response.StatusCode, called);
    }

    [Fact] public async Task Correct_token_passes() =>
        Assert.Equal((200, true), await Run(Token, "/api/v1/deploy", Token));

    [Fact] public async Task Wrong_token_is_401() =>
        Assert.Equal((401, false), await Run(Token, "/api/v1/deploy", "wrong"));

    [Fact] public async Task Missing_token_is_401() =>
        Assert.Equal((401, false), await Run(Token, "/api/v1/deploy", null));

    [Fact] public async Task Health_is_not_protected() =>
        Assert.Equal((200, true), await Run(Token, "/health", null));

    [Fact] public async Task No_configured_token_disables_protection() =>
        Assert.Equal((200, true), await Run(null, "/api/v1/deploy", null));
}
