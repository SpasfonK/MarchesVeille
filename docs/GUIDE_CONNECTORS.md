# Guide de configuration — `connectors.json`

Le fichier `connectors.json` est créé **à côté de l'exécutable** au premier lancement (valeurs par défaut).
Il est relu à chaque démarrage : modifiez-le, relancez l'application, aucune recompilation n'est nécessaire.
Les commentaires `//` et les virgules finales sont tolérés. En cas de JSON invalide, la GUI prévient et
utilise la configuration par défaut ; la CLI s'arrête avec le code 2.

> **Important** : les sélecteurs et noms de champs de `place` et `grandEst` sont des valeurs de départ
> **non vérifiées sur les sites réels**. Validez-les (voir « Trouver les bons sélecteurs ») avant de compter dessus.
> Le connecteur BOAMP (API JSON) est le plus fiable ; il couvre déjà une grande partie des avis PLACE et VNF.

## Structure

| Clé | Rôle |
|---|---|
| `userAgent`, `requestDelayMs`, `maxRetries` | Politesse réseau : pause entre requêtes, reprises sur 429/5xx/erreurs réseau |
| `taggingRules` | Qualification automatique des avis (tag `buyer_category`, ex. `VNF-DTNE`) |
| `boamp` | API BOAMP : filtres de conservation (départements, acheteurs) |
| `place`, `grandEst` | Connecteurs HTML (formulaire + tableau de résultats) |
| `customConnectors` | Liste de profils acheteurs supplémentaires, même format que `place` |

## Règles de qualification (`taggingRules`)

La **première** règle qui correspond pose le tag. Les motifs sont des expressions régulières appliquées sur
du texte **sans accents et en minuscules**.

```json
{ "category": "VNF-DTNE",
  "buyerPatterns": ["voies navigables de france", "\\bvnf\\b"],
  "textPatterns":  ["direction territoriale nord[- ]?est", "\\bdtne\\b"],
  "sirets": ["13001840000012"] }
```

- `buyerPatterns` / `sirets` : l'acheteur doit correspondre à l'un d'eux (si les deux sont vides, la règle ne teste que le texte).
- `textPatterns` : au moins un motif doit correspondre sur acheteur + titre + description (liste vide = pas de condition).
- Pensez à doubler les antislash dans le JSON (`\\b`).

## Ajouter un profil acheteur sans recompiler

Ajoutez un objet dans `customConnectors` (un exemple désactivé est fourni) :

```json
{
  "id": "mon-profil", "name": "Mon profil acheteur", "enabled": true,
  "baseUrl": "https://profil.example.org/",
  "searchUrl": "consultations/recherche",
  "formSelector": "form#recherche",
  "formFields": { "dateDebut": "{dateStart}", "dateFin": "{dateEnd}" },
  "submitName": "rechercher",
  "dateFormat": "dd/MM/yyyy",
  "rowSelector": "table#resultats tbody tr",
  "fields": {
    "reference": "td.ref", "title": "td.objet a", "buyer": "td.acheteur",
    "department": "td.dept", "deadline": "td.cloture", "url": "td.objet a@href", "dce": "td.dce a@href"
  },
  "paging": { "mode": "link", "nextSelector": "a.suivant" },
  "defaultRegion": "Grand Est",
  "keepBuyerPatterns": []
}
```

Le nouvel identifiant (`mon-profil`) apparaît dans la liste des sources de la GUI et s'utilise en CLI : `--source=mon-profil`.

### Champs

- `searchUrl` : relatif à `baseUrl` (ou absolu). La page doit contenir le formulaire de recherche.
- `formSelector` : sélecteur CSS du formulaire. Tous ses champs (dont les champs cachés `PRADO_PAGESTATE`, `__VIEWSTATE`…) sont repris automatiquement.
- `formFields` : champs à forcer (nom → valeur). Jetons : `{dateStart}`, `{dateEnd}` (selon `dateFormat`), `{isoStart}`, `{isoEnd}`. La période couvre `--days` jours, aujourd'hui inclus.
- `submitName` : nom du bouton de recherche. Si `postbackTargetField` est renseigné (ex. `PRADO_POSTBACK_TARGET`), la valeur est postée dans ce champ (postback Prado) ; sinon `submitName=1` est posté comme un bouton.
- `rowSelector` : une ligne de résultat. `fields` : `"sélecteur CSS"` (texte) ou `"sélecteur CSS@attribut"` (ex. `a@href`). Clés : `reference`, `title`, `description`, `buyer`, `department`, `publication`, `deadline`, `url`, `dce`.
- `paging.mode` : `none`, `link` (suit le `href` de `nextSelector`) ou `postback` (reposte le formulaire avec `postbackTarget`). Arrêt automatique quand une page n'apporte aucun nouvel avis ou que `maxPages` est atteint.
- `keepBuyerPatterns` : si non vide, seuls les avis dont l'acheteur correspond sont conservés (filtre côté client).
- Les dates sont lues en heure de Paris et stockées en UTC.

## BOAMP

- `keepDepartments` / `keepBuyerPatterns` : un avis est conservé s'il est dans un de ces départements **ou** si l'acheteur correspond (par défaut : Grand Est + VNF). Les deux listes vides = tout conserver.
- `extraWhere` : clause ODSQL ajoutée côté serveur (optionnel).
- Une requête paginée par jour de parution ; la limite Opendatasoft (offset + limit ≤ 10 000) est respectée.

## Trouver les bons sélecteurs

1. Ouvrez le portail dans le navigateur, outils de développement (F12), onglet **Réseau**.
2. Lancez une recherche : repérez la requête POST du formulaire et les noms de champs envoyés → `formFields`, `submitName`.
3. Onglet **Éléments** : clic droit sur une ligne de résultat → Copier → sélecteur CSS → `rowSelector`, puis `fields`.
4. Testez : `MarchesVeille.exe --scrap --source=mon-profil --days=1` et lisez `marches-scrap.log`.

## Ligne de commande

```
MarchesVeille.exe --scrap [--source=all|boamp|place|grandest|<id>[,<id>]] [--days=3] [--db=<marches.db>] [--config=<connectors.json>]
```

Codes de sortie : `0` succès, `1` au moins une source en erreur (les autres sont ingérées), `2` arguments/configuration invalides.
Le journal est écrit dans `marches-scrap.log` à côté de la base. Pour le Planificateur de tâches Windows : programme
`MarchesVeille.exe`, arguments `--scrap --source=all --days=3`, déclencheur quotidien.
