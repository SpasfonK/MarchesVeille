using MarchesVeille.Core.Models;

namespace MarchesVeille.Core.Providers;

public enum DceOutcome { Downloaded, Restricted, Unsupported, Failed }

/// <param name="FilePath">Fichier temporaire téléchargé (à déplacer par l'appelant).</param>
public sealed record DceDownloadResult(DceOutcome Outcome, string? FilePath, string? FileName, long SizeBytes, string? Error);

/// <summary>Connecteur capable de récupérer anonymement le DCE d'un avis.</summary>
public interface IDceDownloadProvider
{
    string SourceId { get; }
    string? BaseUrl { get; }
    bool CanDownload(Notice notice);
    /// <param name="tempDir">Dossier où écrire le fichier téléchargé.</param>
    /// <param name="debugDir">Si non nul, les pages HTML de chaque étape y sont sauvegardées (réglage des sélecteurs).</param>
    Task<DceDownloadResult> DownloadDceAsync(Notice notice, string tempDir, long maxBytes, string? debugDir, CancellationToken ct);
}
