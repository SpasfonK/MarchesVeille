using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace MarchesVeille.Core.Util;

public sealed record ExtractionResult(List<string> Files, string Text);

/// <summary>
/// Liste les fichiers d'une archive DCE (zip, y compris zips imbriqués) et en extrait le texte pour l'indexation :
/// PDF (PdfPig), docx/xlsx/pptx/odt/ods/odp, txt/csv/xml/html. Les PDF scannés (images) ne produisent pas de texte (pas d'OCR).
/// </summary>
public static class ContentExtractor
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private const long MaxEntryBytes = 120L * 1024 * 1024;
    private const int MaxDepth = 2;

    public static ExtractionResult ExtractFromFile(string path, int maxChars = 300_000)
    {
        var files = new List<string>();
        var sb = new StringBuilder();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var name = Path.GetFileName(path);
            Process(name, fs, files, sb, maxChars, 0, topLevel: true);
        }
        catch (Exception)
        {
            // fichier illisible : on garde ce qui a été collecté
        }
        return new ExtractionResult(files, sb.ToString().Trim());
    }

    private static bool IsOfficeContainer(string ext) =>
        ext is ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp";

    private static void Process(string name, Stream stream, List<string> files, StringBuilder sb, int maxChars, int depth, bool topLevel, string prefix = "")
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (!IsOfficeContainer(ext) && LooksLikeZip(stream))
        {
            if (!topLevel) files.Add(prefix + name);
            if (depth > MaxDepth) return;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/') || string.IsNullOrEmpty(e.Name)) continue;
                var shown = prefix + e.FullName;
                var entryExt = Path.GetExtension(e.Name).ToLowerInvariant();

                if (entryExt == ".zip")
                {
                    files.Add(shown);
                    if (e.Length > 0 && e.Length <= MaxEntryBytes && depth + 1 <= MaxDepth)
                    {
                        try
                        {
                            using var ms = new MemoryStream();
                            using (var es = e.Open()) es.CopyTo(ms);
                            ms.Position = 0;
                            ExtractZipEntries(ms, files, sb, maxChars, depth + 1, shown + "/");
                        }
                        catch (Exception) { /* zip interne illisible */ }
                    }
                    continue;
                }

                files.Add(shown);
                if (sb.Length >= maxChars || e.Length == 0 || e.Length > MaxEntryBytes) continue;
                try
                {
                    using var es = e.Open();
                    AppendText(e.Name, es, sb, maxChars);
                }
                catch (Exception) { /* entrée illisible */ }
            }
            return;
        }

        if (topLevel) files.Add(name);
        else files.Add(prefix + name);
        if (sb.Length < maxChars) AppendText(name, stream, sb, maxChars);
    }

    private static void ExtractZipEntries(Stream zipStream, List<string> files, StringBuilder sb, int maxChars, int depth, string prefix)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/') || string.IsNullOrEmpty(e.Name)) continue;
            files.Add(prefix + e.FullName);
            if (sb.Length >= maxChars || e.Length == 0 || e.Length > MaxEntryBytes) continue;
            try
            {
                using var es = e.Open();
                AppendText(e.Name, es, sb, maxChars);
            }
            catch (Exception) { }
        }
    }

    private static bool LooksLikeZip(Stream s)
    {
        if (!s.CanSeek || s.Length < 4) return false;
        var pos = s.Position;
        Span<byte> sig = stackalloc byte[4];
        s.Position = 0;
        var n = s.Read(sig);
        s.Position = pos;
        return n == 4 && sig[0] == 0x50 && sig[1] == 0x4B && (sig[2] == 3 || sig[2] == 5);
    }

    // ------------------------------------------------------------ texte
    private static void Append(StringBuilder sb, string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text) || sb.Length >= maxChars) return;
        var t = Spaces.Replace(text, " ").Trim();
        if (t.Length == 0) return;
        var room = maxChars - sb.Length;
        sb.Append(t.Length > room ? t[..room] : t).Append('\n');
    }

    private static string StripXml(string xml) => WebUtility.HtmlDecode(Tags.Replace(xml.Replace("</w:p>", "\n").Replace("</a:p>", "\n").Replace("</text:p>", "\n"), " "));

    private static void AppendText(string name, Stream stream, StringBuilder sb, int maxChars)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        switch (ext)
        {
            case ".txt": case ".csv": case ".md": case ".log":
                Append(sb, ReadText(stream, 2_000_000), maxChars);
                break;
            case ".xml": case ".html": case ".htm":
                Append(sb, StripXml(ReadText(stream, 2_000_000)), maxChars);
                break;
            case ".pdf":
                AppendPdf(stream, sb, maxChars);
                break;
            case ".docx": case ".xlsx": case ".pptx": case ".odt": case ".ods": case ".odp":
                AppendOffice(ext, stream, sb, maxChars);
                break;
        }
    }

    private static string ReadText(Stream s, int maxChars)
    {
        using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
        var buf = new char[maxChars];
        var n = reader.ReadBlock(buf, 0, buf.Length);
        return new string(buf, 0, n);
    }

    private static void AppendPdf(Stream stream, StringBuilder sb, int maxChars)
    {
        try
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            using var doc = PdfDocument.Open(ms.ToArray());
            int pages = 0;
            foreach (var page in doc.GetPages())
            {
                Append(sb, page.Text, maxChars);
                if (sb.Length >= maxChars || ++pages >= 400) break;
            }
        }
        catch (Exception) { /* PDF chiffré ou corrompu */ }
    }

    private static void AppendOffice(string ext, Stream stream, StringBuilder sb, int maxChars)
    {
        try
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            IEnumerable<ZipArchiveEntry> parts = ext switch
            {
                ".docx" => zip.Entries.Where(e => e.FullName == "word/document.xml"),
                ".xlsx" => zip.Entries.Where(e => e.FullName == "xl/sharedStrings.xml"),
                ".pptx" => zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml")),
                _ => zip.Entries.Where(e => e.FullName == "content.xml")
            };
            foreach (var part in parts)
            {
                using var ps = part.Open();
                Append(sb, StripXml(ReadText(ps, 4_000_000)), maxChars);
                if (sb.Length >= maxChars) break;
            }
        }
        catch (Exception) { /* document illisible */ }
    }
}
