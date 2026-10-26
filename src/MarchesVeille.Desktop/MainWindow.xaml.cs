using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Ingestion;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Providers;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Desktop;

public sealed class SavedSearchVm
{
    public SavedSearchVm(SavedSearch s, int newCount) { Search = s; NewCount = newCount; }
    public SavedSearch Search { get; }
    public int NewCount { get; }
    public string Display => NewCount > 0 ? $"{Search.Name}   ·   {NewCount} nouveau(x)" : Search.Name;
}

public partial class MainWindow : Window
{
    private readonly NoticeRepository _repo;
    private readonly ConnectorsConfig _cfg;
    private readonly ConnectorRegistry _registry;
    private readonly IngestionService _ingestion;
    private readonly AppServices _services;
    private HashSet<string> _archived = new();

    private readonly ObservableCollection<ResultRow> _rows = new();
    private readonly ObservableCollection<SavedSearchVm> _saved = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private int _searchVersion;
    private bool _suspend;
    private CancellationTokenSource? _syncCts;

    public MainWindow()
    {
        InitializeComponent();

        var dataDir = AppPaths.DataDir;
        try { _cfg = ConnectorsConfig.LoadOrCreate(Path.Combine(dataDir, "connectors.json")); }
        catch (JsonException ex)
        {
            MessageBox.Show(this, "connectors.json est invalide, la configuration par défaut est utilisée.\n\n" + ex.Message,
                "Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            _cfg = ConnectorsConfig.Default();
        }

        var db = new Database(Path.Combine(dataDir, "marches.db"));
        db.Migrate();
        _services = new AppServices(dataDir, _cfg, db);
        _repo = _services.Notices;
        _registry = _services.Registry;
        _ingestion = new IngestionService(_repo, new VnfTargetProfile(_cfg.TaggingRules), new NoticeClassifier(_cfg.Classification));

        ResultsGrid.ItemsSource = _rows;
        SavedList.ItemsSource = _saved;
        SourceCombo.ItemsSource = new[] { "all" }.Concat(_registry.All.Select(p => p.SourceId)).ToList();
        SourceCombo.SelectedIndex = 0;
        DaysCombo.ItemsSource = new[] { 1, 3, 7, 14, 31 };
        DaysCombo.SelectedItem = 3;

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RunSearchAsync(); };
        Loaded += async (_, _) => { await RunSearchAsync(); await RefreshSavedAsync(); };
        Closing += (_, _) => _syncCts?.Cancel();
    }

    // ------------------------------------------------------------ recherche
    private SearchFilters CurrentFilters() => new()
    {
        Text = SearchBox.Text,
        Region = ChipGrandEst.IsChecked == true ? "Grand Est" : null,
        BuyerCategory = ChipVnf.IsChecked == true ? "VNF-DTNE" : null,
        MarketType = TypeCombo.SelectedIndex switch { 1 => "Travaux", 2 => "Fournitures", 3 => "Services", 4 => "PI", _ => null },
        OnlyActive = ChipActive.IsChecked == true,
        OnlyUnread = ChipUnread.IsChecked == true,
        OnlyFavorites = ChipFav.IsChecked == true,
        DeadlineWithinDays = DeadlineSlider.Value >= 1 ? (int)DeadlineSlider.Value : null,
        Sort = SortCombo.SelectedIndex switch { 1 => SortMode.PublicationDesc, 2 => SortMode.DeadlineAsc, _ => SortMode.Relevance },
        Limit = 500
    };

    private void ApplyFiltersToUi(SearchFilters f)
    {
        _suspend = true;
        try
        {
            SearchBox.Text = f.Text ?? "";
            ChipGrandEst.IsChecked = f.Region == "Grand Est";
            ChipVnf.IsChecked = f.BuyerCategory == "VNF-DTNE";
            ChipActive.IsChecked = f.OnlyActive;
            TypeCombo.SelectedIndex = f.MarketType switch { "Travaux" => 1, "Fournitures" => 2, "Services" => 3, "PI" => 4, _ => 0 };
            ChipUnread.IsChecked = f.OnlyUnread;
            ChipFav.IsChecked = f.OnlyFavorites;
            DeadlineSlider.Value = f.DeadlineWithinDays ?? 0;
            SortCombo.SelectedIndex = f.Sort switch { SortMode.PublicationDesc => 1, SortMode.DeadlineAsc => 2, _ => 0 };
            UpdateDeadlineLabel();
        }
        finally { _suspend = false; }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suspend) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDeadlineChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDeadlineLabel();
        OnFilterChanged(sender, e);
    }

    private void UpdateDeadlineLabel() =>
        DeadlineLabel.Text = DeadlineSlider.Value >= 1 ? $"Échéance ≤ {(int)DeadlineSlider.Value} j" : "Échéance : toutes";

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _debounce.Stop();
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        var f = CurrentFilters();
        var version = ++_searchVersion;
        try
        {
            var (results, total, archived) = await Task.Run(() => (_repo.Search(f), _repo.Count(f), _services.Archives.ArchivedNoticeIds()));
            if (version != _searchVersion) return;
            _archived = archived;

            var keepId = (ResultsGrid.SelectedItem as ResultRow)?.Notice.Id;
            _rows.Clear();
            foreach (var r in results) _rows.Add(new ResultRow(r) { HasDce = archived.Contains(r.Notice.Id) });
            CountText.Text = total > results.Count ? $"{results.Count} affichés sur {total} avis" : $"{total} avis";

            if (keepId != null)
            {
                var again = _rows.FirstOrDefault(r => r.Notice.Id == keepId);
                if (again != null) ResultsGrid.SelectedItem = again;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Erreur de recherche : " + ex.Message;
        }
    }

    // ------------------------------------------------------------ détail
    private ResultRow? Selected => ResultsGrid.SelectedItem as ResultRow;

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Selected;
        if (row == null)
        {
            DetailTitle.Text = "Sélectionnez un avis";
            DetailMeta.Text = "";
            DetailText.Text = "";
            foreach (var b in new[] { BtnOpen, BtnDce, BtnFav, BtnRead, BtnArchive }) b.IsEnabled = false;
            return;
        }

        var n = row.Notice;
        DetailTitle.Text = n.Title;
        DetailMeta.Text =
            $"Acheteur : {n.BuyerName}\n" +
            $"Référence : {n.Reference}   ·   Source : {n.SourceId}\n" +
            $"Département : {n.Department ?? "—"}   ·   Région : {n.Region ?? "—"}\n" +
            $"Publié : {n.PublicationDate?.ToLocalTime():dd/MM/yyyy}   ·   Date limite : {(n.DeadlineDate.HasValue ? n.DeadlineDate.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "—")}" +
            (string.IsNullOrEmpty(n.BuyerCategory) ? "" : $"\nTag : {n.BuyerCategory}");
        DetailText.Text = n.Description;
        BtnOpen.IsEnabled = !string.IsNullOrWhiteSpace(n.UrlNotice);
        BtnDce.IsEnabled = !string.IsNullOrWhiteSpace(n.UrlDce);
        BtnFav.IsEnabled = BtnRead.IsEnabled = BtnArchive.IsEnabled = true;
        RefreshFlagButtons(row);

        if (!row.IsRead)
        {
            try
            {
                await Task.Run(() => _repo.SetRead(n.Id, true));
                row.IsRead = true;
                RefreshFlagButtons(row);
            }
            catch (Exception ex) { StatusText.Text = "Erreur : " + ex.Message; }
        }
    }

    private void RefreshFlagButtons(ResultRow row)
    {
        BtnFav.Content = row.IsFavorite ? "★ Retirer des favoris" : "☆ Favori";
        BtnRead.Content = row.IsRead ? "Marquer non lu" : "Marquer lu";
        BtnArchive.Content = row.HasDce ? "📦 Ouvrir l'archive DCE" : "Télécharger le DCE (anonyme)";
    }

    private async void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var v = !row.IsFavorite;
        await Task.Run(() => _repo.SetFavorite(row.Notice.Id, v));
        row.IsFavorite = v;
        RefreshFlagButtons(row);
    }

    private async void OnToggleRead(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var v = !row.IsRead;
        await Task.Run(() => _repo.SetRead(row.Notice.Id, v));
        row.IsRead = v;
        RefreshFlagButtons(row);
    }

    private void OnOpenNotice(object sender, RoutedEventArgs e) => OpenUrl(Selected?.Notice.UrlNotice);
    private void OnOpenDce(object sender, RoutedEventArgs e) => OpenUrl(Selected?.Notice.UrlDce);

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Impossible d'ouvrir le lien : " + ex.Message; }
    }

    // ------------------------------------------------------------ veilles
    private async Task RefreshSavedAsync()
    {
        try
        {
            var items = await Task.Run(() => _repo.ListSavedSearches().Select(s => new SavedSearchVm(s, _repo.CountNew(s))).ToList());
            _saved.Clear();
            foreach (var i in items) _saved.Add(i);
            var totalNew = items.Where(i => i.Search.AlertEnabled).Sum(i => i.NewCount);
            SavedTab.Header = totalNew > 0 ? $"Veilles programmées ({totalNew})" : "Veilles programmées";
        }
        catch (Exception ex) { StatusText.Text = "Erreur veilles : " + ex.Message; }
    }

    private async void OnSaveSearch(object sender, RoutedEventArgs e)
    {
        var name = Prompt.Ask(this, "Nouvelle veille", "Nom de la veille :", string.IsNullOrWhiteSpace(SearchBox.Text) ? "Ma veille" : SearchBox.Text.Trim());
        if (name == null) return;
        var filters = CurrentFilters();
        var s = new SavedSearch { Name = name, Filters = filters, CreatedAt = DateTime.UtcNow, LastRunAt = DateTime.UtcNow };
        await Task.Run(() => _repo.SaveSearch(s));
        await RefreshSavedAsync();
    }

    private async void OnApplySaved(object sender, RoutedEventArgs e)
    {
        if (SavedList.SelectedItem is not SavedSearchVm vm) return;
        ApplyFiltersToUi(vm.Search.Filters);
        await RunSearchAsync();
        await Task.Run(() => _repo.TouchSavedSearch(vm.Search.Id, DateTime.UtcNow));
        await RefreshSavedAsync();
    }

    private async void OnDeleteSaved(object sender, RoutedEventArgs e)
    {
        if (SavedList.SelectedItem is not SavedSearchVm vm) return;
        await Task.Run(() => _repo.DeleteSavedSearch(vm.Search.Id));
        await RefreshSavedAsync();
    }

    // ------------------------------------------------------------ DCE
    private async void OnArchiveAction(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var n = row.Notice;

        if (row.HasDce)
        {
            var a = await Task.Run(() => _services.Archives.GetByNotice(n.Id));
            if (a != null) OpenFile(_services.Collector.FullPath(a));
            return;
        }

        BtnArchive.IsEnabled = false;
        StatusText.Text = "Téléchargement anonyme du DCE…";
        try
        {
            var archive = await Task.Run(() => _services.Collector.DownloadSingleAsync(n, debug: false, CancellationToken.None));
            switch (archive.Status)
            {
                case DceStatus.Ok:
                    row.HasDce = true;
                    StatusText.Text = $"DCE archivé : {archive.FilePath}";
                    break;
                case DceStatus.Restricted:
                    StatusText.Text = "DCE soumis à identification : téléchargement anonyme impossible.";
                    MessageBox.Show(this, "Ce portail demande une identification ou une inscription pour ce DCE.\nUtilisez « Accéder au DCE » pour le récupérer dans votre navigateur.",
                        "DCE", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                default:
                    StatusText.Text = "DCE non récupéré : " + archive.Error;
                    break;
            }
        }
        catch (Exception ex) { StatusText.Text = "Erreur DCE : " + ex.Message; }
        finally { RefreshFlagButtons(row); BtnArchive.IsEnabled = true; }
    }

    private void OpenFile(string path)
    {
        if (!File.Exists(path)) { StatusText.Text = "Archive introuvable : " + path; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Impossible d'ouvrir l'archive : " + ex.Message; }
    }

    private void OnOpenCollect(object sender, RoutedEventArgs e) => new DceWindow(_services) { Owner = this }.Show();
    private void OnOpenArchives(object sender, RoutedEventArgs e) => new ArchiveWindow(_services) { Owner = this }.Show();

    // ------------------------------------------------------------ synchronisation
    private async void OnSync(object sender, RoutedEventArgs e)
    {
        if (_syncCts != null) return;
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;
        SyncBtn.IsEnabled = false;
        CancelBtn.Visibility = Visibility.Visible;
        SyncBar.Value = 0;

        var source = SourceCombo.SelectedItem as string ?? "all";
        var days = DaysCombo.SelectedItem is int d ? d : 3;
        var progress = new Progress<IngestionProgress>(p =>
        {
            StatusText.Text = $"[{p.SourceId}] {p.Message}";
            SyncBar.Value = 100.0 * p.SourcesDone / Math.Max(1, p.SourcesTotal);
        });

        try
        {
            var providers = _registry.Select(source);
            var summary = await Task.Run(() => _ingestion.RunAsync(providers, new ScrapingOptions { Days = days }, progress, ct), ct);
            SyncBar.Value = 100;
            StatusText.Text = "Synchronisation terminée : " + summary.ToOneLine();
            if (summary.HasErrors)
            {
                var errors = string.Join("\n", summary.Sources.Where(s => s.Error != null).Select(s => $"• {s.SourceId} : {s.Error}"));
                MessageBox.Show(this, "Certaines sources ont échoué :\n\n" + errors, "Synchronisation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (ChkDce.IsChecked == true)
            {
                var profiles = (await Task.Run(() => _services.Archives.ListProfiles())).Where(p => p.Enabled).ToList();
                var dceProgress = new Progress<DceProgress>(p =>
                {
                    StatusText.Text = $"DCE {p.Done}/{p.Total} — {p.Message}";
                    SyncBar.Value = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
                });
                var dce = await Task.Run(() => _services.Collector.RunAsync(profiles, new DceRunOptions(), dceProgress, ct), ct);
                SyncBar.Value = 100;
                StatusText.Text = "Terminé : " + summary.ToOneLine() + " — " + dce.ToOneLine();
                if (dce.Messages.Count > 0)
                    MessageBox.Show(this, dce.ToText(), "Collecte des DCE", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            await RunSearchAsync();
            await RefreshSavedAsync();
        }
        catch (OperationCanceledException) { StatusText.Text = "Synchronisation annulée."; }
        catch (Exception ex)
        {
            StatusText.Text = "Erreur de synchronisation : " + ex.Message;
            MessageBox.Show(this, ex.Message, "Synchronisation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;
            SyncBtn.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelSync(object sender, RoutedEventArgs e) => _syncCts?.Cancel();
}
