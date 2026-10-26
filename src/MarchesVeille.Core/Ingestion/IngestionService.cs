using System.Diagnostics;
using System.Text;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Providers;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Ingestion;

public sealed record IngestionProgress(string SourceId, string Message, int SourcesDone, int SourcesTotal);

public sealed class SourceSummary
{
    public string SourceId { get; set; } = "";
    public int Fetched { get; set; }
    public int Inserted { get; set; }
    public int Duplicates { get; set; }
    public string? Error { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public sealed class IngestionSummary
{
    public List<SourceSummary> Sources { get; } = new();
    public bool HasErrors => Sources.Any(s => s.Error != null);
    public int TotalInserted => Sources.Sum(s => s.Inserted);
    public int TotalDuplicates => Sources.Sum(s => s.Duplicates);
    public int TotalFetched => Sources.Sum(s => s.Fetched);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Récapitulatif d'ingestion :");
        foreach (var s in Sources)
        {
            sb.Append($"  {s.SourceId} : récupérés={s.Fetched} nouveaux={s.Inserted} doublons={s.Duplicates} ({s.Elapsed.TotalSeconds:F1} s)");
            if (s.Error != null) sb.Append($" ERREUR : {s.Error}");
            sb.AppendLine();
        }
        sb.Append($"  TOTAL : récupérés={TotalFetched} nouveaux={TotalInserted} doublons={TotalDuplicates}");
        return sb.ToString();
    }

    public string ToOneLine() =>
        $"{TotalInserted} nouveaux avis, {TotalDuplicates} doublons" + (HasErrors ? $", {Sources.Count(s => s.Error != null)} source(s) en erreur" : "");
}

public sealed class IngestionService
{
    private readonly NoticeRepository _repo;
    private readonly VnfTargetProfile _tagger;
    private readonly NoticeClassifier? _classifier;

    public IngestionService(NoticeRepository repo, VnfTargetProfile tagger, NoticeClassifier? classifier = null)
    {
        _repo = repo;
        _tagger = tagger;
        _classifier = classifier;
    }

    public async Task<IngestionSummary> RunAsync(
        IReadOnlyList<IMarketplaceProvider> providers, ScrapingOptions options,
        IProgress<IngestionProgress>? progress, CancellationToken ct)
    {
        var summary = new IngestionSummary();
        int done = 0;

        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            var s = new SourceSummary { SourceId = provider.SourceId };
            var sw = Stopwatch.StartNew();
            progress?.Report(new IngestionProgress(provider.SourceId, "Démarrage…", done, providers.Count));

            try
            {
                var local = new ScrapingOptions
                {
                    Days = options.Days,
                    MaxPages = options.MaxPages,
                    Today = options.Today,
                    Progress = new DelegateProgress<string>(m =>
                    {
                        options.Progress?.Report(m);
                        progress?.Report(new IngestionProgress(provider.SourceId, m, done, providers.Count));
                    })
                };

                var fetched = await provider.FetchAsync(local, ct);
                s.Fetched = fetched.Count;

                var unique = new Dictionary<string, Notice>();
                int intraBatchDuplicates = 0;
                foreach (var n in fetched)
                {
                    if (string.IsNullOrWhiteSpace(n.SourceId)) n.SourceId = provider.SourceId;
                    if (string.IsNullOrWhiteSpace(n.Reference)) continue;
                    n.EnsureId();
                    n.Region ??= Geo.RegionOf(n.Department);
                    _tagger.Apply(n);
                    _classifier?.Apply(n);
                    if (!unique.TryAdd(n.Id, n)) intraBatchDuplicates++;
                }

                _repo.EnsureSource(provider.SourceId, provider.Name, provider.Type, provider.BaseUrl);
                var res = _repo.BulkUpsert(unique.Values);
                _repo.MarkSynced(provider.SourceId, DateTime.UtcNow);

                s.Inserted = res.Inserted;
                s.Duplicates = intraBatchDuplicates + res.Updated;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                s.Error = ex.Message;
            }

            s.Elapsed = sw.Elapsed;
            summary.Sources.Add(s);
            done++;
            progress?.Report(new IngestionProgress(provider.SourceId,
                s.Error == null ? $"Terminé : {s.Inserted} nouveaux, {s.Duplicates} doublons" : $"Erreur : {s.Error}",
                done, providers.Count));
        }
        return summary;
    }
}
