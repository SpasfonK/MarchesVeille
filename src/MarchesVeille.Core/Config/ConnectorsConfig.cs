using System.Text;
using System.Text.Json;

namespace MarchesVeille.Core.Config;

public sealed class TaggingRule
{
    /// <summary>Tag posé sur l'avis (ex. "VNF-DTNE").</summary>
    public string Category { get; set; } = "";
    /// <summary>Regex (sur texte sans accents, minuscules) testées sur le nom de l'acheteur.</summary>
    public List<string> BuyerPatterns { get; set; } = new();
    /// <summary>Regex optionnelles testées sur acheteur + titre + description (au moins une doit correspondre).</summary>
    public List<string> TextPatterns { get; set; } = new();
    /// <summary>SIRET (chiffres seuls) reconnus comme acheteur.</summary>
    public List<string> Sirets { get; set; } = new();
}

public sealed class BoampConfig
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "BOAMP (DILA)";
    public string BaseUrl { get; set; } = "https://boamp-datadila.opendatasoft.com";
    public string Dataset { get; set; } = "boamp";
    public int PageSize { get; set; } = 100;
    /// <summary>Clause ODSQL supplémentaire ajoutée au filtre de date (optionnel).</summary>
    public string? ExtraWhere { get; set; }
    /// <summary>Un avis est conservé si son département est dans cette liste OU si l'acheteur correspond à un motif. Les deux listes vides = tout conserver.</summary>
    public List<string> KeepDepartments { get; set; } = new();
    public List<string> KeepBuyerPatterns { get; set; } = new();
}

public sealed class PagingConfig
{
    /// <summary>none | link | postback</summary>
    public string Mode { get; set; } = "none";
    /// <summary>Sélecteur CSS de l'élément « page suivante ».</summary>
    public string? NextSelector { get; set; }
    /// <summary>Mode postback (Prado/ASP.NET) : valeur de l'événement cible à poster.</summary>
    public string? PostbackTarget { get; set; }
}

public sealed class HtmlConnectorConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "";
    public string SearchUrl { get; set; } = "";
    public string FormSelector { get; set; } = "form";
    /// <summary>Champs à forcer. Jetons : {dateStart} {dateEnd} (selon DateFormat), {isoStart} {isoEnd}.</summary>
    public Dictionary<string, string> FormFields { get; set; } = new();
    /// <summary>Nom du bouton de recherche (ou valeur d'événement cible en mode postback).</summary>
    public string? SubmitName { get; set; }
    /// <summary>Si renseigné (ex. PRADO_POSTBACK_TARGET), SubmitName est posté dans ce champ ; sinon SubmitName est posté comme bouton.</summary>
    public string? PostbackTargetField { get; set; }
    public string DateFormat { get; set; } = "dd/MM/yyyy";
    public string RowSelector { get; set; } = "table tr";
    /// <summary>reference, title, description, buyer, department, publication, deadline, url, dce → "sélecteur CSS[@attribut]".</summary>
    public Dictionary<string, string> Fields { get; set; } = new();
    public string[] DateFormats { get; set; } = { "dd/MM/yyyy HH:mm", "dd/MM/yyyy", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
    public PagingConfig Paging { get; set; } = new();
    public int MaxPages { get; set; } = 30;
    public string? DefaultRegion { get; set; }
    /// <summary>Si non vide, seuls les avis dont l'acheteur correspond à un motif sont conservés.</summary>
    public List<string> KeepBuyerPatterns { get; set; } = new();
    /// <summary>Parcours de téléchargement anonyme du DCE sur ce portail.</summary>
    public DceFlowConfig Dce { get; set; } = new();
}

public sealed class DceStepConfig
{
    /// <summary>get | link | form</summary>
    public string Type { get; set; } = "get";
    /// <summary>get : URL ({dceUrl}, {noticeUrl}, absolue ou relative au site).</summary>
    public string? Url { get; set; }
    /// <summary>link : sélecteur CSS du lien à suivre sur la page courante.</summary>
    public string? Selector { get; set; }
    /// <summary>form : sélecteur CSS du formulaire (défaut « form »).</summary>
    public string? FormSelector { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();
    public string? SubmitName { get; set; }
    public string? PostbackTargetField { get; set; }
    /// <summary>Étape ignorée si l'élément est absent de la page.</summary>
    public bool Optional { get; set; }
}

public sealed class DceFlowConfig
{
    public bool Enabled { get; set; }
    public List<DceStepConfig> Steps { get; set; } = new();
    /// <summary>Si le parcours aboutit à une page HTML contenant l'un de ces motifs, le DCE est classé « accès restreint ».</summary>
    public List<string> RestrictedPatterns { get; set; } = new()
    {
        "identifiez-vous", "identification obligatoire", "mot de passe", "creer un compte",
        "inscription obligatoire", "acces restreint", "reserve aux candidats"
    };
}

public sealed class DceSettings
{
    /// <summary>Dossier des archives (relatif au dossier de l'exécutable, ou absolu).</summary>
    public string Folder { get; set; } = "DCE";
    public int MaxFileSizeMb { get; set; } = 500;
    public int MaxPerRun { get; set; } = 300;
    public int DelayMs { get; set; } = 1500;
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Seuls les avis publiés depuis N jours sont candidats à la collecte.</summary>
    public int PublishedWithinDays { get; set; } = 45;
}

public sealed class ClassificationConfig
{
    public List<string> Travaux { get; set; } = new();
    public List<string> Fournitures { get; set; } = new();
    public List<string> Services { get; set; } = new();
    public List<string> Pi { get; set; } = new();
}

public sealed class ConnectorsConfig
{
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MarchesVeille/1.0";
    public int RequestDelayMs { get; set; } = 400;
    public int MaxRetries { get; set; } = 3;
    public List<TaggingRule> TaggingRules { get; set; } = new();
    public BoampConfig Boamp { get; set; } = new();
    public HtmlConnectorConfig Place { get; set; } = new();
    public HtmlConnectorConfig GrandEst { get; set; } = new();
    public List<HtmlConnectorConfig> CustomConnectors { get; set; } = new();
    public DceSettings Dce { get; set; } = new();
    public ClassificationConfig Classification { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ConnectorsConfig Parse(string json) =>
        JsonSerializer.Deserialize<ConnectorsConfig>(json, Options) ?? new ConnectorsConfig();

    public static ConnectorsConfig Default() => Parse(DefaultJson);

    /// <summary>Charge connectors.json ; le crée avec les valeurs par défaut s'il n'existe pas. Lève JsonException si le fichier est invalide.</summary>
    public static ConnectorsConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, DefaultJson, new UTF8Encoding(false));
        return Parse(File.ReadAllText(path));
    }

    public const string DefaultJson = """
{
  // Voir docs/GUIDE_CONNECTORS.md pour la description de chaque champ.
  "userAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MarchesVeille/1.0",
  "requestDelayMs": 400,
  "maxRetries": 3,

  // Règles de qualification : la première règle qui correspond pose le tag (buyer_category).
  "taggingRules": [
    {
      "category": "VNF-DTNE",
      "buyerPatterns": ["voies navigables de france", "\\bvnf\\b"],
      "textPatterns": [
        "direction territoriale nord[- ]?est",
        "\\bdt nord[- ]?est\\b",
        "\\bdtne\\b",
        "canal de la marne au rhin",
        "\\bcmre\\b"
      ],
      "sirets": []
    },
    {
      "category": "VNF",
      "buyerPatterns": ["voies navigables de france", "\\bvnf\\b"],
      "textPatterns": [],
      "sirets": []
    }
  ],

  // Téléchargement des DCE : dossier (relatif à l'exe), plafonds, politesse, fenêtre de publication.
  "dce": {
    "folder": "DCE",
    "maxFileSizeMb": 500,
    "maxPerRun": 300,
    "delayMs": 1500,
    "maxAttempts": 3,
    "publishedWithinDays": 45
  },

  // Classification (motifs sur texte sans accents, en minuscules). Le type fourni par la source (BOAMP) prime.
  "classification": {
    "travaux": ["\\btravaux\\b", "\\brefection\\b", "\\brenovation\\b", "\\bconstruction\\b", "\\breconstruction\\b",
                "\\bdragage\\b", "\\bcurage\\b", "\\brestauration\\b", "\\bterrassement", "\\bconfortement\\b",
                "\\bremplacement des?\\b", "\\bamenagement\\b"],
    "fournitures": ["\\bfournitures?\\b", "\\bacquisition", "\\bachats?\\b", "\\blivraison", "\\bmateriels?\\b",
                    "\\bvehicules?\\b", "\\bequipements?\\b"],
    "services": ["\\bservices?\\b", "\\bprestations?\\b", "\\bmaintenance\\b", "\\bentretien\\b", "\\bnettoyage\\b",
                 "\\bgardiennage\\b", "\\bformations?\\b", "\\bassurances?\\b", "\\btransports?\\b", "\\bassistance\\b"],
    "pi": ["\\betudes?\\b", "\\bmaitrise d'?oeuvre\\b", "\\bmoe\\b", "\\bassistance a maitrise d'?ouvrage\\b", "\\bamo\\b",
           "\\bdiagnostics?\\b", "\\baudit\\b", "\\bconseil\\b", "\\bexpertise\\b", "\\bingenierie\\b",
           "\\bbureau d'?etudes?\\b", "\\bcoordination sps\\b", "\\bcontrole technique\\b", "\\btopograph",
           "\\bgeotechni", "\\bmission de\\b"]
  },

  "boamp": {
    "enabled": true,
    "name": "BOAMP (DILA)",
    "baseUrl": "https://boamp-datadila.opendatasoft.com",
    "dataset": "boamp",
    "pageSize": 100,
    "extraWhere": "",
    "keepDepartments": ["08", "10", "51", "52", "54", "55", "57", "67", "68", "88"],
    "keepBuyerPatterns": ["voies navigables de france", "\\bvnf\\b"]
  },

  // ATTENTION : sélecteurs et noms de champs de PLACE et du Grand Est à valider sur le site réel
  // (outils de développement du navigateur, onglet Réseau / Éléments) puis à ajuster ici.
  "place": {
    "id": "place",
    "name": "PLACE (marches-publics.gouv.fr)",
    "enabled": true,
    "baseUrl": "https://www.marches-publics.gouv.fr/",
    "searchUrl": "?page=Entreprise.EntrepriseAdvancedSearch&AllCons&searchAnnCons",
    "formSelector": "form",
    "formFields": {
      "ctl0$CONTENU_PAGE$AdvancedSearch$dateMiseEnLigneStart": "{dateStart}",
      "ctl0$CONTENU_PAGE$AdvancedSearch$dateMiseEnLigneEnd": "{dateEnd}"
    },
    "submitName": "ctl0$CONTENU_PAGE$AdvancedSearch$lancerRecherche",
    "postbackTargetField": "PRADO_POSTBACK_TARGET",
    "dateFormat": "dd/MM/yyyy",
    "rowSelector": "table.table-results tbody tr",
    "fields": {
      "reference": "td:nth-child(1)",
      "title": "td:nth-child(2)",
      "buyer": "td:nth-child(3)",
      "deadline": "td:nth-child(4)",
      "url": "td:nth-child(2) a@href"
    },
    "paging": {
      "mode": "postback",
      "nextSelector": "a[title*='suivante']",
      "postbackTarget": "ctl0$CONTENU_PAGE$resultSearch$PagerTop$ctl2"
    },
    // Parcours de téléchargement anonyme du DCE : à valider avec le mode diagnostic (--dce-debug), voir le guide.
    "dce": {
      "enabled": true,
      "steps": [
        { "type": "get", "url": "{dceUrl}" },
        { "type": "link", "selector": "a[href*='TelechargementDce']" },
        { "type": "form", "formSelector": "form", "submitName": "ctl0$CONTENU_PAGE$validateButton",
          "postbackTargetField": "PRADO_POSTBACK_TARGET", "optional": true },
        { "type": "link", "selector": "a[href*='DownloadCompleteDce']" }
      ]
    },
    "maxPages": 30,
    "keepBuyerPatterns": []
  },

  "grandEst": {
    "id": "grandest",
    "name": "Région Grand Est",
    "enabled": true,
    "baseUrl": "https://marchespublics.grandest.fr/",
    "searchUrl": "?page=Entreprise.EntrepriseAdvancedSearch&AllCons&searchAnnCons",
    "formSelector": "form",
    "formFields": {
      "ctl0$CONTENU_PAGE$AdvancedSearch$dateMiseEnLigneStart": "{dateStart}",
      "ctl0$CONTENU_PAGE$AdvancedSearch$dateMiseEnLigneEnd": "{dateEnd}"
    },
    "submitName": "ctl0$CONTENU_PAGE$AdvancedSearch$lancerRecherche",
    "postbackTargetField": "PRADO_POSTBACK_TARGET",
    "dateFormat": "dd/MM/yyyy",
    "rowSelector": "table.table-results tbody tr",
    "fields": {
      "reference": "td:nth-child(1)",
      "title": "td:nth-child(2)",
      "buyer": "td:nth-child(3)",
      "deadline": "td:nth-child(4)",
      "url": "td:nth-child(2) a@href"
    },
    "paging": {
      "mode": "postback",
      "nextSelector": "a[title*='suivante']",
      "postbackTarget": "ctl0$CONTENU_PAGE$resultSearch$PagerTop$ctl2"
    },
    // Parcours de téléchargement anonyme du DCE : à valider avec le mode diagnostic (--dce-debug), voir le guide.
    "dce": {
      "enabled": true,
      "steps": [
        { "type": "get", "url": "{dceUrl}" },
        { "type": "link", "selector": "a[href*='TelechargementDce']" },
        { "type": "form", "formSelector": "form", "submitName": "ctl0$CONTENU_PAGE$validateButton",
          "postbackTargetField": "PRADO_POSTBACK_TARGET", "optional": true },
        { "type": "link", "selector": "a[href*='DownloadCompleteDce']" }
      ]
    },
    "maxPages": 30,
    "defaultRegion": "Grand Est",
    "keepBuyerPatterns": []
  },

  // Exemple de profil acheteur ajouté sans recompiler (désactivé par défaut).
  "customConnectors": [
    {
      "id": "exemple-profil",
      "name": "Exemple de profil acheteur",
      "enabled": false,
      "baseUrl": "https://profil-acheteur.example.org/",
      "searchUrl": "consultations/recherche",
      "formSelector": "form#recherche",
      "formFields": { "dateDebut": "{dateStart}", "dateFin": "{dateEnd}" },
      "submitName": "rechercher",
      "rowSelector": "table#resultats tbody tr",
      "fields": {
        "reference": "td.ref",
        "title": "td.objet a",
        "buyer": "td.acheteur",
        "deadline": "td.cloture",
        "url": "td.objet a@href"
      },
      "paging": { "mode": "link", "nextSelector": "a.suivant" },
      "dce": { "enabled": false, "steps": [ { "type": "get", "url": "{dceUrl}" }, { "type": "link", "selector": "a.telecharger-dce" } ] },
      "defaultRegion": "Grand Est"
    }
  ]
}
""";
}
