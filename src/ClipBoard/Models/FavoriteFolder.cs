using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ClipBoard.Models;

public enum FolderKind { Normal = 0, Meme = 1 }

public class FavoriteFolder : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(); } }
    }

    public int Order { get; set; }

    public FolderKind Kind { get; set; } = FolderKind.Normal;

    [JsonIgnore]
    public System.Collections.ObjectModel.ObservableCollection<ClipItem> Items { get; }
        = new System.Collections.ObjectModel.ObservableCollection<ClipItem>();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
