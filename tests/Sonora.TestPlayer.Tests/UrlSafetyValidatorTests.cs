using Sonora.TestPlayer.Services;

namespace Sonora.TestPlayer.Tests;

public sealed class UrlSafetyValidatorTests
{
    [Theory]
    [InlineData("http://127.0.0.1/a.mp3")]
    [InlineData("http://127.255.255.254/a.mp3")]
    [InlineData("http://10.0.0.5/a.mp3")]
    [InlineData("http://10.255.255.255/a.mp3")]
    [InlineData("http://192.168.1.10/a.mp3")]
    [InlineData("http://172.16.0.1/a.mp3")]
    [InlineData("http://172.31.255.255/a.mp3")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://100.64.0.1/a.mp3")]
    [InlineData("http://0.0.0.0/a.mp3")]
    [InlineData("http://[::1]/a.mp3")]
    [InlineData("http://[fe80::1]/a.mp3")]
    [InlineData("http://[fd12:3456:789a::1]/a.mp3")]
    [InlineData("http://[::ffff:127.0.0.1]/a.mp3")]
    [InlineData("http://[::ffff:192.168.0.1]/a.mp3")]
    [InlineData("http://localhost/a.mp3")]
    [InlineData("http://LOCALHOST:8080/a.mp3")]
    [InlineData("http://music.localhost/a.mp3")]
    [InlineData("http://printer.local/a.mp3")]
    [InlineData("http://intranet/a.mp3")]
    public void Restricted_mode_blocks_local_and_private_hosts(string url)
    {
        var (ok, reason) = UrlSafetyValidator.Validate(new Uri(url), allowLocalDeveloperMode: false);

        Assert.False(ok);
        Assert.StartsWith("Blocked:", reason);
    }

    [Theory]
    [InlineData("https://example.com/a.mp3")]
    [InlineData("http://sub.example.org:8000/stream")]
    [InlineData("http://93.184.216.34/a.mp3")]
    [InlineData("http://172.32.0.1/a.mp3")]
    [InlineData("http://172.15.255.255/a.mp3")]
    [InlineData("http://[2606:4700:4700::1111]/a.mp3")]
    public void Restricted_mode_allows_public_hosts(string url)
    {
        var (ok, reason) = UrlSafetyValidator.Validate(new Uri(url), allowLocalDeveloperMode: false);

        Assert.True(ok, reason);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/a.mp3")]
    [InlineData("http://10.1.2.3/a.mp3")]
    [InlineData("http://192.168.0.20/a.mp3")]
    [InlineData("http://[::1]:5000/a.mp3")]
    [InlineData("http://localhost:8080/a.mp3")]
    public void Developer_override_allows_local_hosts_and_says_so(string url)
    {
        var (ok, reason) = UrlSafetyValidator.Validate(new Uri(url), allowLocalDeveloperMode: true);

        Assert.True(ok);
        Assert.Contains("developer override", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Developer_override_still_rejects_unsupported_schemes()
    {
        var (ok, reason) = UrlSafetyValidator.Validate(new Uri("ftp://example.com/a.mp3"), allowLocalDeveloperMode: true);

        Assert.False(ok);
        Assert.Contains("scheme", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Credentials_in_url_are_rejected_even_with_developer_override()
    {
        var (ok, _) = UrlSafetyValidator.Validate(new Uri("https://user:secret@example.com/a.mp3"), allowLocalDeveloperMode: true);

        Assert.False(ok);
    }

    [Fact]
    public void Relative_uri_is_rejected()
    {
        var (ok, _) = UrlSafetyValidator.Validate(new Uri("/a.mp3", UriKind.Relative), allowLocalDeveloperMode: false);

        Assert.False(ok);
    }

    [Fact]
    public void Null_uri_throws()
    {
        Assert.Throws<ArgumentNullException>(() => UrlSafetyValidator.Validate((Uri)null!, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("example.com/a.mp3")]
    public void String_overload_rejects_unparseable_input(string value)
    {
        var (ok, _) = UrlSafetyValidator.Validate(value, allowLocalDeveloperMode: false);

        Assert.False(ok);
    }

    [Fact]
    public void ModeFor_maps_flag_to_mode()
    {
        Assert.Equal(UrlSafetyMode.Restricted, UrlSafetyValidator.ModeFor(false));
        Assert.Equal(UrlSafetyMode.DeveloperAllowLocal, UrlSafetyValidator.ModeFor(true));
    }
}
