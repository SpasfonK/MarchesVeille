using MarchesVeille.Core.Models;

namespace MarchesVeille.Core.Providers;

public sealed class ScrapingOptions
{
    public int Days { get; set; } = 3;
    public int MaxPages { get; set; } = 50;
    public IProgress<string>? Progress { get; set; }
    /// <summary>Date « aujourd'hui » (UTC) ; surchargeable pour les tests.</summary>
    public DateTime? Today { get; set; }
}

public interface IMarketplaceProvider
{
    string SourceId { get; }
    string Name { get; }
    /// <summary>API ou HTML_SCRAPER.</summary>
    string Type { get; }
    string? BaseUrl { get; }
    Task<IReadOnlyList<Notice>> FetchAsync(ScrapingOptions options, CancellationToken ct);
}
