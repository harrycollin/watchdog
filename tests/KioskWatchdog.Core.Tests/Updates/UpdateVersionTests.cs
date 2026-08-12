using KioskWatchdog.Core.Updates;

namespace KioskWatchdog.Core.Tests.Updates;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.4.1", 1, 4, 1)]
    [InlineData("1.4.1", 1, 4, 1)]
    [InlineData("V2.0.0", 2, 0, 0)]
    [InlineData("1.2.3-beta", 1, 2, 3)]
    [InlineData("1.2.3+meta", 1, 2, 3)]
    public void TryParse_accepts_common_tags(string input, int major, int minor, int build)
    {
        Assert.True(UpdateVersion.TryParse(input, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void TryParse_rejects_invalid(string? input)
    {
        Assert.False(UpdateVersion.TryParse(input, out _));
    }

    [Fact]
    public void Normalize_drops_negative_build()
    {
        var normalized = UpdateVersion.Normalize(new Version(1, 2));
        Assert.Equal(new Version(1, 2, 0), normalized);
    }
}
