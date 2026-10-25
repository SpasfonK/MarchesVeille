using System.Text.Json;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Ingestion;
using MarchesVeille.Core.Providers;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Cli;

/// <summary>Mode batch : MarchesVeille.exe --scrap --source=all|boamp|place|grandest|&lt;id&gt; --days=3 [--db=chemin] [--config=chemin]</summary>
public static class CliRunner
{
    public const string Usage = """
MarchesVeille - veille de marchés publics (mode batch)

Utilisation :
  MarchesVeille.exe --scrap [--source=all|boamp|place|grandest|<id>[,<id>...]] [--days=3] [--db=<marches.db>] [--config=<connectors.json>]
  MarchesVeille.exe --help

Codes de sortie : 0 = succès, 1 = au moins une source en erreur, 2 = arguments ou configuration invalides.
Sans argument, l'interface graphique démarre.
""";

    public static bool IsCliInvocation(string[] args) =>
        args.Any(a => a.Equals("--scrap", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("--help", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("-h", StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(
        string[] args, TextWriter output, string baseDir,
        Func<ConnectorsConfig, IReadOnlyList<IMarketplaceProvider>>? providerFactory = null,
        CancellationToken ct = default)
    {
        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine(Usage);
            return 0;
        }

        var sourceArg = GetArg(args, "--source") ?? "all";
        int days = 3;
        var daysArg = GetArg(args, "--days");
        if (daysArg != null && (!int.TryParse(daysArg, out days) || days < 1 || days > 90))
        {
            output.WriteLine("Argument --days invalide (entier entre 1 et 90).");
            return 2;
        }

        var dbPath = GetArg(args, "--db") ?? Path.Combine(baseDir, "marches.db");
        var cfgPath = GetArg(args, "--config") ?? Path.Combine(baseDir, "connectors.json");

        ConnectorsConfig cfg;
        try { cfg = ConnectorsConfig.LoadOrCreate(cfgPath); }
        catch (JsonException ex)
        {
            output.WriteLine($"connectors.json invalide ({cfgPath}) : {ex.Message}");
            return 2;
        }

        var db = new Database(dbPath);
        db.Migrate();
        var repo = new NoticeRepository(db);

        IReadOnlyList<IMarketplaceProvider> providers;
        try
        {
            providers = providerFactory != null
                ? ConnectorRegistry.Filter(providerFactory(cfg), sourceArg)
                : new ConnectorRegistry(cfg).Select(sourceArg);
        }
        catch (ArgumentException ex)
        {
            output.WriteLine(ex.Message);
            return 2;
        }

        output.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Synchronisation : sources={string.Join(",", providers.Select(p => p.SourceId))} jours={days} base={dbPath}");

        var service = new IngestionService(repo, new VnfTargetProfile(cfg.TaggingRules));
        var progress = new DelegateProgress<IngestionProgress>(p => output.WriteLine($"  [{p.SourceId}] {p.Message}"));
        var summary = await service.RunAsync(providers, new ScrapingOptions { Days = days }, progress, ct);

        output.WriteLine(summary.ToText());
        return summary.HasErrors ? 1 : 0;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return a[(name.Length + 1)..];
            if (a.Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                return args[i + 1];
        }
        return null;
    }
}
