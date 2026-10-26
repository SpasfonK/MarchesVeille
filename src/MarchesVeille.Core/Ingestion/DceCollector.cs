using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Providers;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Ingestion;

public sealed record DceProgress(string Message, int Done, int Total);

public sealed class DceRunOptions
{
    /// <summary>Surcharge de dce.publishedWithinDays.</summary>
    public int? PublishedWithinDays { get; set; }
    public int? MaxPerRun { get; set; }
    /// <summary>Réessayer aussi les DCE « accès restreint » ou « non pris en charge ».</summary>
    public bool RetryBlocked { get; set; }
    /// <summary>Sauvegarde les pages HTML de chaque étape (DCE/_diagnostic) pour régler les sélecteurs.</summary>
    public bool Debug { get; set; }
    public DateTime? Now { get; set; }
}

public sealed class DceSummary
{
    public int Candidates { get; set; }
    public int Matched { get; set; }
    public int Downloaded { get; set; }
    public int Restricted { get; set; }
    public int Unsupported { get; set; }
    public int Failed { get; set; }
    public int SkippedByLimit { get; set; }
    public long TotalBytes { get; set; }
    public List<string> Messages { get; } = new();

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Récapitulatif de la collecte des DCE :");
        sb.AppendLine($"  candidats={Candidates} retenus={Matched} téléchargés={Downloaded} restreints={Restricted} non-pris-en-charge={Unsupported} échecs={Failed} reportés={SkippedByLimit}");
        sb.Append($"  volume téléchargé : {TotalBytes / (1024.0 * 1024.0):F1} Mo");
        foreach (var m in Messages) sb.Append("\n  ").Append(m);
        return sb.ToString();
    }

    public string ToOneLine() =>
        $"{Downloaded} DCE téléchargés, {Restricted} restreints, {Failed} échecs, {Unsupported} non pris en charge";
}

public sealed class DceCollector
{
    private static readonly Regex NonSlug = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    private readonly ArchiveRepository _archives;
    private readonly IReadOnlyList<IDceDownloadProvider> _providers;
    private readonly DceSettings _settings;
    private readonly string _rootFolder;

    public DceCollector(ArchiveRepository archives, IReadOnlyList<IDceDownloadProvider> providers, DceSettings settings, string dataDir)
    {
        _archives = archives;
        _providers = providers;
        _settings = settings;
        _rootFolder = Path.IsPathRooted(settings.Folder) ? settings.Folder : Path.Combine(dataDir, settings.Folder);
    }

    public string RootFolder => _rootFolder;

    public async Task<DceSummary> RunAsync(
        IReadOnlyList<DceProfile> profiles, DceRunOptions options, IProgress<DceProgress>? progress, CancellationToken ct)
    {
        var summary = new DceSummary();
        var active = profiles.Where(p => p.Enabled).ToList();
        if (active.Count == 0)
        {
            summary.Messages.Add("Aucun profil de collecte actif : créez-en un (un profil sans critère télécharge tout).");
            return summary;
        }

        Directory.CreateDirectory(_rootFolder);
        var tempDir = Path.Combine(_rootFolder, "_tmp");
        var now = options.Now ?? DateTime.UtcNow;
        var since = now.AddDays(-(options.PublishedWithinDays ?? _settings.PublishedWithinDays));

        var candidates = _archives.ListCandidates(since, _settings.MaxAttempts, options.RetryBlocked);
        summary.Candidates = candidates.Count;
        var matched = candidates.Where(n => DceProfile.AnyMatches(active, n)).ToList();
        summary.Matched = matched.Count;

        var limit = Math.Max(1, options.MaxPerRun ?? _settings.MaxPerRun);
        if (matched.Count > limit)
        {
            summary.SkippedByLimit = matched.Count - limit;
            matched = matched.Take(limit).ToList();
            summary.Messages.Add($"{summary.SkippedByLimit} avis reportés au prochain lancement (limite de {limit} par exécution).");
        }

        int done = 0;
        foreach (var n in matched)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DceProgress($"{n.BuyerName} — {Truncate(n.Title, 70)}", done, matched.Count));
            var archive = await ProcessAsync(n, options.Debug, ct);
            switch (archive.Status)
            {
                case DceStatus.Ok: summary.Downloaded++; summary.TotalBytes += archive.SizeBytes; break;
                case DceStatus.Restricted: summary.Restricted++; break;
                case DceStatus.Unsupported: summary.Unsupported++; break;
                default: summary.Failed++; break;
            }
            done++;
            progress?.Report(new DceProgress($"{archive.Status} — {Truncate(n.Title, 70)}", done, matched.Count));
            if (done < matched.Count) await Task.Delay(_settings.DelayMs, ct);
        }

        try { if (Directory.Exists(tempDir) && !Directory.EnumerateFileSystemEntries(tempDir).Any()) Directory.Delete(tempDir); } catch { }
        return summary;
    }

    /// <summary>Télécharge le DCE d'un seul avis (hors profils), par exemple depuis le détail d'un avis.</summary>
    public Task<DceArchive> DownloadSingleAsync(Notice n, bool debug, CancellationToken ct)
    {
        Directory.CreateDirectory(_rootFolder);
        return ProcessAsync(n, debug, ct);
    }

    private async Task<DceArchive> ProcessAsync(Notice n, bool debug, CancellationToken ct)
    {
        var archive = DceArchive.FromNotice(n);
        archive.LastAttemptAt = DateTime.UtcNow;
        var tempDir = Path.Combine(_rootFolder, "_tmp");
        var maxBytes = Math.Max(1, _settings.MaxFileSizeMb) * 1024L * 1024L;

        var provider = _providers.FirstOrDefault(p => p.CanDownload(n));
        if (provider == null)
        {
            archive.Status = DceStatus.Unsupported;
            archive.Error = "Aucun connecteur ne sait récupérer le DCE de ce portail.";
            _archives.Save(archive);
            return archive;
        }

        try
        {
            var debugDir = debug
                ? Path.Combine(_rootFolder, "_diagnostic", Slug(n.Reference, 40) + "_" + n.Id[..Math.Min(8, n.Id.Length)])
                : null;
            var res = await provider.DownloadDceAsync(n, tempDir, maxBytes, debugDir, ct);
            switch (res.Outcome)
            {
                case DceOutcome.Downloaded: await FinalizeAsync(n, archive, res, ct); break;
                case DceOutcome.Restricted: archive.Status = DceStatus.Restricted; archive.Error = res.Error; break;
                case DceOutcome.Unsupported: archive.Status = DceStatus.Unsupported; archive.Error = res.Error; break;
                default: archive.Status = DceStatus.Failed; archive.Error = res.Error; break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            archive.Status = DceStatus.Failed;
            archive.Error = ex.Message;
        }
        _archives.Save(archive);
        return archive;
    }

    private async Task FinalizeAsync(Notice n, DceArchive archive, DceDownloadResult res, CancellationToken ct)
    {
        var ext = Path.GetExtension(res.FileName ?? "");
        if (string.IsNullOrEmpty(ext)) ext = ".zip";
        var when = n.PublicationDate ?? DateTime.UtcNow;
        var dir = Path.Combine(_rootFolder, when.Year.ToString("D4"), when.Month.ToString("D2"), Slug(n.BuyerName, 30));
        Directory.CreateDirectory(dir);
        var baseName = $"{Slug(n.Reference, 30)}_{Slug(n.Title, 40)}";
        var dest = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{baseName}_{i}{ext}");
        File.Move(res.FilePath!, dest);

        string sha;
        await using (var fs = File.OpenRead(dest))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        var extraction = await Task.Run(() => ContentExtractor.ExtractFromFile(dest), ct);

        archive.Status = DceStatus.Ok;
        archive.Error = null;
        archive.FileName = Path.GetFileName(dest);
        archive.FilePath = Path.GetRelativePath(_rootFolder, dest).Replace('\\', '/');
        archive.SizeBytes = new FileInfo(dest).Length;
        archive.Sha256 = sha;
        archive.FileList = string.Join('\n', extraction.Files);
        archive.ContentText = extraction.Text;
        archive.DownloadedAt = DateTime.UtcNow;
    }

    public string FullPath(DceArchive a) => Path.Combine(_rootFolder, a.FilePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Slug(string? s, int max)
    {
        var t = NonSlug.Replace(TextNormalizer.Normalize(s), "-").Trim('-');
        if (t.Length == 0) t = "sans-nom";
        return t.Length > max ? t[..max].TrimEnd('-') : t;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
