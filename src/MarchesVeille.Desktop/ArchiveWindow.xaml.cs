using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MarchesVeille.Core.Models;

namespace MarchesVeille.Desktop;

public sealed class ArchiveRow
{
    public ArchiveRow(ArchiveHit hit)
    {
        Hit = hit;
        TitleMarkup = string.IsNullOrEmpty(hit.TitleHighlight) ? hit.Archive.Title : hit.TitleHighlight;
    }
    public ArchiveHit Hit { get; }
    public DceArchive Archive => Hit.Archive;
    public string TitleMarkup { get; }
    public string Buyer => Archive.BuyerName;
    public string Title => Archive.Title;
    public DateTime? Published => Archive.PublicationDate?.ToLocalTime();
    public DateTime? Downloaded => Archive.DownloadedAt?.ToLocalTime();
    public string TypeLabel => string.Join(" · ", new[] { Archive.MarketType, Archive.IsPi ? "PI" : null }.Where(s => !string.IsNullOrEmpty(s)));
    public int FileCount => Archive.FileCount;
    public long SizeBytes => Archive.SizeBytes;
    public string SizeLabel => Archive.SizeBytes >= 1_048_576 ? $"{Archive.SizeBytes / 1_048_576.0:F1} Mo" : $"{Archive.SizeBytes / 1024.0:F0} Ko";
}

public partial class ArchiveWindow : Window
{
    private readonly AppServices _s;
    private readonly ObservableCollection<ArchiveRow> _rows = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _version;
    private bool _suspend;

    public ArchiveWindow(AppServices services)
    {
        _s = services;
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RunSearchAsync(); };
        Loaded += async (_, _) => await RunSearchAsync();
    }

    private ArchiveFilters CurrentFilters() => new()
    {
        Text = SearchBox.Text,
        Scope = ScopeCombo.SelectedIndex switch { 1 => ArchiveScope.Metadata, 2 => ArchiveScope.FileNames, 3 => ArchiveScope.Content, _ => ArchiveScope.All },
        Buyer = BuyerBox.Text,
        Reference = RefBox.Text,
        MarketType = TypeCombo.SelectedIndex switch { 1 => "Travaux", 2 => "Fournitures", 3 => "Services", 4 => "PI", _ => null },
        Region = RegionCombo.SelectedIndex == 1 ? "Grand Est" : null,
        PublishedFrom = PubFrom.SelectedDate,
        PublishedTo = PubTo.SelectedDate,
        DownloadedFrom = DlFrom.SelectedDate,
        DownloadedTo = DlTo.SelectedDate,
        Sort = SortCombo.SelectedIndex switch { 1 => ArchiveSort.PublishedDesc, 2 => ArchiveSort.DownloadedDesc, _ => ArchiveSort.Relevance },
        Limit = 500
    };

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suspend) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _debounce.Stop();
        await RunSearchAsync();
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        _suspend = true;
        try
        {
            SearchBox.Text = ""; BuyerBox.Text = ""; RefBox.Text = "";
            ScopeCombo.SelectedIndex = 0; TypeCombo.SelectedIndex = 0; RegionCombo.SelectedIndex = 0; SortCombo.SelectedIndex = 0;
            PubFrom.SelectedDate = null; PubTo.SelectedDate = null; DlFrom.SelectedDate = null; DlTo.SelectedDate = null;
        }
        finally { _suspend = false; }
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        var f = CurrentFilters();
        var version = ++_version;
        try
        {
            var (hits, total) = await Task.Run(() => (_s.Archives.Search(f), _s.Archives.Count(f)));
            if (version != _version) return;
            _rows.Clear();
            foreach (var h in hits) _rows.Add(new ArchiveRow(h));
            CountText.Text = total > hits.Count ? $"{hits.Count} affichées sur {total} archives" : $"{total} archive(s)";
        }
        catch (Exception ex) { CountText.Text = "Erreur : " + ex.Message; }
    }

    private ArchiveRow? Selected => ResultsGrid.SelectedItem as ArchiveRow;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Selected;
        if (row == null)
        {
            DetailTitle.Text = "Sélectionnez une archive";
            DetailMeta.Text = ""; SnippetBlock.Text = ""; FilesBox.Text = "";
            BtnOpen.IsEnabled = BtnFolder.IsEnabled = BtnNotice.IsEnabled = false;
            return;
        }
        var a = row.Archive;
        DetailTitle.Text = a.Title;
        DetailMeta.Text =
            $"Acheteur : {a.BuyerName}\nRéférence : {a.Reference}   ·   Source : {a.SourceId}\n" +
            $"Publié : {a.PublicationDate?.ToLocalTime():dd/MM/yyyy}   ·   Récupéré : {a.DownloadedAt?.ToLocalTime():dd/MM/yyyy}\n" +
            $"Type : {(string.IsNullOrEmpty(row.TypeLabel) ? "—" : row.TypeLabel)}   ·   {a.FileCount} fichier(s)   ·   {row.SizeLabel}\n" +
            $"Archive : {a.FilePath}";
        Highlight.SetMarkup(SnippetBlock, string.IsNullOrEmpty(row.Hit.ContentSnippet) ? row.Hit.FileSnippet : row.Hit.ContentSnippet);
        FilesBox.Text = a.FileList.Replace("\n", Environment.NewLine);
        BtnOpen.IsEnabled = File.Exists(FullPath(a));
        BtnFolder.IsEnabled = BtnOpen.IsEnabled;
        BtnNotice.IsEnabled = !string.IsNullOrWhiteSpace(a.UrlNotice);
    }

    private string FullPath(DceArchive a) => _s.Collector.FullPath(a);

    private void OnOpenArchive(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var path = FullPath(row.Archive);
        if (!File.Exists(path)) { MessageBox.Show(this, "Fichier introuvable :\n" + path, "Archives DCE", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Archives DCE"); }
    }

    private void OnShowFolder(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var path = FullPath(row.Archive);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Archives DCE"); }
    }

    private void OnOpenNotice(object sender, RoutedEventArgs e)
    {
        var url = Selected?.Archive.UrlNotice;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
    }
}
