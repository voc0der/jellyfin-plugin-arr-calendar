using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Configuration;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

public class ArrConnectionTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("http://sonarr:8989", "http://sonarr:8989/api/v3/")]
    [InlineData("http://sonarr:8989/", "http://sonarr:8989/api/v3/")]
    [InlineData("  http://192.168.1.20:8989  ", "http://192.168.1.20:8989/api/v3/")]
    [InlineData("https://media.example/sonarr", "https://media.example/sonarr/api/v3/")]
    [InlineData("https://media.example/sonarr/", "https://media.example/sonarr/api/v3/")]
    [InlineData("http://sonarr:8989/api/v3", "http://sonarr:8989/api/v3/")]
    [InlineData("http://sonarr:8989/api", "http://sonarr:8989/api/v3/")]
    [InlineData("http://host/sonarr/api/v3/", "http://host/sonarr/api/v3/")]
    [InlineData("HTTP://SONARR:8989/Api/V3", "http://sonarr:8989/api/v3/")]
    public void ParseApiRoot_ResolvesEveryUsualSpellingToTheV3Root(string url, string expected)
    {
        Assert.Equal(expected, ArrConnection.ParseApiRoot(ArrKind.Sonarr, url).AbsoluteUri);
    }

    [Theory]
    [InlineData(null, "address is not set")]
    [InlineData("  ", "address is not set")]
    [InlineData("sonarr:8989", "starting with http")]
    [InlineData("192.168.1.20:8989", "starting with http")]
    [InlineData("ftp://sonarr", "starting with http")]
    [InlineData("file:///etc/passwd", "starting with http")]
    [InlineData("http://user:pass@sonarr:8989", "user name or password")]
    [InlineData("http://sonarr:8989/?apikey=1", "? or #")]
    [InlineData("http://sonarr:8989/#top", "? or #")]
    public void ParseApiRoot_RefusesAddressesThatCannotBeUsed(string? url, string message)
    {
        var error = Assert.Throws<ArrConfigurationException>(() => ArrConnection.ParseApiRoot(ArrKind.Sonarr, url));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseApiRoot_SuggestsEachServicesOwnPort()
    {
        Assert.Contains(":8989", Assert.Throws<ArrConfigurationException>(() => ArrConnection.ParseApiRoot(ArrKind.Sonarr, "nope")).Message, StringComparison.Ordinal);
        Assert.Contains(":7878", Assert.Throws<ArrConfigurationException>(() => ArrConnection.ParseApiRoot(ArrKind.Radarr, "nope")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_NeedsAKey(string? key)
    {
        var error = Assert.Throws<ArrConfigurationException>(() => ArrConnection.Create(ArrKind.Radarr, "http://radarr:7878", key));
        Assert.Contains("Radarr's API key is not set", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc def")]
    [InlineData("abc\r\nX-Injected: 1")]
    [InlineData("ключ")]
    public void Create_RefusesKeysThatCouldCorruptAHeader(string key)
    {
        var error = Assert.Throws<ArrConfigurationException>(() => ArrConnection.Create(ArrKind.Sonarr, "http://sonarr:8989", key));
        Assert.Contains("one line of letters and numbers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_TrimsTheKey()
    {
        var connection = ArrConnection.Create(ArrKind.Sonarr, "http://sonarr:8989", "  " + Key + "\n");
        Assert.Equal(Key, connection.ApiKey);
    }

    [Fact]
    public void Build_StaysUnderTheApiRootAndEscapesQueryValues()
    {
        var connection = ArrConnection.Create(ArrKind.Sonarr, "http://sonarr:8989/base", Key);

        var uri = connection.Build("calendar", [new("start", "2026-11-01T00:00:00Z"), new("odd", "a&b=c d")]);

        Assert.Equal("http://sonarr:8989/base/api/v3/calendar?start=2026-11-01T00%3A00%3A00Z&odd=a%26b%3Dc%20d", uri.AbsoluteUri);
        Assert.Equal("http://sonarr:8989/base/api/v3/system/status", connection.Build("/system/status").AbsoluteUri);
        Assert.Equal("http://sonarr:8989/base/api/v3/queue/details", connection.Build("queue/details", []).AbsoluteUri);
    }

    [Theory]
    [InlineData("../../config")]
    [InlineData("../v1/anything")]
    [InlineData("mediacover/../../../MediaCover/1/poster.jpg")]
    [InlineData("http://evil.example/steal")]
    [InlineData("mediacover/%2e%2e/%2e%2e/%2e%2e/config")]
    public void Build_RefusesAnythingOutsideTheApiRoot(string path)
    {
        var connection = ArrConnection.Create(ArrKind.Sonarr, "http://sonarr:8989", Key);
        Assert.Throws<ArrConfigurationException>(() => connection.Build(path));
    }

    [Fact]
    public void Fingerprint_ChangesWithTheAddressOrKeyButNeverContainsTheKey()
    {
        var one = ArrConnection.Create(ArrKind.Sonarr, "http://sonarr:8989", Key);
        var same = ArrConnection.Create(ArrKind.Sonarr, "http://sonarr:8989/", Key);
        var otherKey = ArrConnection.Create(ArrKind.Sonarr, "http://sonarr:8989", "fedcba9876543210fedcba9876543210");
        var otherHost = ArrConnection.Create(ArrKind.Sonarr, "http://sonarr2:8989", Key);

        Assert.Equal(one.Fingerprint, same.Fingerprint);
        Assert.NotEqual(one.Fingerprint, otherKey.Fingerprint);
        Assert.NotEqual(one.Fingerprint, otherHost.Fingerprint);
        Assert.DoesNotContain(Key, one.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, one.ToString(), StringComparison.Ordinal);
        Assert.Equal("Sonarr at http://sonarr:8989", one.ToString());
    }

    [Fact]
    public void KindNames_RoundTrip()
    {
        foreach (var kind in new[] { ArrKind.Sonarr, ArrKind.Radarr })
        {
            Assert.True(ArrKindNames.TryParse(kind.Id(), out var parsed));
            Assert.Equal(kind, parsed);
        }

        Assert.False(ArrKindNames.TryParse("Sonarr", out _));
        Assert.False(ArrKindNames.TryParse("lidarr", out _));
        Assert.False(ArrKindNames.TryParse(null, out _));
    }
}

public class ArrSecretSourceTests
{
    [Fact]
    public void ResolveApiKey_PrefersTheEnvironmentOverStoredKeys()
    {
        var environment = new Dictionary<string, string?> { [ArrSecretSource.SonarrApiKeyVariable] = " from-env " };
        var source = new ArrSecretSource(name => environment.GetValueOrDefault(name), _ => throw new IOException());
        var config = new PluginConfiguration { SonarrApiKey = "stored", RadarrApiKey = "stored-radarr" };

        Assert.Equal("from-env", source.ResolveApiKey(ArrKind.Sonarr, config));
        Assert.Equal("stored-radarr", source.ResolveApiKey(ArrKind.Radarr, config));
        Assert.Equal("from-env", source.GetEnvironmentApiKey(ArrKind.Sonarr));
        Assert.Null(source.GetEnvironmentApiKey(ArrKind.Radarr));
    }

    [Fact]
    public void GetEnvironmentApiKey_PrefersTheFileAndFailsClosedWhenItIsUnreadable()
    {
        var environment = new Dictionary<string, string?>
        {
            [ArrSecretSource.RadarrApiKeyFileVariable] = "/run/secrets/radarr",
            [ArrSecretSource.RadarrApiKeyVariable] = "inline",
            [ArrSecretSource.SonarrApiKeyFileVariable] = "/missing"
        };
        var source = new ArrSecretSource(
            name => environment.GetValueOrDefault(name),
            path => path == "/run/secrets/radarr" ? "from-file\n" : throw new FileNotFoundException());

        Assert.Equal("from-file", source.GetEnvironmentApiKey(ArrKind.Radarr));
        Assert.Null(source.GetEnvironmentApiKey(ArrKind.Sonarr));
    }

    [Fact]
    public void IsConfigured_NeedsBothAnAddressAndAKey()
    {
        var source = new ArrSecretSource(_ => null, _ => string.Empty);

        Assert.False(source.IsConfigured(ArrKind.Sonarr, new PluginConfiguration()));
        Assert.False(source.IsConfigured(ArrKind.Sonarr, new PluginConfiguration { SonarrUrl = "http://sonarr:8989" }));
        Assert.False(source.IsConfigured(ArrKind.Sonarr, new PluginConfiguration { SonarrApiKey = "key" }));
        Assert.False(source.IsConfigured(ArrKind.Sonarr, new PluginConfiguration { SonarrUrl = "http://sonarr:8989", SonarrApiKey = "   " }));
        Assert.True(source.IsConfigured(ArrKind.Sonarr, new PluginConfiguration { SonarrUrl = "http://sonarr:8989", SonarrApiKey = "key" }));
    }

    [Fact]
    public void VariableNames_AreNotLoggedByJellyfin()
    {
        // Jellyfin logs every environment variable starting with these prefixes, values included.
        foreach (var name in new[] { ArrSecretSource.SonarrApiKeyVariable, ArrSecretSource.SonarrApiKeyFileVariable, ArrSecretSource.RadarrApiKeyVariable, ArrSecretSource.RadarrApiKeyFileVariable })
        {
            Assert.DoesNotMatch("^(JELLYFIN_|DOTNET_|ASPNETCORE_)", name);
        }
    }
}
