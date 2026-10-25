using System.Text.Json.Serialization;

namespace MarchesVeille.Core.Models;

public enum SortMode { Relevance, PublicationDesc, DeadlineAsc }

public sealed class SearchFilters
{
    public string? Text { get; set; }
    public string? Buyer { get; set; }
    public string? Region { get; set; }
    public string? BuyerCategory { get; set; }
    public string? SourceId { get; set; }
    /// <summary>Date limite >= maintenant (UTC).</summary>
    public bool OnlyActive { get; set; }
    /// <summary>Date limite comprise entre maintenant et maintenant + N jours.</summary>
    public int? DeadlineWithinDays { get; set; }
    public bool OnlyUnread { get; set; }
    public bool OnlyFavorites { get; set; }
    public SortMode Sort { get; set; } = SortMode.Relevance;
    public int Limit { get; set; } = 200;

    [JsonIgnore] public int Offset { get; set; }
    [JsonIgnore] public DateTime? Now { get; set; }
    [JsonIgnore] public DateTime? CreatedAfter { get; set; }

    public SearchFilters Clone() => (SearchFilters)MemberwiseClone();
}

public sealed record SearchResult(Notice Notice, double Score, string TitleHighlight, string DescriptionHighlight);

public sealed record UpsertResult(int Inserted, int Updated);

public sealed class SavedSearch
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public SearchFilters Filters { get; set; } = new();
    public bool AlertEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }
}

public sealed record SourceInfo(string Id, string Name, string Type, string? BaseUrl, bool IsActive, DateTime? LastSyncedAt);
