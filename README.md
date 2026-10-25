# MarchesVeille — veille de marchés publics portable et hors-ligne

Application Windows autonome (exécutable unique, aucun runtime .NET à installer) : télécharge, dédoublonne et indexe
localement les avis de marchés publics (BOAMP, PLACE, profil acheteur Région Grand Est, profils personnalisés),
avec recherche plein texte instantanée (SQLite FTS5) et qualification automatique « VNF-DTNE ».

## Structure

```
src/MarchesVeille.Core      SQLite + FTS5, connecteurs, ingestion, CLI (net8.0)
src/MarchesVeille.Desktop   Interface WPF + exe portable (net8.0-windows, win-x64)
tests/MarchesVeille.Tests   Tests xUnit (phases 1, 2 et 4)
docs/GUIDE_CONNECTORS.md    Configurer connectors.json
.github/workflows/build.yml Tests + publication de l'exe (windows-latest)
```

## Construire

Sous Windows avec le SDK .NET 8 :

```
dotnet test tests/MarchesVeille.Tests/MarchesVeille.Tests.csproj -c Release
dotnet publish src/MarchesVeille.Desktop/MarchesVeille.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish
```

Résultat : `publish/MarchesVeille.exe`. Au premier lancement, `marches.db` et `connectors.json` sont créés
à côté de l'exécutable (repli sur `%LOCALAPPDATA%\MarchesVeille` si le dossier n'est pas inscriptible).
La bibliothèque native SQLite est embarquée dans l'exe et extraite dans le dossier temporaire au démarrage.

Sans machine Windows : poussez le dépôt sur GitHub, le workflow `build` exécute les tests, publie l'exe
et le met à disposition en artefact.

## Utilisation

- **Interface** : lancer `MarchesVeille.exe`. Recherche : mots, `"phrase exacte"`, `AND` / `OR` / `NOT`.
  Filtres rapides (Grand Est, VNF DT Nord-Est, date limite à venir, non lus, favoris, curseur d'échéance),
  couleurs d'urgence (rouge ≤ J-3, orange ≤ J-7, gris = clos), veilles programmées, bouton de synchronisation.
- **Batch** : `MarchesVeille.exe --scrap --source=all --days=3` (voir le guide).

## État de validation

Code écrit et relu sans compilation ni exécution locale. La première exécution de la CI est la vraie validation :
corrigez d'abord ce qu'elle signale. Les sélecteurs PLACE / Grand Est de `connectors.json` sont des valeurs de départ
à vérifier sur les sites réels.
