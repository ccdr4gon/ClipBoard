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
    private IReadOnlyList<ClipItem> _shown = [];
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
        if (await Dialogs.Confirm(this, "清空历史", "删除全部未置顶的历史？收藏和置顶内容会保留。")) App.History.Clear();
    });
    private void RenderTabs()
    {
        _tabs.Children.Clear();
        foreach (var (mode, name, subtitle) in new[] { ("history", "历史", "HISTORY"), ("images", "图片", "IMAGES"), ("emoji", "emoji", "EMOJI") })
            AddTab(name, subtitle, mode, null);
        foreach (var folder in App.Favorites.Folders) AddTab(folder.Name, folder.Kind == FolderKind.Meme ? "MEMES" : "FOLDER", "folder", folder);
    }
    private void AddTab(string name, string subtitle, string mode, FavoriteFolder? folder)
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
        _tabs.Children.Add(tab);
    }
    internal void SelectView(string mode, FavoriteFolder? folder = null)
    {
        _mode = mode; _folder = folder; RenderTabs(); RenderToolbar(); RenderCards(); ResetPosition();
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
        _items.ItemsPanel = new FuncTemplate<Panel?>(() => rows ? new StackPanel() : new WrapPanel());
        var items = VisibleItems;
        _shown = items; // 先于 ItemsSource 赋值：行号从这份列表里取
        _items.ItemsSource = items;
        _items.SelectedItem = items.FirstOrDefault(i => i.Id == selected);
        _counter.Text = $"NO. {App.History.Items.Count + App.Favorites.PinnedHistory.Count + App.Favorites.Folders.Sum(f => f.Items.Count):D3}";
        _entryCount.Text = $"— {items.Count} entries —";
    }
    private Control RenderItem(ClipItem item)
    {
        Control control = _mode == "emoji" ? new Border { Width = 44, Height = 44, Background = Brushes.Transparent, Child = new TextBlock { Text = item.Text, FontSize = 26, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } }
            : _mode == "history" || _folder?.Kind == FolderKind.Normal ? HistoryRow(item) : ImageTile(item);
        AttachItemActions(control, item); return control;
    }
    // 行号取自正在显示的列表（与 IndexOf 一样按引用找第一个）。原先每行都重新筛选全部条目再查找，
    // 一次渲染是 O(n²)，搜索时每个条目还要拼接一遍全文。
    private int RowNumber(ClipItem item)
    {
        var shown = _shown;
        for (int i = 0; i < shown.Count; i++) if (ReferenceEquals(shown[i], item)) return i + 1;
        return VisibleItems.ToList().IndexOf(item) + 1;
    }
    private Control HistoryRow(ClipItem item)
    {
        // 透明背景让整行（不只是文字）都能响应右键菜单。
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,Auto"), Background = Brushes.Transparent };
        var index = Label(RowNumber(item).ToString("D2"), 10, true); index.TextAlignment = TextAlignment.Right; row.Children.Add(index);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        if (item.IsPinned) meta.Children.Add(Label("📌", 11));
        if (item.HasRichText)
        {
            var badge = new Border { Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(3), BorderBrush = Divider, BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "格式", FontSize = 9.5, Foreground = Brush.Parse("#7A4F2B") } };
            ToolTip.SetTip(badge, "带格式（列表编号、粗体等）。回车 / 单击保留格式粘贴，Shift+回车 / Shift+单击粘贴为纯文本");
            meta.Children.Add(badge);
        }
        meta.Children.Add(Label(item.TimeLabel, 10.5, true));
        Grid.SetColumn(meta, 2); row.Children.Add(meta);
        Control content;
        if (item.Kind is ClipKind.Image or ClipKind.VideoSticker or ClipKind.VectorSticker)
        {
            var preview = new Border { Width = 184, Height = 107, Padding = new Thickness(3), Background = Brush.Parse("#F5FDFAF3"), BorderBrush = Divider, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Child = new HistoryThumbnail { Source = item.Image, Width = 176, Height = 99 }, Margin = new Thickness(0, 0, 12, 0) };
            ToolTip.SetTip(preview, new Image { Source = item.Image, MaxWidth = 400, MaxHeight = 400, Stretch = Stretch.Uniform });
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
    private Control ImageTile(ClipItem item)
    {
        bool meme = _folder?.Kind == FolderKind.Meme;
        var label = Label(meme ? item.TitleOrUntitled : item.DimensionLabel, meme ? 15 : 10, !meme);
        label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 8, 0, 0); label.MaxWidth = 185; label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (meme) label.FontWeight = FontWeight.Bold;
        var content = new DockPanel(); DockPanel.SetDock(label, Dock.Bottom); content.Children.Add(label);
        content.Children.Add(new Border { ClipToBounds = true, Child = new Image { Source = item.Image ?? item.GifSource, Stretch = meme ? Stretch.Uniform : Stretch.UniformToFill } });
        var card = new Border { Width = 210, Height = 246, Padding = new Thickness(10), Background = Brush.Parse("#FFFEF9"), BorderBrush = Divider, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1),
            BoxShadow = BoxShadows.Parse("2 3 10 0 #382C2418"), RenderTransformOrigin = new RelativePoint(.5, .5, RelativeUnit.Relative), RenderTransform = new RotateTransform(((uint)item.Id.GetHashCode() % 301) / 100.0 - 1.5), Child = content };
        ToolTip.SetTip(card, new Image { Source = item.Image ?? item.GifSource, MaxWidth = 400, MaxHeight = 400, Stretch = Stretch.Uniform }); return card;
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
