using Microsoft.AspNetCore.DataProtection;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.UnitTests;

public class WebhookSecretProtectorTests
{
    private readonly WebhookSecretProtector _secrets = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Stored_value_is_not_the_secret_and_round_trips()
    {
        var stored = _secrets.Protect("s3cret");

        Assert.NotNull(stored);
        Assert.DoesNotContain("s3cret", stored);
        Assert.Equal("s3cret", _secrets.Unprotect(stored));
    }

    // 存的值一定是加密格式；不是的話不拿來簽，寧可這個 webhook 送不出去
    [Fact]
    public void Value_without_the_prefix_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => _secrets.Unprotect("plain-secret"));

    [Fact]
    public void No_secret_stays_null()
    {
        Assert.Null(_secrets.Protect(null));
        Assert.Null(_secrets.Protect(""));
        Assert.Null(_secrets.Unprotect(null));
    }
}
