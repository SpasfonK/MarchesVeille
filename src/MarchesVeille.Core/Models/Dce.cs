using System.Text.Json;
using System.Text.Json.Serialization;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Models;

public static class DceStatus
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Restricted = "restricted";
    public const string Unsupported = "unsupported";
}

/// <summary>Archive de DCE (ou tentative) ; les champs de l'avis sont copiés pour que l'archive reste trouvable seule.</summary>
public sealed class DceArchive
{
    public long Id { get; set; }
    public string NoticeId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Title { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public string? Region { get; set; }
    public string? Department { get; set; }
    public string? MarketType { get; set; }
    public bool IsPi { get; set; }
    public DateTime? PublicationDate { get; set; }
    public DateTime? DeadlineDate { get; set; }
    public string? UrlNotice { get; set; }
    public string Status { get; set; } = DceStatus.Failed;
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>Chemin relatif au dossier DCE, séparateur « / ».</summary>
    public string FilePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Contenu de l'archive (un fichier par ligne).</summary>
    public string FileList { get; set; } = "";
    public string ContentText { get; set; } = "";
    public DateTime? DownloadedAt { get; set; }
    public DateTime LastAttemptAt { get; set; } = DateTime.UtcNow;

    public int FileCount => string.IsNullOrEmpty(FileList) ? 0 : FileList.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    public static DceArchive FromNotice(Notice n) => new()
    {
        NoticeId = n.Id, SourceId = n.SourceId, Reference = n.Reference, Title = n.Title, BuyerName = n.BuyerName,
        Region = n.Region, Department = n.Department, MarketType = n.MarketType, IsPi = n.IsPi,
        PublicationDate = n.PublicationDate, DeadlineDate = n.DeadlineDate, UrlNotice = n.UrlNotice
    };
}

public enum ArchiveScope { All, Metadata, FileNames, Content }
public enum ArchiveSort { Relevance, PublishedDesc, DownloadedDesc }

public sealed class ArchiveFilters
{
    public string? Text { get; set; }
    public ArchiveScope Scope { get; set; } = ArchiveScope.All;
    public string? Buyer { get; set; }
    public string? Reference { get; set; }
    /// <summary>Travaux | Fournitures | Services | PI.</summary>
    public string? MarketType { get; set; }
    public string? Region { get; set; }
    public DateTime? PublishedFrom { get; set; }
    public DateTime? PublishedTo { get; set; }
    public DateTime? DownloadedFrom { get; set; }
    public DateTime? DownloadedTo { get; set; }
    /// <summary>Par défaut, seules les archives effectivement téléchargées sont renvoyées.</summary>
    public string? Status { get; set; } = DceStatus.Ok;
    public ArchiveSort Sort { get; set; } = ArchiveSort.Relevance;
    public int Limit { get; set; } = 300;
    public int Offset { get; set; }
}

public sealed record ArchiveHit(DceArchive Archive, double Score, string TitleHighlight, string ContentSnippet, string FileSnippet);

/// <summary>Profil de collecte : un avis est candidat s'il satisfait TOUS les critères renseignés d'un profil actif ; profil vide = tout.</summary>
public sealed class DceProfile
{
    [JsonIgnore] public long Id { get; set; }
    [JsonIgnore] public string Name { get; set; } = "";
    [JsonIgnore] public bool Enabled { get; set; } = true;

    /// <summary>Au moins un mot-clé dans titre / description / acheteur (insensible aux accents et à la casse).</summary>
    public List<string> Keywords { get; set; } = new();
    public List<string> ExcludeKeywords { get; set; } = new();
    /// <summary>Au moins un fragment de nom d'organisation.</summary>
    public List<string> Buyers { get; set; } = new();
    /// <summary>Travaux, Fournitures, Services, PI (au moins un).</summary>
    public List<string> MarketTypes { get; set; } = new();
    public List<string> Regions { get; set; } = new();
    /// <summary>Tags de qualification (ex. VNF-DTNE).</summary>
    public List<string> Categories { get; set; } = new();

    [JsonIgnore]
    public bool MatchesEverything => Keywords.Count == 0 && ExcludeKeywords.Count == 0 && Buyers.Count == 0
        && MarketTypes.Count == 0 && Regions.Count == 0 && Categories.Count == 0;

    public bool Matches(Notice n)
    {
        if (Regions.Count > 0 && !Regions.Any(r => string.Equals(r, n.Region, StringComparison.OrdinalIgnoreCase))) return false;
        if (Categories.Count > 0 && !Categories.Any(c => string.Equals(c, n.BuyerCategory, StringComparison.OrdinalIgnoreCase))) return false;

        if (MarketTypes.Count > 0)
        {
            bool ok = MarketTypes.Any(t => t.Equals("PI", StringComparison.OrdinalIgnoreCase)
                ? n.IsPi
                : string.Equals(t, n.MarketType, StringComparison.OrdinalIgnoreCase));
            if (!ok) return false;
        }

        if (Buyers.Count > 0)
        {
            var buyer = TextNormalizer.Normalize(n.BuyerName);
            if (!Buyers.Any(b => buyer.Contains(TextNormalizer.Normalize(b).Trim()))) return false;
        }

        if (Keywords.Count > 0 || ExcludeKeywords.Count > 0)
        {
            var desc = n.Description.Length > 3000 ? n.Description[..3000] : n.Description;
            var hay = TextNormalizer.Normalize($"{n.Title} {desc} {n.BuyerName}");
            if (Keywords.Count > 0 && !Keywords.Any(k => hay.Contains(TextNormalizer.Normalize(k).Trim()))) return false;
            if (ExcludeKeywords.Any(k => hay.Contains(TextNormalizer.Normalize(k).Trim()))) return false;
        }
        return true;
    }

    public static bool AnyMatches(IEnumerable<DceProfile> profiles, Notice n) => profiles.Any(p => p.Enabled && p.Matches(n));

    public string ToJson() => JsonSerializer.Serialize(this);

    public static DceProfile FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<DceProfile>(json) ?? new DceProfile(); }
        catch (JsonException) { return new DceProfile(); }
    }
}
