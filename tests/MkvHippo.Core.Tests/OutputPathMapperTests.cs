namespace MkvHippo.Core.Tests;

public class OutputPathMapperTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "hippo-in");
    private static readonly string Out = Path.Combine(Path.GetTempPath(), "hippo-out");

    [Fact]
    public void MapsTopLevelFile()
    {
        var mapped = OutputPathMapper.Map(Root, Out, Path.Combine(Root, "movie.mkv"));
        Assert.Equal(Path.Combine(Out, "movie.mkv"), mapped);
    }

    [Fact]
    public void MapsNestedFileMirroringTheSubpath()
    {
        var input = Path.Combine(Root, "shows", "s01", "e01.mkv");
        var mapped = OutputPathMapper.Map(Root, Out, input);
        Assert.Equal(Path.Combine(Out, "shows", "s01", "e01.mkv"), mapped);
    }

    [Fact]
    public void RefusesFileOutsideTheInputRoot()
    {
        Assert.Throws<ArgumentException>(
            () => OutputPathMapper.Map(Root, Out, Path.Combine(Path.GetTempPath(), "elsewhere.mkv")));
    }

    [Fact]
    public void RefusesOutputRootInsideInputRoot()
    {
        Assert.Throws<ArgumentException>(
            () => OutputPathMapper.EnsureValidRoots(Root, Path.Combine(Root, "out")));
    }

    [Fact]
    public void RefusesOutputRootEqualToInputRoot()
    {
        Assert.Throws<ArgumentException>(
            () => OutputPathMapper.EnsureValidRoots(Root, Root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AllowsSiblingOutputRoot()
    {
        OutputPathMapper.EnsureValidRoots(Root, Out);
    }

    [Fact]
    public void AllowsSiblingWithSharedNamePrefix()
    {
        // "hippo-in-hippo" starts with "hippo-in" as a string but is not inside it.
        OutputPathMapper.EnsureValidRoots(Root, Root + "-hippo");
    }

    [Fact]
    public void DefaultOutputRootIsSiblingWithHippoSuffix()
    {
        Assert.Equal(Root + "-hippo", OutputPathMapper.DefaultOutputRoot(Root + Path.DirectorySeparatorChar));
    }
}
