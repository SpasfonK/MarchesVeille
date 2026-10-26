using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MarchesVeille.Core.Ingestion;
using MarchesVeille.Core.Models;

namespace MarchesVeille.Desktop;

public sealed class ProfileVm
{
    public ProfileVm(DceProfile p) => Profile = p;
    public DceProfile Profile { get; }
    public string Display => (Profile.Enabled ? "● " : "○ ") + Profile.Name + (Profile.MatchesEverything ? "  (tout)" : "");
}

public partial class DceWindow : Window
{
    private readonly AppServices _s;
    private readonly ObservableCollection<ProfileVm> _profiles = new();
    private DceProfile _current = new();
    private CancellationTokenSource? _cts;

    public DceWindow(AppServices services)
    {
        _s = services;
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;
        DaysCombo.ItemsSource = new[] { 15, 31, 45, 90, 180, 365 };
        DaysCombo.SelectedItem = _s.Config.Dce.PublishedWithinDays is var d && new[] { 15, 31, 45, 90, 180, 365 }.Contains(d) ? d : 45;
        Loaded += async (_, _) => await ReloadAsync(null);
        Closing += (_, _) => _cts?.Cancel();
    }

    private async Task ReloadAsync(long? selectId)
    {
        var list = await Task.Run(() => _s.Archives.ListProfiles());
        _profiles.Clear();
        foreach (var p in list) _profiles.Add(new ProfileVm(p));
        var sel = _profiles.FirstOrDefault(p => p.Profile.Id == selectId) ?? _profiles.FirstOrDefault();
        ProfileList.SelectedItem = sel;
        if (sel == null) { _current = new DceProfile { Name = "Nouveau profil" }; ToEditor(_current); }
    }

    private static List<string> Lines(string text) =>
        text.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void ToEditor(DceProfile p)
    {
        NameBox.Text = p.Name;
        EnabledBox.IsChecked = p.Enabled;
        KeywordsBox.Text = string.Join(Environment.NewLine, p.Keywords);
        ExcludeBox.Text = string.Join(Environment.NewLine, p.ExcludeKeywords);
        BuyersBox.Text = string.Join(Environment.NewLine, p.Buyers);
        RegionsBox.Text = string.Join(Environment.NewLine, p.Regions);
        CategoriesBox.Text = string.Join(Environment.NewLine, p.Categories);
        bool Has(string t) => p.MarketTypes.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase));
        ChkTravaux.IsChecked = Has("Travaux");
        ChkFournitures.IsChecked = Has("Fournitures");
        ChkServices.IsChecked = Has("Services");
        ChkPi.IsChecked = Has("PI");
    }

    private DceProfile FromEditor()
    {
        var types = new List<string>();
        if (ChkTravaux.IsChecked == true) types.Add("Travaux");
        if (ChkFournitures.IsChecked == true) types.Add("Fournitures");
        if (ChkServices.IsChecked == true) types.Add("Services");
        if (ChkPi.IsChecked == true) types.Add("PI");
        return new DceProfile
        {
            Id = _current.Id,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Profil" : NameBox.Text.Trim(),
            Enabled = EnabledBox.IsChecked == true,
            Keywords = Lines(KeywordsBox.Text),
            ExcludeKeywords = Lines(ExcludeBox.Text),
            Buyers = Lines(BuyersBox.Text),
            Regions = Lines(RegionsBox.Text),
            Categories = Lines(CategoriesBox.Text),
            MarketTypes = types
        };
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is ProfileVm vm) { _current = vm.Profile; ToEditor(_current); }
    }

    private void OnNewProfile(object sender, RoutedEventArgs e)
    {
        ProfileList.SelectedItem = null;
        _current = new DceProfile { Name = "Nouveau profil" };
        ToEditor(_current);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private async void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var p = FromEditor();
        var id = await Task.Run(() => _s.Archives.SaveProfile(p));
        await ReloadAsync(id);
    }

    private async void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileVm vm) return;
        if (MessageBox.Show(this, $"Supprimer le profil « {vm.Profile.Name} » ?", "Collecte des DCE", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Task.Run(() => _s.Archives.DeleteProfile(vm.Profile.Id));
        await ReloadAsync(null);
    }

    private void Log(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var profiles = (await Task.Run(() => _s.Archives.ListProfiles())).Where(p => p.Enabled).ToList();
        if (profiles.Count == 0)
        {
            MessageBox.Show(this, "Aucun profil actif. Enregistrez au moins un profil (sans critère = tout télécharger).", "Collecte des DCE");
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RunBtn.IsEnabled = false;
        CancelBtn.Visibility = Visibility.Visible;
        Bar.Value = 0;
        LogBox.Clear();
        Log($"Collecte lancée : {profiles.Count} profil(s) actif(s).");

        var options = new DceRunOptions
        {
            PublishedWithinDays = DaysCombo.SelectedItem is int d ? d : null,
            RetryBlocked = ChkRetry.IsChecked == true,
            Debug = ChkDebug.IsChecked == true
        };
        var progress = new Progress<DceProgress>(p =>
        {
            StatText.Text = $"{p.Done}/{p.Total}";
            Bar.Value = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            Log(p.Message);
        });

        try
        {
            var summary = await Task.Run(() => _s.Collector.RunAsync(profiles, options, progress, ct), ct);
            Bar.Value = 100;
            Log(summary.ToText());
            if (options.Debug) Log($"Pages de diagnostic : {System.IO.Path.Combine(_s.Collector.RootFolder, "_diagnostic")}");
        }
        catch (OperationCanceledException) { Log("Collecte annulée."); }
        catch (Exception ex) { Log("Erreur : " + ex.Message); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RunBtn.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
