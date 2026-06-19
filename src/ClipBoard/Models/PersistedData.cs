namespace ClipBoard.Models;

public class PersistedData
{
    public List<FavoriteFolder> Folders { get; set; } = new();
    public List<ClipItem> PinnedHistory { get; set; } = new();
    public List<ClipItem> Favorites { get; set; } = new();
    public List<ClipItem> History { get; set; } = new();
    public ClipBoard.Services.AppSettings Settings { get; set; } = new();
}
