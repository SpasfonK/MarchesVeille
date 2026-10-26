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


---

# Téléchargement anonyme des DCE et archives

## Principe

1. **Synchroniser** les avis (`--scrap`, ou le bouton de la fenêtre principale).
2. **Collecter** : l'application télécharge, sans identification, le DCE des avis retenus par vos **profils de collecte**,
   puis l'enregistre sous `DCE/AAAA/MM/<acheteur>/<référence>_<objet>.zip` à côté de l'exécutable.
3. **Retrouver** : le menu « Archives DCE… » interroge l'index (avis + noms de fichiers + texte des documents),
   **sans limite de date de récupération**.

Usage mensuel typique : `MarchesVeille.exe --scrap --days=31 --dce` (Planificateur de tâches), ou dans l'interface :
Jours = 31, case « puis télécharger les DCE », « Lancer la synchronisation ».

## Profils de collecte (interface : bouton « Collecte DCE… »)

Un avis est retenu s'il satisfait **tous** les critères renseignés d'un profil **actif** (un critère multiple est satisfait par l'un de ses éléments) :
mots-clés (objet, description, acheteur), mots-clés exclus, organisations (fragment de nom), types (**Travaux, Fournitures, Services, PI**),
régions, tags (ex. `VNF-DTNE`). **Un profil sans critère télécharge tout.** Plusieurs profils se cumulent (OU).
Les profils sont stockés dans `marches.db`. En ligne de commande, `--dce-all` ignore les profils.

Type de marché et PI : fournis par la source quand elle les donne (BOAMP : `type_marche`), sinon déduits par les motifs de la
section `classification` de `connectors.json` (à adapter à votre vocabulaire). Une PI (étude, maîtrise d'œuvre, AMO, audit…) est aussi comptée comme Service.

## Réglages (`connectors.json`, section `dce`)

| Clé | Rôle |
|---|---|
| `folder` | Dossier des archives (relatif à l'exe, ou absolu) |
| `maxFileSizeMb` | Taille maximale d'un DCE (au-delà : échec, rien n'est conservé) |
| `maxPerRun` | Nombre maximal de DCE par exécution ; le reste est repris au lancement suivant |
| `delayMs` | Pause entre deux téléchargements (soyez courtois avec les plateformes) |
| `maxAttempts` | Tentatives avant abandon d'un DCE en échec |
| `publishedWithinDays` | Seuls les avis publiés depuis N jours sont candidats |

Statuts d'une archive : `ok`, `failed` (réessayé jusqu'à `maxAttempts`), `restricted` (identification ou inscription exigée),
`unsupported` (aucun connecteur pour ce portail). `restricted` et `unsupported` ne sont retentés qu'avec `--dce-retry` / la case dédiée.

## Parcours de téléchargement d'un portail (`dce` dans un connecteur)

Chaque connecteur HTML décrit comment atteindre le fichier **anonymement**, sous forme d'étapes exécutées dans une même session :

```json
"dce": {
  "enabled": true,
  "steps": [
    { "type": "get",  "url": "{dceUrl}" },
    { "type": "link", "selector": "a[href*='TelechargementDce']" },
    { "type": "form", "formSelector": "form", "fields": { "choix": "anonyme" },
      "submitName": "valider", "postbackTargetField": "PRADO_POSTBACK_TARGET", "optional": true },
    { "type": "link", "selector": "a[href*='DownloadCompleteDce']" }
  ],
  "restrictedPatterns": ["identifiez-vous", "mot de passe"]
}
```

- `get` : ouvre une URL (`{dceUrl}` = lien du DCE s'il est connu, sinon `{noticeUrl}` = page de l'avis).
- `link` : suit le premier lien correspondant au sélecteur CSS de la page courante.
- `form` : soumet un formulaire de la page courante (champs cachés repris, `fields` forcés). **Aucune coordonnée n'est envoyée** : seuls les champs que vous configurez le sont.
- `optional` : l'étape est ignorée si l'élément est absent. Le parcours s'arrête dès qu'une réponse est un fichier (zip, pdf…).
- Sans `steps`, le lien `{dceUrl}` est téléchargé directement.
- Si le parcours finit sur une page HTML contenant un des `restrictedPatterns`, l'archive est marquée `restricted`.
- Un avis BOAMP est traité par le connecteur dont l'adresse correspond à l'URL du DCE trouvée dans l'avis (profil acheteur).

> Les parcours fournis pour PLACE et le Grand Est sont des **valeurs de départ non vérifiées**. Lancez `--dce-only --dce-all --dce-max=3 --dce-debug`
> (ou cochez « Mode diagnostic ») : les pages HTML de chaque étape sont sauvegardées dans `DCE/_diagnostic/<avis>/etapeN.html`.
> Ouvrez-les pour corriger sélecteurs et noms de champs, relancez, jusqu'à obtenir un zip.

## Archives : recherche

- Barre de recherche : mots, `"phrase exacte"`, `AND` / `OR` / `NOT`, insensible aux accents et à la casse.
- « Chercher dans » : tout, avis (objet, acheteur, référence), noms de fichiers, contenu des documents.
- Recherche avancée : organisation, référence, type (Travaux / Fournitures / Services / PI), région, dates de publication et de récupération, tri.
- Texte indexé : PDF avec couche texte, Word, Excel, PowerPoint, LibreOffice, txt/csv/xml/html, zips imbriqués (2 niveaux). Les **PDF scannés** (images) ne sont pas lisibles : seuls leurs noms de fichiers sont indexés (pas d'OCR).
- Les archives restent trouvables même si l'avis a disparu de la base : l'index copie les informations de l'avis.

## À garder en tête

- Un téléchargement anonyme reste visible de l'acheteur dans les statistiques de la plateforme (nombre de retraits), sans nom.
- Respectez les conditions d'utilisation des plateformes : gardez des pauses raisonnables (`delayMs`) et des volumes mesurés.
- Pour une consultation sous le seuil de publicité obligatoire ou réservée, le portail peut exiger une inscription : l'archive sera `restricted`
  et le bouton « Accéder au DCE » vous permet de la récupérer dans votre navigateur.
