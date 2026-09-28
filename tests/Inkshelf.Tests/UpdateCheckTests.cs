namespace Inkshelf.Tests;

public class UpdateCheckTests
{
    [Theory]
    // A newer release is the only case that renders anything.
    [InlineData("1.0.0", "v1.0.1", "1.0.1")]
    [InlineData("1.0.0", "1.0.1", "1.0.1")]
    // A PR image is stamped "<version>+pr-34.a1b2c3d"; it compares as its base version.
    [InlineData("1.0.0+pr-34.a1b2c3d", "v1.0.1", "1.0.1")]
    // Up to date, and ahead of the latest release (a local build), both stay silent.
    [InlineData("1.0.0", "v1.0.0", null)]
    [InlineData("1.1.0", "v1.0.1", null)]
    // Anything that will not parse shows nothing rather than guessing.
    [InlineData("1.0.0", "nightly", null)]
    [InlineData("1.0.0", "", null)]
    [InlineData("1.0.0", "v1.1.0-rc1", null)]
    [InlineData("not-a-version", "v1.0.1", null)]
    public void NewerThan_reports_only_a_parseable_newer_release(string local, string remote, string? expected)
        => Assert.Equal(expected, UpdateCheck.NewerThan(local, remote));
}
