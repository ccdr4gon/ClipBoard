using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ClipBoard.Models;
using ClipBoard.Services;
using ClipBoard.Views;

namespace ClipBoard;

public sealed partial class MainWindow
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 24 };
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly TextBox _search = new() { Name = "SearchBox", Watermark = "搜索..." };
    private readonly TextBlock _status = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button _cancel = new() { Content = "取消任务", IsVisible = false };
    private readonly Grid _body = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
    private readonly ListBox _items = new() { Name = "HistoryList", Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Single };
    private readonly TextBlock _counter = Label("NO. 000", 10.5, true);
    private readonly TextBlock _entryCount = Label("— 0 entries —", 10.5, true);
    private bool _pinned, _contextOpen;
    // 列表渲染状态：当前 ItemsSource、生成它时的视图与收藏夹、已生成的行（见 TrySyncRows）。
    private ObservableCollection<ClipItem> _visible = [];
    private (string, FavoriteFolder?, FolderKind?, string)? _renderKey;
    private (FavoriteFolder, string)[] _renderFolders = [];
    private readonly Dictionary<ClipItem, BuiltRow> _built = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ClipItem> _rowHistory = new(ReferenceEqualityComparer.Instance); // 本视图里生成过行的条目
    private bool _resyncOnShow, _rowsTouched;
    private readonly List<(string Mode, FavoriteFolder? Folder, string Name, string Subtitle)> _tabKeys = [];
    private Button? _focusedTab; // 窗口里最后拿到焦点的是哪个标签（不是标签则为 null）
    private bool _tabsSettled;
    private static readonly FontFamily Serif = new("Georgia, PingFang SC, Segoe UI");
    private static readonly FontFamily Mono = new("Consolas, Menlo, monospace");
    private static IBrush Muted => Brush.Parse("#857867");
    private static IBrush Divider => Brush.Parse("#242C2418");

    private static TextBlock Label(string text, double size, bool mono = false) => new()
    {
        Text = text, FontSize = size, FontFamily = mono ? Mono : Serif,
        Foreground = mono ? Muted : Ink, VerticalAlignment = VerticalAlignment.Center,
    };

    private void BuildPanel()
    {
        Title = "ClipBoard"; Width = 780; Height = 640; MinWidth = 620; MinHeight = 400;
        SystemDecorations = SystemDecorations.None; Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var surface = new Grid();
        surface.Children.Add(new PaperBackground { IsHitTestVisible = false });
        var root = new Grid { RowDefinitions = new RowDefinitions("42,Auto,*,30") };
        surface.Children.Add(root);
        Content = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = surface };

        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = Label("ClipBoard", 15); name.FontStyle = FontStyle.Italic;
        brand.Children.Add(name); brand.Children.Add(_counter); title.Children.Add(brand);
        var caption = new StackPanel { Orientation = Orientation.Horizontal };
        Button Caption(string text, string tip, Action action)
        {
            var button = Button(text, () => { action(); return Task.CompletedTask; }); button.Classes.Add("caption");
            ToolTip.SetTip(button, tip); caption.Children.Add(button); return button;
        }
        Button? pin = null;
        pin = Caption("📌", "固定窗口（不自动隐藏）", () => { _pinned = !_pinned; pin!.Opacity = _pinned ? 1 : .4; }); pin.Opacity = .4;
        var settings = Button("☀", SystemSettingsAsync); settings.Classes.Add("caption"); ToolTip.SetTip(settings, "设置"); caption.Children.Add(settings);
        Caption("—", "最小化", () => WindowState = WindowState.Minimized);
        Caption("□", "最大化", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        Caption("✕", "关闭", Hide);
        Grid.SetColumn(caption, 1); title.Children.Add(caption); root.Children.Add(title);
        title.PointerPressed += (_, e) =>
        {
            if (e.Source is Control c && (c is Button || c.FindAncestorOfType<Button>() != null)) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        _search.Classes.Add("panelSearch");
        var search = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        search.Children.Add(new TextBlock { Text = "🔍", FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_search, 1); search.Children.Add(_search);
        var searchLine = new Border { Margin = new Thickness(20, 12, 20, 4), Padding = new Thickness(0, 0, 0, 6), BorderBrush = Divider, BorderThickness = new Thickness(0, 0, 0, 1.5), Child = search };
        Grid.SetRow(searchLine, 1); root.Children.Add(searchLine);

        var tabs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 14, 20, 0) };
        tabs.Children.Add(new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var add = Button("+ new", NewFolderAsync); add.Classes.Add("quiet"); add.FontFamily = Mono; add.FontSize = 11; add.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(add, 1); tabs.Children.Add(add);
        _body.Children.Add(new Border { BorderBrush = Divider, BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs });
        var tools = new ScrollViewer { Content = _toolbar, Margin = new Thickness(16, 6, 12, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        // A hidden toolbar occupies no space on history/image tabs.
        tools.Bind(IsVisibleProperty, _toolbar.GetObservable(IsVisibleProperty));
        Grid.SetRow(tools, 1); _body.Children.Add(tools);
        _items.Margin = new Thickness(12, 8);
        ScrollViewer.SetHorizontalScrollBarVisibility(_items, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _items.ItemTemplate = new FuncDataTemplate<ClipItem>((item, _) => RenderItem(item!));
        Grid.SetRow(_items, 2); _body.Children.Add(_items);
        var status = new DockPanel { Margin = new Thickness(18, 0, 18, 4) };
        DockPanel.SetDock(_cancel, Dock.Right); status.Children.Add(_cancel); status.Children.Add(_status);
        Grid.SetRow(status, 3); _body.Children.Add(status);
        Grid.SetRow(_body, 2); root.Children.Add(_body);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(18, 0) };
        footer.Children.Add(Label("ctrl+cmd+v — summon    ↑↓ — select    enter — paste    shift+enter — plain text", 10.5, true));
        Grid.SetColumn(_entryCount, 1); footer.Children.Add(_entryCount);
        var footerLine = new Border { Background = Brush.Parse("#D1FDFAF3"), BorderBrush = Divider, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        Grid.SetRow(footerLine, 3); root.Children.Add(footerLine);
        var menu = new ContextMenu(); AddMenu(menu, "清空历史…", ClearHistoryAsync); footerLine.ContextMenu = menu;
    }

    private Task NewFolderAsync() => RunAsync(async _ =>
    {
        var value = await Dialogs.Form(this, "新标签", "创建一个表情包栏目。", ("名称", "新表情包", false));
        if (value != null && !string.IsNullOrWhiteSpace(value[0])) SelectFolder(App.Favorites.CreateFolder(value[0].Trim(), FolderKind.Meme));
    });
    private Task ClearHistoryAsync() => RunAsync(async _ =>
    {
        if (await Dialogs.Confirm(this, "清空历史", "删除全部未置顶的历史？收藏和置顶内容会保留。")) { App.History.Clear(); ReclaimRemovedThumbnails(); }
    });
    // 标签列表没变时只换掉选中状态变化的标签（每次粘贴、切换标签都会走到这里），收藏夹增删、改名、换类型才整排重建。
    // 换下的标签总是新建按钮而不是改样式类：点下的按钮有按压缩放动画，也带着焦点，原先它总被丢弃。
    // 同理，拿到过焦点、这次又不换的标签不保留，改为整排重建。
    // 一组标签第一次排版时，Avalonia 的回退字体可能解析得与之后不同（例如启动时的中文粗体标题），原先下一次重绘
    // 就全部换掉；所以同一组标签排版过之后要再整排重建一次（_tabsSettled），之后才保留未变的标签。
    private void RenderTabs(bool replaceSelected = false)
    {
        var keys = new List<(string Mode, FavoriteFolder? Folder, string Name, string Subtitle)>(3 + App.Favorites.Folders.Count)
            { ("history", null, "历史", "HISTORY"), ("images", null, "图片", "IMAGES"), ("emoji", null, "emoji", "EMOJI") };
        foreach (var folder in App.Favorites.Folders) keys.Add(("folder", folder, folder.Name, folder.Kind == FolderKind.Meme ? "MEMES" : "FOLDER"));
        bool same = _tabs.Children.Count == keys.Count && keys.SequenceEqual(_tabKeys);
        if (same && _tabsSettled)
        {
            var replace = new List<int>();
            for (int i = 0; i < keys.Count; i++)
            {
                bool selected = _mode == keys[i].Mode && _folder == keys[i].Folder;
                if (selected != _tabs.Children[i].Classes.Contains("selected") || replaceSelected && selected) replace.Add(i);
            }
            int focused = _focusedTab == null ? -1 : _tabs.Children.IndexOf(_focusedTab);
            if (focused < 0 || replace.Contains(focused))
            {
                foreach (int i in replace) { _tabs.Children.RemoveAt(i); _tabs.Children.Insert(i, CreateTab(keys[i].Name, keys[i].Subtitle, keys[i].Mode, keys[i].Folder)); }
                return;
            }
        }
        _tabsSettled = same && _tabs.Children.All(tab => tab.DesiredSize != default);
        _tabs.Children.Clear(); _tabKeys.Clear(); _tabKeys.AddRange(keys);
        foreach (var (mode, folder, name, subtitle) in keys) _tabs.Children.Add(CreateTab(name, subtitle, mode, folder));
    }
    private Button CreateTab(string name, string subtitle, string mode, FavoriteFolder? folder)
    {
        var header = new StackPanel();
        var title = Label(name, 15); title.FontWeight = FontWeight.SemiBold;
        header.Children.Add(title); header.Children.Add(Label(subtitle, 9, true));
        var tab = Button(name, () => { SelectView(mode, folder); return Task.CompletedTask; });
        tab.Content = header; tab.Classes.Add("tab");
        if (_mode == mode && _folder == folder) tab.Classes.Add("selected");
        if (folder != null)
        {
            var menu = new ContextMenu();
            AddMenu(menu, "管理标签…", () => { SelectFolder(folder); return ManageFolderAsync(); });
            menu.Opened += (_, _) => _contextOpen = true; menu.Closed += (_, _) => _contextOpen = false;
            tab.ContextMenu = menu;
        }
        return tab;
    }
    internal void SelectView(string mode, FavoriteFolder? folder = null)
    {
        _mode = mode; _folder = folder; RenderTabs(replaceSelected: true); RenderToolbar(); RenderCards(); ResetPosition();
    }
    public void SelectFolder(FavoriteFolder folder) => SelectView("folder", folder);
    private void RenderToolbar()
    {
        _toolbar.Children.Clear(); _toolbar.IsVisible = _folder?.Kind == FolderKind.Meme;
        if (!_toolbar.IsVisible) return;
        foreach (var (name, action) in new (string, Func<Task>)[] { ("导入文件", ImportFilesAsync), ("导入 Telegram", ImportTelegramAsync), ("导出", ExportAsync), ("发布 / 同步", PublishAsync) })
        { var button = Button(name, action); button.Classes.Add("quiet"); _toolbar.Children.Add(button); }
        var more = Button("更多 ▾", () => Task.CompletedTask); more.Classes.Add("quiet");
        var menu = new ContextMenu(); AddMenu(menu, "管理标签…", ManageFolderAsync); AddMenu(menu, "Telegram 连接…", SettingsAsync);
        menu.Opened += (_, _) => _contextOpen = true; menu.Closed += (_, _) => _contextOpen = false;
        more.Click += (_, _) => menu.Open(more); _toolbar.Children.Add(more);
    }
    private void RenderCards()
    {
        var selected = (_items.SelectedItem as ClipItem)?.Id;
        bool rows = _mode == "history" || _folder?.Kind == FolderKind.Normal;
        _items.Classes.Set("history", rows); _items.Classes.Set("tiles", !rows && _mode != "emoji"); _items.Classes.Set("emoji", _mode == "emoji");
        var items = VisibleItems;
        // 面板隐藏时（粘贴之后、后台复制时）只做增量更新；面板显示着时仍整表重建，与原先完全一致。
        if (IsVisible || !SameRenderState() || !TrySyncRows(items)) RebuildList(items);
        else _resyncOnShow = true;
        _items.SelectedItem = _visible.FirstOrDefault(i => i.Id == selected);
        _counter.Text = $"NO. {App.History.Items.Count + App.Favorites.PinnedHistory.Count + App.Favorites.Folders.Sum(f => f.Items.Count):D3}";
        _entryCount.Text = $"— {_visible.Count} entries —";
    }
    private void RebuildList(IReadOnlyList<ClipItem> items)
    {
        bool rows = _mode == "history" || _folder?.Kind == FolderKind.Normal;
        var key = RenderKey();
        if (_renderKey != key) _rowHistory.Clear(); else _rowHistory.IntersectWith(items);
        _renderKey = key; _renderFolders = FolderNames(); _built.Clear(); _resyncOnShow = false; _rowsTouched = false;
        _items.ItemsPanel = new FuncTemplate<Panel?>(() => rows ? new StackPanel() : new WrapPanel());
        _visible = new ObservableCollection<ClipItem>(items); // 先于 ItemsSource 赋值：行号从这份列表里取
        _items.ItemsSource = _visible;
    }
    private (string, FavoriteFolder?, FolderKind?, string) RenderKey() => (_mode, _folder, _folder?.Kind, _search.Text ?? "");
    private static (FavoriteFolder, string)[] FolderNames() => App.Favorites.Folders.Select(f => (f, f.Name)).ToArray();
    // 能否沿用已生成的行：同一视图、同一组收藏夹（行的“收藏到”菜单列出它们），列表里没有控件拿到过焦点
    // （单击会让行获得焦点，重新激活窗口时焦点和滚动可能回到旧行），没有打开着的右键菜单，
    // 鼠标也不在列表上（原先整表重建后第一帧就按鼠标位置重新判定悬停底色和提示，沿用旧行会晚一帧）。
    private bool SameRenderState() => _renderKey == RenderKey() && FolderNames().SequenceEqual(_renderFolders) && !_rowsTouched && !_contextOpen && !_items.IsPointerOver;

    // 隐藏时的增量更新：移除已不在列表里的条目、插入新条目，其余已生成的行原样保留（编号在下面改写）。
    // 原先每次都换掉 ItemsSource 和面板，下次打开面板时全部几百行重新生成、重新排版。
    // 保留的行必须与此刻重新生成的完全相同，否则换成新容器（打开面板时重新生成）：
    // 条目的显示数据变了（例如同一段文字补上了格式）、鼠标停在行上（悬停底色、提示）、
    // 或这是该条目第一次生成的行（第一次排版时回退字体可能与之后不同，原先下一次渲染就会换掉它）。
    // 改动太多或顺序变了（例如“向前移动”）时返回 false，整表重建。
    private const int MaxRowEdits = 24;
    private bool TrySyncRows(IReadOnlyList<ClipItem> target)
    {
        var wanted = new HashSet<ClipItem>(target, ReferenceEqualityComparer.Instance);
        var current = new HashSet<ClipItem>(_visible, ReferenceEqualityComparer.Instance);
        if (!_visible.Where(wanted.Contains).SequenceEqual(target.Where(current.Contains), ReferenceEqualityComparer.Instance)) return false;
        int kept = target.Count(current.Contains);
        var stale = StaleRows(wanted);
        if (stale == null || _visible.Count - kept + target.Count - kept + stale.Count > MaxRowEdits) return false;
        for (int i = _visible.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_visible[i])) { _built.Remove(_visible[i]); _rowHistory.Remove(_visible[i]); _visible.RemoveAt(i); }
        for (int i = 0; i < target.Count; i++)
            if (i >= _visible.Count || !ReferenceEquals(_visible[i], target[i])) _visible.Insert(i, target[i]);
        ReplaceRows(stale);
        return true;
    }
    /// <summary>需要换新容器的已生成行；超过上限时返回 null。</summary>
    private HashSet<ClipItem>? StaleRows(HashSet<ClipItem>? among = null)
    {
        var stale = new HashSet<ClipItem>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _visible.Count; i++)
        {
            var item = _visible[i];
            if (among?.Contains(item) == false || !_built.TryGetValue(item, out var row)) continue; // 还没生成的行打开面板时才按当时的数据生成
            if (!row.Settled || row.State != StateOf(item) || row.Tip != null && ToolTip.GetIsOpen(row.Tip) || _items.ContainerFromIndex(i)?.IsPointerOver == true)
                if (stale.Add(item) && stale.Count > MaxRowEdits) return null;
        }
        return stale;
    }
    private void ReplaceRows(HashSet<ClipItem> stale)
    {
        for (int i = 0; i < _visible.Count; i++)
        {
            var item = _visible[i];
            if (stale.Contains(item)) { _built.Remove(item); _visible[i] = item; } // 换成新容器，打开面板时重新生成
            else if (_built.TryGetValue(item, out var row) && row.Index != null)
            {
                string number = (i + 1).ToString("D2");
                if (row.Index.Text != number) row.Index.Text = number;
            }
        }
    }
    /// <summary>
    /// 打开面板前调用（面板仍隐藏）。原先隐藏期间渲染过，打开时全部行按当时的数据重新生成；
    /// 增量更新时保留的行在这里按此刻的数据再核对一遍。
    /// </summary>
    internal void ResyncHiddenRows()
    {
        if (!_resyncOnShow || IsVisible) return;
        _resyncOnShow = false;
        if (!SameRenderState() || StaleRows() is not { } stale) RebuildList(_visible.ToArray()); // 原先显示的也是上次渲染时的列表
        else ReplaceRows(stale);
    }
    private readonly record struct RowState(ClipKind Kind, bool Pinned, string? Rich, DateTime Time, object? Image, byte[]? Gif,
        int Width, int Height, StickerAsset? Sticker, double? Duration, string? Text, string[]? Files, string? Title, Guid Id);
    // 生成一行时读到的全部条目数据（不含会解码或拼接字符串的属性）。
    private static RowState StateOf(ClipItem i) => new(i.Kind, i.IsPinned, i.RichBlobName, i.Timestamp, i.Image, i.GifBytes,
        i.PixelW, i.PixelH, i.Sticker, i.Sticker?.DurationSeconds, i.Text, i.FilePaths, i.Title, i.Id);
    private sealed record BuiltRow(TextBlock? Index, Control? Tip, RowState State, bool Settled);

    private Control RenderItem(ClipItem item)
    {
        TextBlock? index = null; Control? tip = null;
        Control control = _mode == "emoji" ? new Border { Width = 44, Height = 44, Background = Brushes.Transparent, Child = new TextBlock { Text = item.Text, FontSize = 26, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } }
            : _mode == "history" || _folder?.Kind == FolderKind.Normal ? HistoryRow(item, out index, out tip) : ImageTile(item, out tip);
        AttachItemActions(control, item);
        // 模板在排版时才运行，所以该条目此前若生成过行，那一行已经排版（字体已解析）过。
        _built[item] = new BuiltRow(index, tip, StateOf(item), Settled: !_rowHistory.Add(item));
        return control;
    }
    // 行号取自正在显示的列表（与 IndexOf 一样按引用找第一个）。原先每行都重新筛选全部条目再查找，
    // 一次渲染是 O(n²)，搜索时每个条目还要拼接一遍全文。
    private int RowNumber(ClipItem item)
    {
        var shown = _visible;
        for (int i = 0; i < shown.Count; i++) if (ReferenceEquals(shown[i], item)) return i + 1;
        return VisibleItems.ToList().IndexOf(item) + 1;
    }
    private Control HistoryRow(ClipItem item, out TextBlock index, out Control? tip)
    {
        tip = null;
        // 透明背景让整行（不只是文字）都能响应右键菜单。
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,Auto"), Background = Brushes.Transparent };
        index = Label(RowNumber(item).ToString("D2"), 10, true); index.TextAlignment = TextAlignment.Right; row.Children.Add(index);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        if (item.IsPinned) meta.Children.Add(Label("📌", 11));
        if (item.HasRichText)
        {
            var badge = new Border { Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(3), BorderBrush = Divider, BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "格式", FontSize = 9.5, Foreground = Brush.Parse("#7A4F2B") } };
            ToolTip.SetTip(badge, "带格式（列表编号、粗体等）。回车 / 单击保留格式粘贴，Shift+回车 / Shift+单击粘贴为纯文本");
            meta.Children.Add(badge); tip = badge;
        }
        meta.Children.Add(Label(item.TimeLabel, 10.5, true));
        Grid.SetColumn(meta, 2); row.Children.Add(meta);
        Control content;
        if (item.Kind is ClipKind.Image or ClipKind.VideoSticker or ClipKind.VectorSticker)
        {
            var preview = new Border { Width = 184, Height = 107, Padding = new Thickness(3), Background = Brush.Parse("#F5FDFAF3"), BorderBrush = Divider, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Child = new HistoryThumbnail { Source = item.Image, Width = 176, Height = 99 }, Margin = new Thickness(0, 0, 12, 0) };
            ToolTip.SetTip(preview, new Image { Source = item.Image, MaxWidth = 400, MaxHeight = 400, Stretch = Stretch.Uniform }); tip = preview;
            var image = new DockPanel(); DockPanel.SetDock(preview, Dock.Left); image.Children.Add(preview);
            var dimensions = Label(item.DimensionLabel, 11, true); dimensions.TextTrimming = TextTrimming.CharacterEllipsis; image.Children.Add(dimensions); content = image;
        }
        else
        {
            var label = Label(item.Kind == ClipKind.Gif ? "— gif, " + item.DimensionLabel + " —" : item.Preview, item.Kind == ClipKind.Files ? 12.5 : 13.5, item.Kind == ClipKind.Files);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            if (item.Kind == ClipKind.Gif) { label.FontStyle = FontStyle.Italic; label.Foreground = Muted; }
            content = label;
        }
        content.Margin = new Thickness(14, 0, 0, 0); Grid.SetColumn(content, 1); row.Children.Add(content); return row;
    }
    private Control ImageTile(ClipItem item, out Control tip)
    {
        bool meme = _folder?.Kind == FolderKind.Meme;
        var label = Label(meme ? item.TitleOrUntitled : item.DimensionLabel, meme ? 15 : 10, !meme);
        label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 8, 0, 0); label.MaxWidth = 185; label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (meme) label.FontWeight = FontWeight.Bold;
        var content = new DockPanel(); DockPanel.SetDock(label, Dock.Bottom); content.Children.Add(label);
        content.Children.Add(new Border { ClipToBounds = true, Child = new Image { Source = item.Image ?? item.GifSource, Stretch = meme ? Stretch.Uniform : Stretch.UniformToFill } });
        var card = new Border { Width = 210, Height = 246, Padding = new Thickness(10), Background = Brush.Parse("#FFFEF9"), BorderBrush = Divider, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1),
            BoxShadow = BoxShadows.Parse("2 3 10 0 #382C2418"), RenderTransformOrigin = new RelativePoint(.5, .5, RelativeUnit.Relative), RenderTransform = new RotateTransform(((uint)item.Id.GetHashCode() % 301) / 100.0 - 1.5), Child = content };
        ToolTip.SetTip(card, new Image { Source = item.Image ?? item.GifSource, MaxWidth = 400, MaxHeight = 400, Stretch = Stretch.Uniform }); tip = card; return card;
    }
    private async void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == Key.Enter && (_items.SelectedItem ?? VisibleItems.FirstOrDefault()) is ClipItem item)
        {
            e.Handled = true;
            await CopyAsync(item, true, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        else if (e.Key is Key.Down or Key.Up && (_search.IsKeyboardFocusWithin || _items.IsKeyboardFocusWithin) && _items.ItemCount > 0)
        {
            _items.SelectedIndex = Math.Clamp(_items.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _items.ItemCount - 1);
            _items.ScrollIntoView(_items.SelectedItem!); e.Handled = true;
        }
    }
}
