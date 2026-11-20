using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Posters;
using Jellyfin.Plugin.ArrCalendar.Security;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

public class ExpiringCacheTests
{
    [Fact]
    public async Task GetOrAdd_LoadsOncePerLifetimeAndSharesTheLoad()
    {
        var time = new ManualTime();
        var cache = new ExpiringCache<int>(10, time);
        var loads = 0;
        var gate = new TaskCompletionSource<int>();

        var first = cache.GetOrAdd("k", TimeSpan.FromMinutes(1), () => { loads++; return gate.Task; });
        var second = cache.GetOrAdd("k", TimeSpan.FromMinutes(1), () => { loads++; return Task.FromResult(9); });
        gate.SetResult(5);

        Assert.Same(first, second);
        Assert.Equal(5, await second);
        Assert.Equal(1, loads);

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(9, await cache.GetOrAdd("k", TimeSpan.FromMinutes(1), () => { loads++; return Task.FromResult(9); }));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task GetOrAdd_ForgetsAFailedLoad()
    {
        var cache = new ExpiringCache<int>(10, new ManualTime());

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrAdd("k", TimeSpan.FromMinutes(5), () => Task.FromException<int>(new InvalidOperationException())));

        Assert.Equal(0, cache.Count);
        Assert.Equal(3, await cache.GetOrAdd("k", TimeSpan.FromMinutes(5), () => Task.FromResult(3)));
    }

    [Fact]
    public async Task GetOrAdd_ForgetsALoadThatFailsLater()
    {
        var cache = new ExpiringCache<int>(10, new ManualTime());
        var gate = new TaskCompletionSource<int>();

        var load = cache.GetOrAdd("k", TimeSpan.FromMinutes(5), () => gate.Task);
        gate.SetException(new IOException());
        await Assert.ThrowsAsync<IOException>(() => load);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task GetOrAdd_LetsTheResultChooseItsLifetime()
    {
        var time = new ManualTime();
        var cache = new ExpiringCache<string?>(10, time);
        var loads = 0;

        Func<Task<string?>> missing = () => { loads++; return Task.FromResult<string?>(null); };
        await cache.GetOrAdd("k", TimeSpan.FromMinutes(30), missing, value => value is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30));
        time.Advance(TimeSpan.FromMinutes(6));
        await cache.GetOrAdd("k", TimeSpan.FromMinutes(30), missing, value => value is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30));

        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task GetOrAdd_StaysWithinItsCapacity()
    {
        var time = new ManualTime();
        var cache = new ExpiringCache<int>(3, time);
        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await cache.GetOrAdd("k" + i, TimeSpan.FromMinutes(1), () => Task.FromResult(i));
        }

        Assert.Equal(3, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}

public class ImageSignerTests
{
    [Fact]
    public void Verify_AcceptsOnlyThisProcessesSignatureForExactlyThatArtwork()
    {
        var signer = new ImageSigner();
        var signature = signer.Sign(ArrKind.Sonarr, 12);

        Assert.Equal(ImageSigner.SignatureLength, signature.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", signature);
        Assert.True(signer.Verify(ArrKind.Sonarr, 12, signature));
        Assert.False(signer.Verify(ArrKind.Sonarr, 13, signature));
        Assert.False(signer.Verify(ArrKind.Radarr, 12, signature));
        Assert.False(signer.Verify(ArrKind.Sonarr, 12, signature[..^1] + (signature[^1] == 'A' ? "B" : "A")));
        Assert.False(signer.Verify(ArrKind.Sonarr, 12, signature + "x"));
        Assert.False(signer.Verify(ArrKind.Sonarr, 12, null));
        Assert.False(new ImageSigner().Verify(ArrKind.Sonarr, 12, signature));
    }

    [Fact]
    public void Sign_IsStableForOneKey()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(new ImageSigner(key).Sign(ArrKind.Radarr, 5), new ImageSigner(key).Sign(ArrKind.Radarr, 5));
        Assert.Throws<ArgumentException>(() => new ImageSigner(new byte[8]));
    }
}

public class LogSanitizerTests
{
    [Theory]
    [InlineData(null, "(empty)")]
    [InlineData("", "(empty)")]
    [InlineData("/api/v3/calendar", "/api/v3/calendar")]
    [InlineData("line\r\nforged entry", "line??forged entry")]
    [InlineData("bidi\u202Eoverride", "bidi?override")]
    [InlineData("sep\u2028arator", "sep?arator")]
    [InlineData("emoji 🎬 kept", "emoji 🎬 kept")]
    public void ForLog_NeutralizesWhatCouldForgeALine(string? value, string expected)
    {
        Assert.Equal(expected, LogSanitizer.ForLog(value));
    }

    [Fact]
    public void ForLog_BoundsTheLength()
    {
        Assert.Equal(259, LogSanitizer.ForLog(new string('a', 1000)).Length);
    }
}

public class ArrCalendarRateLimiterTests
{
    [Fact]
    public void EachBudgetIsSeparateAndCalendarAndLibraryArePerUser()
    {
        using var limiter = new ArrCalendarRateLimiter(2, 1, 1, 1, TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquireCalendar("alice", out _));
        Assert.True(limiter.TryAcquireCalendar("alice", out _));
        Assert.False(limiter.TryAcquireCalendar("alice", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(limiter.TryAcquireCalendar("bob", out _));

        Assert.True(limiter.TryAcquireLibrary("alice", out _));
        Assert.False(limiter.TryAcquireLibrary("alice", out _));
        Assert.True(limiter.TryAcquireLibrary("bob", out _));

        Assert.True(limiter.TryAcquireImageFetch(out _));
        Assert.False(limiter.TryAcquireImageFetch(out _));
        Assert.True(limiter.TryAcquireAdmin(out _));
        Assert.False(limiter.TryAcquireAdmin(out _));
    }

    [Fact]
    public void Defaults_AreGenerousForPeopleAndTightForTheAdministrator()
    {
        using var limiter = new ArrCalendarRateLimiter();
        for (var i = 0; i < ArrCalendarRateLimiter.DefaultCalendarPermitLimit; i++)
        {
            Assert.True(limiter.TryAcquireCalendar("alice", out _));
        }

        Assert.False(limiter.TryAcquireCalendar("alice", out _));
        Assert.Equal(30, ArrCalendarRateLimiter.DefaultAdminPermitLimit);
    }
}
