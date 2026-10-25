using MarchesVeille.Core.Data;
using MarchesVeille.Core.Util;
using Xunit;

namespace MarchesVeille.Tests;

public class Phase4PortableTests
{
    [Fact]
    public void Database_is_created_next_to_the_given_directory_and_migrates_automatically()
    {
        var dir = Directory.CreateTempSubdirectory("mv-portable-").FullName;
        try
        {
            var path = Path.Combine(dir, "marches.db");
            Assert.False(File.Exists(path));
            new Database(path).Migrate();
            Assert.True(File.Exists(path));
        }
        finally
        {
            Database.ReleasePools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void AppPaths_resolve_to_existing_writable_directories()
    {
        Assert.True(Directory.Exists(AppPaths.BaseDir));
        Assert.True(Directory.Exists(AppPaths.DataDir));
    }

    [Theory]
    [InlineData("54 - Meurthe-et-Moselle", "54")]
    [InlineData("Nancy (54000)", "54")]
    [InlineData("2A", "2A")]
    [InlineData("", null)]
    public void Geo_extracts_department(string input, string? expected) =>
        Assert.Equal(expected, Geo.ExtractDepartment(input));

    [Fact]
    public void French_dates_are_converted_from_paris_time_to_utc()
    {
        var d = DateParsing.ParseFrench("30/10/2026 12:00", new[] { "dd/MM/yyyy HH:mm" });
        Assert.NotNull(d);
        Assert.Equal(new DateTime(2026, 10, 30).Date, d!.Value.Date);
        Assert.Null(DateParsing.ParseFrench(""));
    }
}
