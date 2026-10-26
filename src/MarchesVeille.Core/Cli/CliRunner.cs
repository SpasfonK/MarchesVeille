using System.Text.Json;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Ingestion;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Providers;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Cli;

/// <summary>Mode batch : synchronisation des avis puis collecte anonyme des DCE (lancement mensuel typique).</summary>
public static class CliRunner
{
    public const string Usage = """
MarchesVeille - veille de marchés publics (mode batch)

Utilisation :
  MarchesVeille.exe --scrap [--source=all|boamp|place|grandest|<id>[,<id>...]] [--days=3]
                    [--dce] [--dce-all] [--dce-debug] [--dce-days=45] [--dce-max=300] [--dce-retry]
  MarchesVeille.exe --dce-only [--dce-all] [--dce-debug] ...      (collecte des DCE sans synchroniser les avis)
  Options communes : [--db=<marches.db>] [--config=<connectors.json>]
  MarchesVeille.exe --help

  --dce          après la synchronisation, télécharge anonymement les DCE des avis retenus par les profils de collecte actifs
  --dce-all      ignore les profils : télécharge tous les DCE candidats
  --dce-debug    sauvegarde les pages HTML de chaque étape dans DCE/_diagnostic (réglage des sélecteurs)
  --dce-days     ne considère que les avis publiés depuis N jours (défaut : connectors.json, 45)
  --dce-max      nombre maximal de DCE par exécution (défaut : connectors.json, 300)
  --dce-retry    réessaie aussi les DCE « accès restreint » ou « non pris en charge »

Exemple mensuel : MarchesVeille.exe --scrap --days=31 --dce

Codes de sortie : 0 = succès, 1 = au moins une source ou un téléchargement en erreur, 2 = arguments ou configuration invalides.
Sans argument, l'interface graphique démarre.
""";

    private static readonly string[] Flags = { "--scrap", "--dce", "--dce-only", "--help", "-h" };

    public static bool IsCliInvocation(string[] args) =>
        args.Any(a => Flags.Any(f => a.Equals(f, StringComparison.OrdinalIgnoreCase)));

    public static async Task<int> RunAsync(
        string[] args, TextWriter output, string baseDir,
        Func<ConnectorsConfig, IReadOnlyList<IMarketplaceProvider>>? providerFactory = null,
        Func<ConnectorsConfig, IReadOnlyList<IDceDownloadProvider>>? dceProviderFactory = null,
        CancellationToken ct = default)
    {
        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            output.WriteLine(Usage);
            return 0;
        }

        bool dceOnly = HasFlag(args, "--dce-only");
        bool doSync = !dceOnly;
        bool doDce = dceOnly || HasFlag(args, "--dce");

        var sourceArg = GetArg(args, "--source") ?? "all";
        int days = 3;
        var daysArg = GetArg(args, "--days");
        if (daysArg != null && (!int.TryParse(daysArg, out days) || days < 1 || days > 90))
        {
            output.WriteLine("Argument --days invalide (entier entre 1 et 90).");
            return 2;
        }
        int? dceDays = null, dceMax = null;
        if (GetArg(args, "--dce-days") is { } dd)
        {
            if (!int.TryParse(dd, out var v) || v < 1 || v > 3650) { output.WriteLine("Argument --dce-days invalide (1 à 3650)."); return 2; }
            dceDays = v;
        }
        if (GetArg(args, "--dce-max") is { } dm)
        {
            if (!int.TryParse(dm, out var v) || v < 1) { output.WriteLine("Argument --dce-max invalide."); return 2; }
            dceMax = v;
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
        var archives = new ArchiveRepository(db);
        int exit = 0;

        if (doSync)
        {
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
            var service = new IngestionService(repo, new VnfTargetProfile(cfg.TaggingRules), new NoticeClassifier(cfg.Classification));
            var progress = new DelegateProgress<IngestionProgress>(p => output.WriteLine($"  [{p.SourceId}] {p.Message}"));
            var summary = await service.RunAsync(providers, new ScrapingOptions { Days = days }, progress, ct);
            output.WriteLine(summary.ToText());
            if (summary.HasErrors) exit = 1;
        }

        if (doDce)
        {
            IReadOnlyList<IDceDownloadProvider> dceProviders = dceProviderFactory != null
                ? dceProviderFactory(cfg)
                : new ConnectorRegistry(cfg).DceProviders;

            var profiles = HasFlag(args, "--dce-all")
                ? new List<DceProfile> { new() { Name = "Tout", Enabled = true } }
                : archives.ListProfiles().ToList();

            output.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Collecte des DCE : profils actifs={profiles.Count(p => p.Enabled)} connecteurs={dceProviders.Count}");
            var collector = new DceCollector(archives, dceProviders, cfg.Dce, baseDir);
            var dceProgress = new DelegateProgress<DceProgress>(p => output.WriteLine($"  [{p.Done}/{p.Total}] {p.Message}"));
            var res = await collector.RunAsync(profiles, new DceRunOptions
            {
                PublishedWithinDays = dceDays,
                MaxPerRun = dceMax,
                RetryBlocked = HasFlag(args, "--dce-retry"),
                Debug = HasFlag(args, "--dce-debug")
            }, dceProgress, ct);
            output.WriteLine(res.ToText());
            if (res.Failed > 0) exit = 1;
        }

        return exit;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

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
