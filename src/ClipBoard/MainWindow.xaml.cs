using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBoard.Models;
using ClipBoard.Services;

namespace ClipBoard;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ClipItem> _combinedHistory = new();
    private readonly ObservableCollection<ClipItem> _imageView = new();
    private readonly ObservableCollection<ClipItem> _emojiView = new();
    private readonly Dictionary<FavoriteFolder, TabItem> _folderTabs = new();
    private TabItem? _historyTab;
    private TabItem? _imageTab;
    private TabItem? _emojiTab;
    private TabItem? _memeTab;
    private DateTime _lastShownUtc = DateTime.MinValue;

    private Point _dragStartPoint;
    private ClipItem? _pendingDragItem;
    private bool _suppressDeactivate;
    private bool _pinned;
    private readonly List<ClipItem> _deferredPromotions = new();
    private bool _isDraggingOut;
    private ContextMenu? _openContextMenu;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BuildTabs();
            SearchBox.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                if (!SearchBox.IsKeyboardFocusWithin)
                {
                    SearchBox.Focus();
                    Keyboard.Focus(SearchBox);
                }
            };
            SearchBox.LostKeyboardFocus += (_, _) =>
            {
                if (_pinned) SetNoActivateStyle(true);
            };
        };
        SourceInitialized += OnSourceInitialized;
        Activated += (_, _) => Log($"ACTIVATED pinned={_pinned} fg={ForegroundHex()}");
        Deactivated += (_, _) => Log($"DEACTIVATED pinned={_pinned} fg={ForegroundHex()}");
        App.Favorites.Folders.CollectionChanged += OnFoldersChanged;
        App.Favorites.PinnedHistory.CollectionChanged += OnPinnedChanged;
        App.History.Items.CollectionChanged += OnHistoryChanged;
        _combinedHistory.CollectionChanged += (_, _) =>
        {
            RebuildSmartViews();
            UpdateEntryCount();
        };
        RebuildCombinedHistory();
        Log("MainWindow constructed");
    }

    private void UpdateEntryCount()
    {
        var lb = GetActiveListBox();
        int count = lb?.Items.Count ?? 0;
        EntryCountText.Text = $"— {count} entries —";
    }

    private void UpdateTitleCounter()
    {
        int total = _combinedHistory.Count;
        foreach (var f in App.Favorites.Folders)
            total += f.Items.Count;
        TitleCounter.Text = $"NO. {total:D3}";
    }

    private void RebuildSmartViews()
    {
        _imageView.Clear();
        _emojiView.Clear();
        foreach (var it in _combinedHistory)
        {
            if (it.Kind == ClipKind.Image) _imageView.Add(it);
            else if (it.Kind == ClipKind.Text && IsPureEmoji(it.Text)) _emojiView.Add(it);
        }
    }

    private static bool IsPureEmoji(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        int emojiCount = 0;
        while (e.MoveNext())
        {
            var elem = (string)e.Current!;
            if (elem.All(char.IsWhiteSpace)) continue;
            bool found = false;
            foreach (var rune in elem.EnumerateRunes())
            {
                int cp = rune.Value;
                if ((cp >= 0x1F000 && cp <= 0x1FFFF)
                    || (cp >= 0x2600 && cp <= 0x27BF)
                    || (cp >= 0x2190 && cp <= 0x21FF)
                    || (cp >= 0x2300 && cp <= 0x23FF)
                    || cp == 0x200D || cp == 0xFE0F || cp == 0x20E3
                    || (cp >= 0xE0020 && cp <= 0xE007F))
                { found = true; break; }
            }
            if (!found) return false;
            emojiCount++;
        }
        return emojiCount > 0;
    }

    private string ForegroundHex()
    {
        try { return GetForegroundWindow().ToInt64().ToString("X"); } catch { return "?"; }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(hwnd);
        src?.AddHook(DropFilesHook);
        src?.AddHook(MouseActivateHook);
        DragAcceptFiles(hwnd, true);
        Log($"SourceInitialized hwnd={hwnd.ToInt64():X} DragAcceptFiles called");
    }

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private IntPtr _preClickForeground = IntPtr.Zero;
    private IntPtr _foregroundBeforeShow = IntPtr.Zero;

    private bool IsClickOnSearchBox()
    {
        try
        {
            GetCursorPos(out var p);
            var local = PointFromScreen(new System.Windows.Point(p.X, p.Y));
            var sbLoc = SearchBox.TranslatePoint(new System.Windows.Point(0, 0), this);
            var sbSize = new System.Windows.Size(SearchBox.ActualWidth, SearchBox.ActualHeight);
            Log($"HitTest screen=({p.X},{p.Y}) local=({local.X:F0},{local.Y:F0}) SB@({sbLoc.X:F0},{sbLoc.Y:F0}) {sbSize.Width:F0}x{sbSize.Height:F0}");
            // Fallback: direct bounding-box check
            if (local.X >= sbLoc.X && local.X <= sbLoc.X + sbSize.Width
                && local.Y >= sbLoc.Y && local.Y <= sbLoc.Y + sbSize.Height)
            {
                Log("  match via bounding-box");
                return true;
            }
            Log("  no bbox match");
        }
        catch (Exception ex) { Log("IsClickOnSearchBox FAILED: " + ex.Message); }
        return false;
    }

    private IntPtr MouseActivateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE && _pinned)
        {
            if (IsClickOnSearchBox())
            {
                // Temporarily allow activation so the search box can receive keyboard focus.
                SetNoActivateStyle(false);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var myHwnd = new WindowInteropHelper(this).Handle;
                    SetForegroundWindow(myHwnd);
                    SearchBox.Focus();
                    Keyboard.Focus(SearchBox);
                }), System.Windows.Threading.DispatcherPriority.Input);
                Log("WM_MOUSEACTIVATE allowed for SearchBox (pinned)");
                return IntPtr.Zero;
            }
            var fg = GetForegroundWindow();
            var myHwnd2 = new WindowInteropHelper(this).Handle;
            if (fg != IntPtr.Zero && fg != myHwnd2) _preClickForeground = fg;
            Log($"WM_MOUSEACTIVATE suppressed (pinned), remembered fg={fg.ToInt64():X}");
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    private const int WM_DROPFILES = 0x0233;
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder? lpszFile, uint cch);
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    private IntPtr DropFilesHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DROPFILES)
        {
            try
            {
                var hDrop = wParam;
                uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                var files = new string[count];
                var sb = new System.Text.StringBuilder(260);
                for (uint i = 0; i < count; i++)
                {
                    sb.Length = 0;
                    sb.EnsureCapacity(260);
                    DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    files[i] = sb.ToString();
                }
                DragFinish(hDrop);
                Log($"WM_DROPFILES count={count}");
                var target = GetCurrentDropTarget();
                if (files.Length > 0) AddItem(target, ClipKind.Files, files: files);
                handled = true;
            }
            catch (Exception ex) { Log("WM_DROPFILES FAILED: " + ex); }
        }
        return IntPtr.Zero;
    }

    private static StackPanel MakeTabHeader(string zhName, string enName)
    {
        var serif = (FontFamily)Application.Current.Resources["Serif"];
        var mono = (FontFamily)Application.Current.Resources["Mono"];
        var muted = (SolidColorBrush)Application.Current.Resources["Muted"];
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = zhName, FontFamily = serif, FontSize = 15, FontWeight = FontWeights.SemiBold
        });
        sp.Children.Add(new TextBlock
        {
            Text = enName, FontFamily = mono, FontSize = 9, Foreground = muted
        });
        return sp;
    }

    private void BuildTabs()
    {
        var historyLb = BuildListBoxForCombined();
        historyLb.AlternationCount = 999;
        _historyTab = new TabItem { Header = MakeTabHeader("历史", "HISTORY"), Content = historyLb };
        Tabs.Items.Add(_historyTab);

        var imageLb = BuildListBox(_imageView, isHistoryTab: true, folder: null);
        ApplyTileStyle(imageLb, "ImageTileTemplate", "ImageTileStyle");
        _imageTab = new TabItem { Header = MakeTabHeader("图片", "IMAGES"), Content = imageLb };
        Tabs.Items.Add(_imageTab);

        var emojiLb = BuildListBox(_emojiView, isHistoryTab: true, folder: null);
        ApplyEmojiStyle(emojiLb);
        _emojiTab = new TabItem { Header = MakeTabHeader("emoji", "EMOJI"), Content = emojiLb };
        Tabs.Items.Add(_emojiTab);

        var defaultMemes = App.Favorites.EnsureDefaultMemeFolder();
        var memeLb = BuildListBox(defaultMemes.Items, isHistoryTab: false, folder: defaultMemes);
        ApplyTileStyle(memeLb, "MemeTileTemplate", "MemeTileStyle");
        _memeTab = new TabItem { Header = MakeTabHeader("表情包", "MEMES"), Content = memeLb, Tag = defaultMemes };
        _folderTabs[defaultMemes] = _memeTab;
        Tabs.Items.Add(_memeTab);

        foreach (var folder in App.Favorites.Folders)
        {
            if (folder.Id == FavoritesStore.DefaultMemeFolderId) continue;
            Tabs.Items.Add(CreateFolderTab(folder));
        }

        Tabs.SelectedItem = _historyTab;
        UpdateEntryCount();
        UpdateTitleCounter();
    }

    private static void ApplyTileStyle(ListBox lb, string templateKey, string styleKey)
    {
        lb.ItemTemplate = (DataTemplate)Application.Current.Resources[templateKey];
        lb.ItemContainerStyle = (Style)Application.Current.Resources[styleKey];
        lb.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["WrapPanelTemplate"];
        ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(lb, ScrollBarVisibility.Auto);
        lb.HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private static void ApplyEmojiStyle(ListBox lb)
    {
        lb.ItemTemplate = (DataTemplate)Application.Current.Resources["EmojiTileTemplate"];
        lb.ItemContainerStyle = (Style)Application.Current.Resources["EmojiTileStyle"];
        lb.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["WrapPanelTemplate"];
        ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(lb, ScrollBarVisibility.Auto);
    }

    private void OnFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (FavoriteFolder f in e.NewItems)
            {
                if (f.Id == FavoritesStore.DefaultMemeFolderId) continue;
                if (_folderTabs.ContainsKey(f)) continue;
                var ti = CreateFolderTab(f);
                _folderTabs[f] = ti;
                Tabs.Items.Add(ti);
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            foreach (FavoriteFolder f in e.OldItems)
            {
                if (_folderTabs.TryGetValue(f, out var ti))
                {
                    Tabs.Items.Remove(ti);
                    _folderTabs.Remove(f);
                }
            }
            if (Tabs.SelectedItem == null)
                Tabs.SelectedItem = _historyTab;
        }
        UpdateTitleCounter();
    }

    private TabItem CreateFolderTab(FavoriteFolder folder)
    {
        var serif = (FontFamily)Application.Current.Resources["Serif"];
        var mono = (FontFamily)Application.Current.Resources["Mono"];
        var muted = (SolidColorBrush)Application.Current.Resources["Muted"];
        var header = new StackPanel();
        var label = new TextBlock { FontFamily = serif, FontSize = 15 };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(FavoriteFolder.Name)) { Source = folder });
        header.Children.Add(label);
        var subtitle = folder.Kind == FolderKind.Meme ? "MEMES" : "FOLDER";
        header.Children.Add(new TextBlock { Text = subtitle, FontFamily = mono, FontSize = 9, Foreground = muted });

        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名…" };
        rename.Click += (_, _) => RenameFolder(folder);
        menu.Items.Add(rename);
        if (folder.Kind == FolderKind.Meme)
        {
            var export = new MenuItem { Header = "导出表情包…" };
            export.Click += (_, _) => new Views.ExportMemeDialog(folder, App.Persistence) { Owner = this }.Show();
            menu.Items.Add(export);
        }
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "删除收藏夹…" };
        delete.Click += (_, _) => DeleteFolder(folder);
        menu.Items.Add(delete);
        header.ContextMenu = menu;

        var ti = new TabItem { Header = header, Tag = folder, Content = BuildListBoxForFolder(folder) };
        _folderTabs[folder] = ti;
        return ti;
    }

    private ListBox BuildListBoxForCombined()
    {
        return BuildListBox(_combinedHistory, isHistoryTab: true, folder: null);
    }

    private ListBox BuildListBoxForFolder(FavoriteFolder folder)
    {
        var lb = BuildListBox(folder.Items, isHistoryTab: false, folder: folder);
        if (folder.Kind == FolderKind.Meme)
            ApplyTileStyle(lb, "MemeTileTemplate", "MemeTileStyle");
        return lb;
    }

    private ListBox BuildListBox(System.Collections.IEnumerable source, bool isHistoryTab, FavoriteFolder? folder)
    {
        var lb = new ListBox
        {
            ItemTemplate = (DataTemplate)Application.Current.Resources["ClipItemTemplate"],
            ItemContainerStyle = (Style)Application.Current.Resources["ClipListItemStyle"],
            ItemsSource = source,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Disabled);
        lb.PreviewMouseLeftButtonDown += (s, ev) =>
        {
            _dragStartPoint = ev.GetPosition(lb);
            _pendingDragItem = HitTestItem(ev.OriginalSource as DependencyObject);
            if (_pinned && _pendingDragItem != null)
            {
                lb.SelectedItem = _pendingDragItem;
                ev.Handled = true;
            }
        };
        lb.PreviewMouseMove += (s, ev) =>
        {
            if (ev.LeftButton != MouseButtonState.Pressed || _pendingDragItem == null) return;
            var delta = ev.GetPosition(lb) - _dragStartPoint;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = _pendingDragItem;
            _pendingDragItem = null;
            StartDragOut(item);
        };
        lb.PreviewMouseLeftButtonUp += (s, ev) =>
        {
            var item = _pendingDragItem;
            _pendingDragItem = null;
            if (item == null) return;
            if (!_pinned) lb.SelectedItem = item;
            Log($"Click-Up pinned={_pinned} fg_before_paste={ForegroundHex()}");
            PasteSelected(lb);
            Log($"Click-Up done fg_after_paste={ForegroundHex()}");
            ev.Handled = true;
        };
        lb.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter) { PasteSelected(lb); ev.Handled = true; }
        };
        lb.PreviewMouseRightButtonDown += (s, ev) =>
        {
            var it = HitTestItem(ev.OriginalSource as DependencyObject);
            if (it != null) lb.SelectedItem = it;
        };
        lb.ContextMenu = new ContextMenu();
        lb.ContextMenuOpening += (s, ev) =>
        {
            if (lb.SelectedItem is not ClipItem item) { ev.Handled = true; return; }
            var menu = lb.ContextMenu;
            menu.Items.Clear();
            PopulateItemContextMenu(menu, item, isHistoryTab, folder);
            _openContextMenu = menu;
            StartContextMenuAutoClose(menu);
        };
        lb.ContextMenu.Closed += (s, _) =>
        {
            if (_openContextMenu == s) _openContextMenu = null;
        };
        return lb;
    }

    private System.Windows.Threading.DispatcherTimer? _menuWatchTimer;

    private void StartContextMenuAutoClose(ContextMenu menu)
    {
        _menuWatchTimer?.Stop();
        uint myPid = GetCurrentProcessId();
        _menuWatchTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(60),
            System.Windows.Threading.DispatcherPriority.Normal, (_, _) =>
            {
                if (!menu.IsOpen) { _menuWatchTimer?.Stop(); return; }
                bool lb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                bool rb = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
                bool mb = (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;
                if (!lb && !rb && !mb) return;
                GetCursorPos(out var p);
                var hwndAt = WindowFromPoint(p);
                GetWindowThreadProcessId(hwndAt, out uint pid);
                if (pid == myPid) return;
                menu.IsOpen = false;
                _menuWatchTimer?.Stop();
            }, Dispatcher);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;

    private void PopulateItemContextMenu(ContextMenu menu, ClipItem item, bool isHistoryTab, FavoriteFolder? currentFolder)
    {
        if (isHistoryTab)
        {
            var pinMi = new MenuItem { Header = item.IsPinned ? "取消顶置" : "顶置" };
            pinMi.Click += (_, _) =>
            {
                if (item.IsPinned) App.Favorites.UnpinHistory(item, App.History);
                else App.Favorites.PinHistory(item, App.History);
            };
            menu.Items.Add(pinMi);

            var favMi = new MenuItem { Header = "收藏到" };
            bool isImageOrGif = item.Kind == ClipKind.Image || item.Kind == ClipKind.Gif;
            foreach (var f in App.Favorites.Folders)
            {
                if (f.Kind == FolderKind.Meme && !isImageOrGif) continue;
                var f1 = f;
                var prefix = f.Kind == FolderKind.Meme ? "\U0001F3A8 " : "";
                var sub = new MenuItem { Header = prefix + f.Name };
                sub.Click += (_, _) => App.Favorites.AddToFolder(item, f1);
                favMi.Items.Add(sub);
            }
            if (App.Favorites.Folders.Count > 0) favMi.Items.Add(new Separator());
            var newFolderMi = new MenuItem { Header = "新建收藏夹…" };
            newFolderMi.Click += (_, _) =>
            {
                var created = PromptCreateFolder();
                if (created != null) App.Favorites.AddToFolder(item, created);
            };
            favMi.Items.Add(newFolderMi);
            menu.Items.Add(favMi);

            if (item.Kind == ClipKind.Image)
            {
                var info = new MenuItem { Header = "查看图片信息" };
                info.Click += (_, _) => ShowImageInfo(item);
                menu.Items.Add(info);
            }

            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = "从历史删除" };

            del.Click += (_, _) =>
            {
                if (item.IsPinned) App.Favorites.PinnedHistory.Remove(item);
                else App.History.Remove(item);
                if (item.IsPinned) App.Favorites.Save();
            };
            menu.Items.Add(del);
        }
        else
        {
            var moveMi = new MenuItem { Header = "移动到" };
            bool isImageOrGif = item.Kind == ClipKind.Image || item.Kind == ClipKind.Gif;
            foreach (var f in App.Favorites.Folders)
            {
                if (f == currentFolder) continue;
                if (f.Kind == FolderKind.Meme && !isImageOrGif) continue;
                var f1 = f;
                var prefix = f.Kind == FolderKind.Meme ? "\U0001F3A8 " : "";
                var sub = new MenuItem { Header = prefix + f.Name };
                sub.Click += (_, _) => App.Favorites.MoveFavorite(item, f1);
                moveMi.Items.Add(sub);
            }
            if (moveMi.Items.Count == 0)
                moveMi.IsEnabled = false;
            menu.Items.Add(moveMi);

            if (currentFolder?.Kind == FolderKind.Meme)
            {
                var titleMi = new MenuItem { Header = "设置标题…" };
                titleMi.Click += (_, _) => EditMemeTitle(item);
                menu.Items.Add(titleMi);
            }

            if (item.Kind == ClipKind.Image)
            {
                var info = new MenuItem { Header = "查看图片信息" };
                info.Click += (_, _) => ShowImageInfo(item);
                menu.Items.Add(info);
            }

            var remove = new MenuItem { Header = "从收藏夹移除" };
            remove.Click += (_, _) => App.Favorites.RemoveFavorite(item);
            menu.Items.Add(remove);
        }
    }

    private void EditMemeTitle(ClipItem item)
    {
        var dlg = new FolderNameDialog("设置标题", item.Title ?? "", showKindPicker: false, labelText: "标题：") { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            item.Title = string.IsNullOrWhiteSpace(dlg.FolderName) ? null : dlg.FolderName.Trim();
            App.Favorites.Save();
        }
    }

    private void RebuildCombinedHistory()
    {
        _combinedHistory.Clear();
        foreach (var p in App.Favorites.PinnedHistory) _combinedHistory.Add(p);
        foreach (var i in App.History.Items) _combinedHistory.Add(i);
    }

    private void OnPinnedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildCombinedHistory();
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildCombinedHistory();
    }

    private DateTime _lastPasteUtc = DateTime.MinValue;
    private void PasteSelected(ListBox lb)
    {
        if (lb.SelectedItem is not ClipItem item) return;
        if ((DateTime.UtcNow - _lastPasteUtc).TotalMilliseconds < 150)
        {
            Log("PasteSelected throttled");
            return;
        }
        try
        {
        _lastPasteUtc = DateTime.UtcNow;
        Log($"PasteSelected kind={item.Kind} pinned={_pinned} textLen={item.Text?.Length ?? 0}");
        App.History.CopyToClipboard(item);

        if (_pinned)
        {
            _deferredPromotions.Remove(item);
            _deferredPromotions.Add(item);
        }
        else
        {
            int pinIdx = App.Favorites.PinnedHistory.IndexOf(item);
            if (pinIdx > 0) App.Favorites.PinnedHistory.Move(pinIdx, 0);
            else App.History.PromoteToTop(item);
        }

        if (_pinned)
        {
            if (_preClickForeground != IntPtr.Zero)
            {
                var target = _preClickForeground;
                var fg = GetForegroundWindow();
                var myHwnd = new WindowInteropHelper(this).Handle;
                if (fg != target) SetForegroundWindow(target);
                SendPasteKeystroke();
                Log($"Pinned paste: target={target.ToInt64():X} was_fg={(fg==target)} kind={item.Kind}");
            }
            return;
        }

        HidePanel();
        Dispatcher.BeginInvoke(new Action(SendPasteKeystroke), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex) { Log("PasteSelected FAILED: " + ex.Message); }
    }

    public void ShowPanel()
    {
        try
        {
            var mousePx = GetCursorPos();
            var waPx = GetWorkAreaForPoint((int)mousePx.X, (int)mousePx.Y);
            var hMon = MonitorFromPoint(new POINT { X = (int)mousePx.X, Y = (int)mousePx.Y }, MONITOR_DEFAULTTONEAREST);
            double scale = 1.0;
            if (GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;

            double mouseXDip = mousePx.X / scale;
            double mouseYDip = mousePx.Y / scale;
            double waLeft = waPx.Left / scale;
            double waTop = waPx.Top / scale;
            double waRight = waPx.Right / scale;
            double waBottom = waPx.Bottom / scale;

            Left = Math.Min(mouseXDip, waRight - Width - 12);
            Top = Math.Min(mouseYDip, waBottom - Height - 12);
            if (Left < waLeft + 12) Left = waLeft + 12;
            if (Top < waTop + 12) Top = waTop + 12;

            SearchBox.Text = "";
            _lastShownUtc = DateTime.UtcNow;
            var prevFg = GetForegroundWindow();
            var myHwnd = new WindowInteropHelper(this).Handle;
            if (prevFg != IntPtr.Zero && prevFg != myHwnd) _foregroundBeforeShow = prevFg;
            Show();
            Topmost = false;
            Topmost = true;
            if (!_pinned)
            {
                Activate();
                ForceForeground();
                SearchBox.Focus();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.ToString(), "ShowPanel error");
        }
    }

    [System.Runtime.InteropServices.DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) { SetForegroundWindow(hwnd); return; }
        uint fgTid = GetWindowThreadProcessId(fg, out _);
        uint myTid = GetCurrentThreadId();
        if (fgTid != myTid) AttachThreadInput(fgTid, myTid, true);
        SetForegroundWindow(hwnd);
        if (fgTid != myTid) AttachThreadInput(fgTid, myTid, false);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private static RECT GetWorkAreaForPoint(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        return mi.rcWork;
    }

    public void HidePanel()
    {
        FlushDeferredPromotions();
        Hide();
    }

    private void FlushDeferredPromotions()
    {
        if (_deferredPromotions.Count == 0) return;
        foreach (var item in _deferredPromotions)
        {
            int pinIdx = App.Favorites.PinnedHistory.IndexOf(item);
            if (pinIdx > 0) App.Favorites.PinnedHistory.Move(pinIdx, 0);
            else App.History.PromoteToTop(item);
        }
        _deferredPromotions.Clear();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        var newFg = GetForegroundWindow();
        GetWindowThreadProcessId(newFg, out uint newFgPid);
        if (newFgPid == GetCurrentProcessId()) return;

        if (_openContextMenu != null && _openContextMenu.IsOpen) _openContextMenu.IsOpen = false;
        if (_pinned) return;
        if (_suppressDeactivate) return;
        if ((DateTime.UtcNow - _lastShownUtc).TotalMilliseconds < 400) return;
        HidePanel();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinButton.Opacity = _pinned ? 1.0 : 0.4;
        PinButton.ToolTip = _pinned ? "取消固定（恢复自动隐藏）" : "固定窗口（不自动隐藏）";
        Log($"Pin toggled: pinned={_pinned}");
        SetNoActivateStyle(_pinned);
        if (_pinned && _foregroundBeforeShow != IntPtr.Zero)
        {
            var target = _foregroundBeforeShow;
            _preClickForeground = target;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetForegroundWindow(target);
                Log($"Pin: restored foreground to {target.ToInt64():X}, fg_now={ForegroundHex()}");
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    private void SetNoActivateStyle(bool on)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? (ex | WS_EX_NOACTIVATE) : (ex & ~WS_EX_NOACTIVATE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);
        Log($"WS_EX_NOACTIVATE set to {on}");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_pinned) { _pinned = false; PinButton.Opacity = 0.4; }
        HidePanel();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        var created = PromptCreateFolder();
        if (created != null && _folderTabs.TryGetValue(created, out var ti))
            Tabs.SelectedItem = ti;
    }

    private ClipBoard.Views.SettingsWindow? _settingsWindow;
    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    public void OpenSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsVisible)
        {
            _settingsWindow = new ClipBoard.Views.SettingsWindow(App.Settings) { Owner = this };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    // 内存中的 item.Image 只是缩略图；需要保真时（拖出/复制/查看信息/编码）从磁盘 blob 读全分辨率原图。
    private static BitmapSource? FullResImageFor(ClipItem item)
    {
        if (!string.IsNullOrEmpty(item.ImageBlobName))
        {
            var full = App.Persistence.LoadImageBlob(item.ImageBlobName);
            if (full != null) return full;
        }
        return item.Image;
    }

    private void ShowImageInfo(ClipItem item)
    {
        if (FullResImageFor(item) is not BitmapSource img)
        {
            System.Windows.MessageBox.Show(this, "图片不可用", "图片信息");
            return;
        }

        long rawBytes = (long)img.PixelWidth * img.PixelHeight * img.Format.BitsPerPixel / 8;
        string rawHuman = rawBytes >= 1024 * 1024
            ? $"{rawBytes / 1024.0 / 1024.0:F2} MB"
            : $"{rawBytes / 1024.0:F1} KB";

        long pngBytes = 0, jpgBytes = 0;
        try
        {
            using var msPng = new System.IO.MemoryStream();
            var enc1 = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc1.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            enc1.Save(msPng);
            pngBytes = msPng.Length;

            using var msJpg = new System.IO.MemoryStream();
            var enc2 = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
            enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            enc2.Save(msJpg);
            jpgBytes = msJpg.Length;
        }
        catch { }

        string HumanSize(long n) =>
            n >= 1024 * 1024 ? $"{n / 1024.0 / 1024.0:F2} MB" : $"{n / 1024.0:F1} KB";

        bool hasAlpha = img.Format == System.Windows.Media.PixelFormats.Bgra32
                       || img.Format == System.Windows.Media.PixelFormats.Pbgra32
                       || img.Format == System.Windows.Media.PixelFormats.Rgba128Float
                       || img.Format == System.Windows.Media.PixelFormats.Prgba64;

        var msg =
            $"尺寸：{img.PixelWidth} × {img.PixelHeight}\n" +
            $"像素格式：{img.Format}  ({img.Format.BitsPerPixel} bpp, {(hasAlpha ? "含透明通道" : "不含透明")})\n" +
            $"DPI：{img.DpiX:F0} × {img.DpiY:F0}\n" +
            $"原始（未压缩）：{rawHuman}\n" +
            (pngBytes > 0 ? $"PNG 编码：{HumanSize(pngBytes)}\n" : "") +
            (jpgBytes > 0 ? $"JPG (质量 90)：{HumanSize(jpgBytes)}\n" : "") +
            "\n（系统剪贴板里存的是原始像素，不保留原始文件格式）\n" +
            $"时间：{item.Timestamp:yyyy-MM-dd HH:mm:ss}";
        System.Windows.MessageBox.Show(this, msg, "图片信息");
    }

    private static ClipItem? HitTestItem(DependencyObject? src)
    {
        while (src != null && src is not ListBoxItem)
        {
            if (src is Visual || src is System.Windows.Media.Media3D.Visual3D)
                src = VisualTreeHelper.GetParent(src);
            else if (src is FrameworkContentElement fce)
                src = fce.Parent;
            else
                break;
        }
        return (src as ListBoxItem)?.DataContext as ClipItem;
    }

    private void StartDragOut(ClipItem item)
    {
        var data = new DataObject();
        switch (item.Kind)
        {
            case ClipKind.Text:
                if (item.Text != null) data.SetData(DataFormats.UnicodeText, item.Text);
                break;
            case ClipKind.Files:
                if (item.FilePaths is { Length: > 0 })
                {
                    var sc = new StringCollection();
                    sc.AddRange(item.FilePaths);
                    data.SetFileDropList(sc);
                }
                break;
            case ClipKind.Image:
                var dragImg = FullResImageFor(item);
                if (dragImg != null) data.SetData(DataFormats.Bitmap, dragImg);
                break;
            case ClipKind.Gif:
                if (item.GifBytes is { Length: > 0 })
                {
                    var path = Services.HistoryStore.WriteGifTemp(item.GifBytes);
                    var sc = new StringCollection();
                    sc.Add(path);
                    data.SetFileDropList(sc);
                }
                break;
            default: return;
        }

        _suppressDeactivate = true;
        _isDraggingOut = true;
        try
        {
            var effects = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
            Log($"DoDragDrop returned effects={effects}");
            if (effects != DragDropEffects.None)
            {
                int pinIdx = App.Favorites.PinnedHistory.IndexOf(item);
                if (pinIdx > 0) App.Favorites.PinnedHistory.Move(pinIdx, 0);
                else App.History.PromoteToTop(item);
                if (!_pinned) HidePanel();
            }
        }
        catch (Exception ex) { Log("DoDragDrop FAILED: " + ex); }
        finally
        {
            _suppressDeactivate = false;
            _isDraggingOut = false;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; }
        else if (e.Key == Key.Down || e.Key == Key.Up)
        {
            var lb = GetActiveListBox();
            if (lb == null || lb.Items.Count == 0) return;
            int idx = lb.SelectedIndex;
            idx = e.Key == Key.Down ? Math.Min(idx + 1, lb.Items.Count - 1) : Math.Max(idx - 1, 0);
            lb.SelectedIndex = Math.Max(0, idx);
            (lb.ItemContainerGenerator.ContainerFromIndex(lb.SelectedIndex) as ListBoxItem)?.BringIntoView();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var lb = GetActiveListBox();
            if (lb != null) PasteSelected(lb);
            e.Handled = true;
        }
    }

    private ListBox? GetActiveListBox()
    {
        if (Tabs.SelectedItem is TabItem ti && ti.Content is ListBox lb) return lb;
        return null;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        var lb = GetActiveListBox();
        if (lb?.ItemsSource == null) return;
        var view = CollectionViewSource.GetDefaultView(lb.ItemsSource);
        var q = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q))
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = obj =>
            {
                if (obj is not ClipItem ci) return false;
                if (ci.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
                return ci.Kind switch
                {
                    ClipKind.Text => ci.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true,
                    ClipKind.Files => ci.FilePaths?.Any(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)) == true,
                    _ => false,
                };
            };
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        SearchBox.Text = "";
        var lb = GetActiveListBox();
        if (lb != null && lb.ItemsSource != null)
            CollectionViewSource.GetDefaultView(lb.ItemsSource).Filter = null;
        UpdateEntryCount();
        UpdateTitleCounter();
    }

    private FavoriteFolder? PromptCreateFolder()
    {
        var dlg = new FolderNameDialog("新建收藏夹", "", showKindPicker: true) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            return App.Favorites.CreateFolder(dlg.FolderName.Trim(), dlg.SelectedKind);
        return null;
    }

    private void RenameFolder(FavoriteFolder folder)
    {
        var dlg = new FolderNameDialog("重命名收藏夹", folder.Name, showKindPicker: false) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            App.Favorites.RenameFolder(folder, dlg.FolderName.Trim());
    }

    private void DeleteFolder(FavoriteFolder folder)
    {
        var r = MessageBox.Show(this,
            $"删除收藏夹\"{folder.Name}\"？其中 {folder.Items.Count} 条收藏将一并删除，无法恢复。",
            "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.OK)
            App.Favorites.DeleteFolder(folder);
    }

    protected override void OnPreviewDragEnter(DragEventArgs e)
    {
        base.OnPreviewDragEnter(e);
        if (_isDraggingOut) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (HasDroppableData(e)) { e.Effects = DragDropEffects.Copy; DropOverlay.Opacity = 1; }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }
    protected override void OnPreviewDragOver(DragEventArgs e)
    {
        base.OnPreviewDragOver(e);
        if (_isDraggingOut) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = HasDroppableData(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    protected override void OnPreviewDragLeave(DragEventArgs e)
    {
        base.OnPreviewDragLeave(e);
        DropOverlay.Opacity = 0;
    }
    protected override void OnPreviewDrop(DragEventArgs e)
    {
        base.OnPreviewDrop(e);
        DropOverlay.Opacity = 0;
        if (_isDraggingOut) { e.Handled = true; return; }
        Log($"PreviewDrop formats={string.Join(",", e.Data.GetFormats())}");
        var target = GetCurrentDropTarget();
        bool targetIsMeme = target?.Kind == FolderKind.Meme;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (targetIsMeme)
                {
                    var imgExts = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
                    foreach (var f in files)
                    {
                        if (!imgExts.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) continue;
                        try
                        {
                            if (f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                            {
                                var bytes = System.IO.File.ReadAllBytes(f);
                                var item = new ClipItem { Kind = ClipKind.Gif, GifBytes = bytes };
                                App.Favorites.AddToFolder(item, target!);
                            }
                            else
                            {
                                var bi = new BitmapImage();
                                bi.BeginInit();
                                bi.CacheOption = BitmapCacheOption.OnLoad;
                                bi.UriSource = new Uri(f);
                                bi.EndInit();
                                bi.Freeze();
                                var item = new ClipItem { Kind = ClipKind.Image, Image = bi };
                                App.Favorites.AddToFolder(item, target!);
                            }
                        }
                        catch { }
                    }
                }
                else
                {
                    AddItem(target, ClipKind.Files, files: files);
                }
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                bmp.Freeze();
                AddItem(target, ClipKind.Image, image: bmp);
            }
        }
        else if (!targetIsMeme && (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text)))
        {
            var text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text))?.ToString();
            if (!string.IsNullOrEmpty(text)) AddItem(target, ClipKind.Text, text: text);
        }
        e.Handled = true;
    }

    private static readonly string LogFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard", "debug.log");
    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}"); } catch {}
    }

    private static bool HasDroppableData(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop)
        || e.Data.GetDataPresent(DataFormats.Bitmap)
        || e.Data.GetDataPresent(DataFormats.UnicodeText)
        || e.Data.GetDataPresent(DataFormats.Text);

    private FavoriteFolder? GetCurrentDropTarget()
    {
        if (Tabs.SelectedItem is TabItem ti && ti.Tag is FavoriteFolder f) return f;
        return null;
    }

    private void AddItem(FavoriteFolder? target, ClipKind kind, string? text = null, BitmapSource? image = null, string[]? files = null)
    {
        if (target == null)
        {
            switch (kind)
            {
                case ClipKind.Text: App.History.AddText(text!); break;
                case ClipKind.Image: App.History.AddImage(image!); break;
                case ClipKind.Files: App.History.AddFiles(files!); break;
            }
        }
        else
        {
            var item = new ClipItem { Kind = kind, Text = text, Image = image, FilePaths = files };
            App.Favorites.AddToFolder(item, target);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private static Point GetCursorPos()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
        public uint padding1, padding2;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP_SI = 0x0002;
    private const ushort VK_CTRL_SI = 0x11;
    private const ushort VK_V_SI = 0x56;

    private static void SendPasteKeystroke()
    {
        var inputs = new INPUT[4];
        inputs[0] = NewKey(VK_CTRL_SI, 0);
        inputs[1] = NewKey(VK_V_SI, 0);
        inputs[2] = NewKey(VK_V_SI, KEYEVENTF_KEYUP_SI);
        inputs[3] = NewKey(VK_CTRL_SI, KEYEVENTF_KEYUP_SI);
        SendInput(4, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static INPUT NewKey(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } }
    };
}
