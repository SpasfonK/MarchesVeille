using System.Text;
using System.Text.Json;

namespace MarchesVeille.Core.Providers;

internal static class JsonUtil
{
    /// <summary>Première propriété non vide parmi les noms donnés ; tableaux aplatis par « , ».</summary>
    public static string? GetString(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            var s = Flatten(v);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    private static string? Flatten(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Flatten).Where(x => !string.IsNullOrWhiteSpace(x))),
        _ => null
    };

    /// <summary>Concatène les feuilles texte d'un JSON (texte intégral de l'avis), plafonné.</summary>
    public static void CollectStrings(JsonElement e, StringBuilder sb, int max)
    {
        if (sb.Length >= max) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var s = e.GetString();
                if (!string.IsNullOrWhiteSpace(s) && s.Length > 2) sb.Append(s.Trim()).Append(' ');
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) CollectStrings(p.Value, sb, max);
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray()) CollectStrings(i, sb, max);
                break;
        }
    }
}
