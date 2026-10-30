using PositionTracker.Api.Auth;

namespace PositionTracker.Api.Tests;

public class TokenServiceTests
{
    [Fact]
    public void Issued_token_is_32_bytes_base64url_and_resolves_to_its_participant()
    {
        var tokens = new TokenService();
        var raw = tokens.Issue("e1", "p1");

        Assert.Matches("^[A-Za-z0-9_-]{43}$", raw);
        Assert.Equal(("e1", "p1"), tokens.Resolve(raw));
        Assert.Null(tokens.Resolve(raw + "x"));
        Assert.Null(tokens.Resolve(""));
    }

    [Fact]
    public void Reissue_invalidates_the_previous_token_and_revoke_invalidates_the_current_one()
    {
        var tokens = new TokenService();
        var first = tokens.Issue("e1", "p1");
        var second = tokens.Issue("e1", "p1");
        var other = tokens.Issue("e1", "p2");

        Assert.NotEqual(first, second);
        Assert.Null(tokens.Resolve(first));
        Assert.Equal(("e1", "p1"), tokens.Resolve(second));

        tokens.Revoke("e1", "p1");

        Assert.Null(tokens.Resolve(second));
        Assert.Equal(("e1", "p2"), tokens.Resolve(other));
    }
}
