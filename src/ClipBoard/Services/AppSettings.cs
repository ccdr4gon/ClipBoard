using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipBoard.Services;

public class AppSettings : INotifyPropertyChanged
{
    private bool _showInvisibleChars;
    public bool ShowInvisibleChars
    {
        get => _showInvisibleChars;
        set { if (_showInvisibleChars != value) { _showInvisibleChars = value; OnChanged(); } }
    }

    private bool _startWithWindows = true;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { if (_startWithWindows != value) { _startWithWindows = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
