using System.ComponentModel;
using System.Runtime.CompilerServices;
using MarchesVeille.Core.Models;

namespace MarchesVeille.Desktop;

public sealed class ResultRow : INotifyPropertyChanged
{
    private bool _isRead;
    private bool _isFavorite;

    public ResultRow(SearchResult r)
    {
        Notice = r.Notice;
        TitleMarkup = string.IsNullOrEmpty(r.TitleHighlight) ? r.Notice.Title : r.TitleHighlight;
        _isRead = r.Notice.IsRead;
        _isFavorite = r.Notice.IsFavorite;

        if (Notice.DeadlineDate is DateTime dl)
        {
            var days = (dl - DateTime.UtcNow).TotalDays;
            if (days < 0) { Urgency = "Expired"; DaysLeftText = "clos"; }
            else
            {
                var d = (int)Math.Ceiling(days);
                DaysLeftText = $"J-{d}";
                Urgency = d <= 3 ? "J3" : d <= 7 ? "J7" : "None";
            }
        }
        else { Urgency = "None"; DaysLeftText = ""; }
    }

    public Notice Notice { get; }
    public string TitleMarkup { get; }
    public string Urgency { get; }
    public string DaysLeftText { get; }

    public string BuyerName => Notice.BuyerName;
    public string Department => Notice.Department ?? "";
    public string Category => Notice.BuyerCategory ?? "";
    public DateTime? Published => Notice.PublicationDate?.ToLocalTime();
    public DateTime? Deadline => Notice.DeadlineDate?.ToLocalTime();

    public bool IsRead { get => _isRead; set { if (_isRead != value) { _isRead = value; OnChanged(); } } }
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite != value) { _isFavorite = value; OnChanged(); OnChanged(nameof(FavMark)); } }
    }
    public string FavMark => _isFavorite ? "★" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
