namespace MkvHippo.Core.Tests;

public class MkvmergeLocatorTests
{
    [Fact]
    public void PrefersTheCopyNextToTheApp()
    {
        var appDir = Path.Combine("C:", "apps", "hippo");
        var beside = Path.Combine(appDir, "mkvmerge.exe");
        var onPath = Path.Combine("C:", "tools", "mkvmerge.exe");

        var found = MkvmergeLocator.Locate(
            appDir, Path.Combine("C:", "tools"),
            p => p == beside || p == onPath, windows: true);

        Assert.Equal(beside, found);
    }

    [Fact]
    public void FallsBackToPathEntriesInOrder()
    {
        // Drive-letter-free dirs so the entries survive splitting on ':' off Windows too.
        var first = Path.Combine(Path.GetTempPath(), "one");
        var second = Path.Combine(Path.GetTempPath(), "two");
        var target = Path.Combine(second, "mkvmerge.exe");

        var found = MkvmergeLocator.Locate(
            Path.Combine(Path.GetTempPath(), "apps"),
            first + Path.PathSeparator + second,
            p => p == target, windows: true);

        Assert.Equal(target, found);
    }

    [Fact]
    public void FallsBackToTheDefaultInstallLocationOnWindows()
    {
        var found = MkvmergeLocator.Locate(
            Path.Combine("C:", "apps"), null,
            p => p == @"C:\Program Files\MKVToolNix\mkvmerge.exe", windows: true);

        Assert.Equal(@"C:\Program Files\MKVToolNix\mkvmerge.exe", found);
    }

    [Fact]
    public void ReturnsNullWhenNothingExists()
    {
        var found = MkvmergeLocator.Locate("/app", "/usr/bin:/usr/local/bin", _ => false, windows: false);
        Assert.Null(found);
    }

    [Fact]
    public void UsesUnsuffixedBinaryNameOffWindows()
    {
        var found = MkvmergeLocator.Locate("/app", "/usr/bin", p => p == "/usr/bin/mkvmerge", windows: false);
        Assert.Equal("/usr/bin/mkvmerge", found);
    }
}
