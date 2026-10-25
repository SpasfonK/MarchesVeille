namespace MarchesVeille.Core.Util;

public static class AppPaths
{
    /// <summary>Dossier de l'exécutable (et non celui de dotnet.exe en mode développement).</summary>
    public static string BaseDir
    {
        get
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p) &&
                !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(p) ?? AppContext.BaseDirectory;
            return AppContext.BaseDirectory;
        }
    }

    /// <summary>Dossier des données : à côté de l'exe, sinon %LOCALAPPDATA%\MarchesVeille si non inscriptible.</summary>
    public static string DataDir
    {
        get
        {
            var dir = BaseDir;
            if (CanWrite(dir)) return dir;
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarchesVeille");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".w{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
