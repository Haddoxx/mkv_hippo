using MkvHippo.Core.Mkv;

namespace MkvHippo.Core.Tests;

internal static class Fixtures
{
    public static string PathOf(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static string Json(string name) => File.ReadAllText(PathOf(name));

    public static MkvFileInfo Parse(string name, string filePath = "input.mkv") =>
        MkvIdentifier.Parse(filePath, Json(name));
}
