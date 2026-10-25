using System.Security.Cryptography;
using System.Text;

namespace MarchesVeille.Core.Models;

public sealed class Notice
{
    public string Id { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public string? BuyerSiret { get; set; }
    public string? Department { get; set; }
    public string? Region { get; set; }
    /// <summary>Tag de qualification (ex. "VNF-DTNE").</summary>
    public string? BuyerCategory { get; set; }
    public DateTime? PublicationDate { get; set; }
    public DateTime? DeadlineDate { get; set; }
    public string? UrlNotice { get; set; }
    public string? UrlDce { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public static string ComputeId(string sourceId, string reference)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId}|{reference.Trim()}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void EnsureId()
    {
        if (string.IsNullOrEmpty(Id)) Id = ComputeId(SourceId, Reference);
    }
}
