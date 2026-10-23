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

    // 這功能上線前存的明文照原樣用，舊 webhook 的簽章不會壞
    [Fact]
    public void Legacy_plaintext_passes_through() => Assert.Equal("old-secret", _secrets.Unprotect("old-secret"));

    [Fact]
    public void No_secret_stays_null()
    {
        Assert.Null(_secrets.Protect(null));
        Assert.Null(_secrets.Protect(""));
        Assert.Null(_secrets.Unprotect(null));
    }
}
