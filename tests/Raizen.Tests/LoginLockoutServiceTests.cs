using Raizen.Server.Core.Services;

namespace Raizen.Tests;

public sealed class LoginLockoutServiceTests
{
    [Fact]
    public void MaxAttempts_IsFive()
    {
        Assert.Equal(5, LoginLockoutService.MaxAttempts);
    }

    [Fact]
    public void IsEntryLocked_BelowMaxAttempts_ReturnsFalse()
    {
        var now = DateTimeOffset.UtcNow;

        var locked = LoginLockoutService.IsEntryLocked(
            (LoginLockoutService.MaxAttempts - 1, now.AddMinutes(15)),
            now);

        Assert.False(locked);
    }

    [Fact]
    public void IsEntryLocked_AtMaxAttemptsWithinWindow_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;

        var locked = LoginLockoutService.IsEntryLocked(
            (LoginLockoutService.MaxAttempts, now.AddMinutes(15)),
            now);

        Assert.True(locked);
    }

    [Fact]
    public void IsEntryLocked_AtMaxAttemptsAfterWindow_ReturnsFalse()
    {
        var now = DateTimeOffset.UtcNow;

        var locked = LoginLockoutService.IsEntryLocked(
            (LoginLockoutService.MaxAttempts, now.AddSeconds(-1)),
            now);

        Assert.False(locked);
    }

    [Fact]
    public void NextFailure_ExpiredWindow_ResetsCount()
    {
        var now = DateTimeOffset.UtcNow;

        var entry = LoginLockoutService.NextFailure(
            (LoginLockoutService.MaxAttempts, now.AddSeconds(-1)),
            now);

        Assert.Equal(1, entry.Count);
        Assert.True(entry.Until > now);
    }

    [Fact]
    public void NextFailure_ActiveWindow_IncrementsCount()
    {
        var now = DateTimeOffset.UtcNow;

        var entry = LoginLockoutService.NextFailure((2, now.AddMinutes(1)), now);

        Assert.Equal(3, entry.Count);
        Assert.True(entry.Until > now);
    }
}
