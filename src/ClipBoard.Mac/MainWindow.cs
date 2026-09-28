using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClipBoard.Models;
using ClipBoard.Services;
using ClipBoard.Views;

namespace ClipBoard;

public sealed class MainWindow : Window
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly WrapPanel _cards = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBox _search = new() { Watermark = "搜索文字、标题、表情包…", Margin = new Thickness(0, 14) };
    private readonly TextBlock _status = new() { Text = "双击卡片粘贴 · 右键管理 · ⌘⌥V 唤出", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _cancel = new() { Content = "取消任务", IsVisible = false };
    private readonly Grid _body = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
    private FavoriteFolder? _folder;
    private string _mode = "history";
    private CancellationTokenSource? _work;
    private TelegramConnection _connection = new();
    private bool _connectionLoaded;
    private readonly HashSet<FavoriteFolder> _subscribed = [];
    private bool _renderQueued;
    private static IBrush Ink => Brush.Parse("#2C2418");
    private App Application => (App)App.Current!;
    public MainWindow()
    {
        Title = "ClipBoard"; Width = 820; Height = 660; MinWidth = 630; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { Margin = new Thickness(26, 16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var brand = new StackPanel();
        brand.Children.Add(new TextBlock { Text = "ClipBoard", FontFamily = new FontFamily("Georgia"), FontSize = 30, Foreground = Ink });
        brand.Children.Add(new TextBlock { Text = "随手收藏，随时取用。", Foreground = Brush.Parse("#857867"), FontSize = 12, Margin = new Thickness(0, 3, 0, 16) });
        heading.Children.Add(brand);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button("Telegram 连接", SettingsAsync));
        actions.Children.Add(Button("设置", SystemSettingsAsync));
        Grid.SetColumn(actions, 1); heading.Children.Add(actions);
        root.Children.Add(heading);
        var top = new StackPanel();
        top.Children.Add(new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        top.Children.Add(_search); _body.Children.Add(top);
        var toolbarScroll = new ScrollViewer { Content = _toolbar, Margin = new Thickness(0, 0, 0, 12),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(toolbarScroll, 1); _body.Children.Add(toolbarScroll);
        var scroll = new ScrollViewer { Content = _cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); _body.Children.Add(scroll);
        Grid.SetRow(_body, 1); root.Children.Add(_body);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(_status); Grid.SetColumn(_cancel, 1); bottom.Children.Add(_cancel);
        Grid.SetRow(bottom, 2); root.Children.Add(bottom); Content = root;
        _cancel.Click += (_, _) => _work?.Cancel();
        _search.TextChanged += (_, _) => RenderCards();
        App.History.Items.CollectionChanged += ItemsChanged;
        App.Favorites.PinnedHistory.CollectionChanged += ItemsChanged;
        App.Favorites.Folders.CollectionChanged += (_, _) => { SubscribeFolders(); RenderTabs(); };
        SubscribeFolders(); RenderTabs(); RenderToolbar(); RenderCards();
        Closing += (_, e) => { if (!Application.Quitting) { e.Cancel = true; Hide(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = _folder != null && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (_folder == null || _work != null) return;
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(File.Exists).ToArray() ?? [];
            if (paths.Length > 0) await RunAsync(async ct => ShowImport(await Library().ImportFilesAsync(_folder, paths, Progress(), ct)));
        });
    }
    private void SubscribeFolders()
    {
        foreach (var old in _subscribed.Where(f => !App.Favorites.Folders.Contains(f)).ToArray())
        { old.Items.CollectionChanged -= ItemsChanged; _subscribed.Remove(old); }
        foreach (var folder in App.Favorites.Folders)
            if (_subscribed.Add(folder)) folder.Items.CollectionChanged += ItemsChanged;
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() => { _renderQueued = false; RenderCards(); });
    }
    public void SetStatus(string message) => _status.Text = message;
    internal void CancelWork() => _work?.Cancel();
    private IProgress<string> Progress() => new Progress<string>(SetStatus);
    private static Button Button(string title, Func<Task> action)
    {
        var b = new Button { Content = title, VerticalAlignment = VerticalAlignment.Center };
        b.Click += async (_, _) => await action(); return b;
    }
    private void RenderTabs()
    {
        _tabs.Children.Clear();
        foreach (var (key, label) in new[] { ("history", "历史"), ("images", "图片"), ("emoji", "Emoji") }) AddTab(label, key, null);
        foreach (var folder in App.Favorites.Folders) AddTab(folder.Name, "folder", folder);
        _tabs.Children.Add(Button("＋", () => RunAsync(async _ =>
        {
            var value = await Dialogs.Form(this, "新标签", "创建一个收藏夹或表情包栏目。", ("名称", "新表情包", false));
            if (value != null && !string.IsNullOrWhiteSpace(value[0])) SelectFolder(App.Favorites.CreateFolder(value[0].Trim(), FolderKind.Meme));
        })));
    }
    private void AddTab(string label, string mode, FavoriteFolder? folder)
    {
        var button = Button(label, () => { _mode = mode; _folder = folder; RenderTabs(); RenderToolbar(); RenderCards(); return Task.CompletedTask; });
        button.Classes.Add("tab");
        if (_mode == mode && _folder == folder) button.Classes.Add("selected");
        _tabs.Children.Add(button);
    }
    public void SelectFolder(FavoriteFolder folder) { _folder = folder; _mode = "folder"; RenderTabs(); RenderToolbar(); RenderCards(); }
    private void RenderToolbar()
    {
        _toolbar.Children.Clear();
        if (_folder != null)
        {
            _toolbar.Children.Add(Button("导入文件", ImportFilesAsync));
            _toolbar.Children.Add(Button("导入 Telegram", ImportTelegramAsync));
            _toolbar.Children.Add(Button("导出", ExportAsync));
            _toolbar.Children.Add(Button("发布 / 同步", PublishAsync));
            _toolbar.Children.Add(Button("管理标签", ManageFolderAsync));
        }
        else if (_mode != "emoji") _toolbar.Children.Add(Button("清空历史", () => RunAsync(async _ =>
        {
            if (await Dialogs.Confirm(this, "清空历史", "删除全部未置顶的剪贴板历史？收藏和置顶内容会保留。")) App.History.Clear();
        })));
    }
    public IReadOnlyList<ClipItem> VisibleItems => (_folder != null ? _folder.Items
        : App.Favorites.PinnedHistory.Concat(App.History.Items))
        .Where(i => _mode != "images" || i.Kind is ClipKind.Image or ClipKind.Gif or ClipKind.VideoSticker or ClipKind.VectorSticker)
        .Where(i => string.IsNullOrWhiteSpace(_search.Text) || (i.Title + " " + i.Preview + " " + i.StickerEmojiLabel).Contains(_search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    private void RenderCards()
    {
        _cards.Children.Clear();
        if (_mode == "emoji")
        {
            foreach (var emoji in new[] { "😀", "😃", "😁", "😂", "🥹", "😊", "😍", "🥰", "😘", "😎", "🤔", "🫠", "😭", "😡", "🥳", "😴", "👍", "👎", "👏", "🙏", "💪", "❤️", "💔", "🔥", "🎉", "✨", "🌹", "👀", "🐱", "🐶", "🍀", "☕" })
                _cards.Children.Add(Button(emoji, () => CopyAsync(new ClipItem { Kind = ClipKind.Text, Text = emoji }, false)));
            return;
        }
        foreach (var item in VisibleItems) _cards.Children.Add(Card(item));
        if (_cards.Children.Count == 0) _cards.Children.Add(new TextBlock { Text = _folder != null ? "将图片拖到这里，或导入 Telegram 贴纸包。" : "复制文字、图片或文件后，会出现在这里。", Margin = new Thickness(12, 30), Foreground = Brush.Parse("#857867") });
    }
    private Control Card(ClipItem item)
    {
        var content = new StackPanel { Spacing = 7 };
        if (item.Image != null)
        {
            if (_mode == "history")
            {
                var preview = new HistoryThumbnail { Source = item.Image, Width = 152, Height = 85.5 };
                ToolTip.SetTip(preview, new Image { Source = item.Image, MaxWidth = 400, MaxHeight = 400, Stretch = Stretch.Uniform });
                content.Children.Add(preview);
            }
            else content.Children.Add(new Image { Source = item.Image, Height = 145, Stretch = Stretch.Uniform });
        }
        else content.Children.Add(new TextBlock { Text = item.Preview, Height = 145, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = (item.IsPinned ? "● " : "") + (item.HasTitle ? item.Title : item.Kind == ClipKind.Text ? "文字" : item.Preview), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock { Text = item.Sticker != null ? item.StickerEmojiLabel + " · " + item.StickerStatus : item.TimeLabel, FontSize = 10, Foreground = Brush.Parse("#857867") });
        var card = new Border { Width = 174, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(10), Background = Brush.Parse("#FFFDFA"), BorderBrush = Brush.Parse("#E6DECF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = content, BoxShadow = BoxShadows.Parse("1 3 7 0 #16000000") };
        card.DoubleTapped += async (_, _) => await CopyAsync(item, true);
        var menu = new ContextMenu();
        AddMenu(menu, "复制", () => CopyAsync(item, false)); AddMenu(menu, "粘贴到原应用", () => CopyAsync(item, true));
        if (item.Kind is ClipKind.Image or ClipKind.Gif or ClipKind.VideoSticker or ClipKind.VectorSticker)
            AddMenu(menu, "预览", () => PreviewAsync(item));
        if (_folder != null)
        {
            AddMenu(menu, "编辑图片 / 动画", () => EditAsync(item));
            AddMenu(menu, "替换素材", () => ReplaceAsync(item));
            AddMenu(menu, "恢复原件", () => RunAsync(async ct => { var replacement = await Media().RestoreAsync(item, ct); Library().ReplaceLocal(_folder!, item, replacement); }));
            AddMenu(menu, "修改标题 / Emoji", () => RenameItemAsync(item));
            AddMenu(menu, "向前移动", () => { int index = _folder!.Items.IndexOf(item); if (index > 0) _folder.Items.Move(index, index - 1); App.Favorites.Save(); return Task.CompletedTask; });
            AddMenu(menu, "移除", () => { App.Favorites.RemoveFavorite(item); return Task.CompletedTask; });
        }
        else
        {
            AddMenu(menu, item.IsPinned ? "取消置顶" : "置顶", () => { if (item.IsPinned) App.Favorites.UnpinHistory(item, App.History); else App.Favorites.PinHistory(item, App.History); return Task.CompletedTask; });
            AddMenu(menu, "删除", () => { if (item.IsPinned) App.Favorites.RemovePinnedHistory(item); else App.History.Remove(item); return Task.CompletedTask; });
        }
        var collect = new MenuItem { Header = "收藏到" };
        foreach (var folder in App.Favorites.Folders.Where(f => f != _folder))
        {
            var target = folder; var option = new MenuItem { Header = target.Name };
            option.Click += async (_, _) => await RunAsync(_ => { App.Favorites.AddToFolder(item, target); return Task.CompletedTask; });
            collect.Items.Add(option);
        }
        menu.Items.Add(collect); card.ContextMenu = menu;
        return card;
    }
    private static void AddMenu(ContextMenu menu, string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title }; item.Click += async (_, _) => await action(); menu.Items.Add(item);
    }
    private Task CopyAsync(ClipItem item, bool paste) => RunAsync(_ => Application.CopyAsync(item, paste));
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_work != null) return;
        using var work = new CancellationTokenSource(); _work = work;
        _body.IsEnabled = false; _cancel.IsVisible = true;
        try { await action(work.Token); }
        catch (OperationCanceledException) { SetStatus("已取消，已完成的导入和修改已保存。"); }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { _work = null; _body.IsEnabled = true; _cancel.IsVisible = false; RenderTabs(); RenderCards(); }
    }
    private void LoadConnection()
    {
        if (_connectionLoaded) return;
        _connection = new TelegramConnectionStore(App.Persistence.RootDirectory).Load(); _connectionLoaded = true;
    }
    private StickerMediaService Media() { LoadConnection(); return new(App.Persistence, _connection.MediaToolsDirectory); }
    private StickerLibraryService Library() => new(App.Favorites, App.Persistence, Media());
    private TelegramStickerClient Client()
    {
        LoadConnection();
        if (string.IsNullOrWhiteSpace(_connection.Token)) throw new InvalidOperationException("请先在设置中填写 Telegram 机器人 token。");
        return new(_connection.Token);
    }
    private static readonly FilePickerFileType Images = new("图片与贴纸") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.webm", "*.tgs", "*.bmp"] };
    private async Task<string[]> PickFiles(bool multiple)
        => (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入图片或贴纸", AllowMultiple = multiple, FileTypeFilter = [Images] }))
            .Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
    private Task ImportFilesAsync() => RunAsync(async ct =>
    {
        var paths = await PickFiles(true);
        if (paths.Length > 0) ShowImport(await Library().ImportFilesAsync(_folder!, paths, Progress(), ct));
    });
    private void ShowImport(StickerImportResult result) => SetStatus($"导入 {result.Added} 张，跳过 {result.Skipped} 张。" + string.Join("；", result.Errors.Take(3)));
    private Task ImportTelegramAsync() => RunAsync(async ct =>
    {
        var input = await Dialogs.Form(this, "导入 Telegram", "贴纸包会出现在当前面板的新标签中。再次导入同一包会跳过已有素材。", ("贴纸链接或包名称", "https://t.me/addstickers/jiulm", false));
        if (input == null) return;
        using var client = Client();
        SetStatus("正在读取贴纸包…");
        var set = await client.GetSetAsync(TelegramStickerClient.ParseSetName(input[0]), ct);
        ShowImport(await Library().ImportTelegramAsync(client, set, set.Stickers, Progress(), ct, SelectFolder));
    });
    private Task ExportAsync() => RunAsync(async ct =>
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "导出 Telegram 规格素材" });
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) SetStatus("已导出到 " + await Library().ExportAsync(_folder!, path, Progress(), ct));
    });
    private Task PublishAsync() => RunAsync(async ct =>
    {
        using var client = Client(); var library = Library();
        var publisher = new TelegramStickerPublisher(client, Media(), library.Save);
        var plan = await publisher.PrepareAsync(_folder!, _connection.OwnerUserId, "", Progress(), ct);
        if (!await Dialogs.Confirm(this, "确认发布到 Telegram", $"所有者：{plan.OwnerId}\n机器人：@{plan.Bot.Username}\n目标包：{plan.SetName}\n\n新增 {plan.Added} 张，更新 {plan.Replaced} 张，删除 {plan.Deleted} 张。\n" + (plan.NewSet ? "将创建你自己账号的贴纸包，来源包保持原样。" : "将同步到此前由本应用创建的贴纸包。"))) return;
        await publisher.PublishAsync(plan, Progress(), ct);
    });
    private Task ManageFolderAsync() => RunAsync(async _ =>
    {
        var folder = _folder!;
        var value = await Dialogs.Form(this, "管理标签", "修改标签名称；填写“删除此标签”可删除本地标签，不会删除 Telegram 远端包。", ("标签名称", folder.Name, false));
        if (value == null) return;
        if (value[0] == "删除此标签")
        {
            if (folder.Id == FavoritesStore.DefaultMemeFolderId) throw new InvalidOperationException("默认表情包标签不能删除。");
            if (!await Dialogs.Confirm(this, "删除标签", $"删除“{folder.Name}”及其 {folder.Items.Count} 张本地素材？")) return;
            App.Favorites.DeleteFolder(folder); _folder = null; _mode = "history"; RenderToolbar();
        }
        else if (!string.IsNullOrWhiteSpace(value[0])) App.Favorites.RenameFolder(folder, value[0].Trim());
    });
    private Task RenameItemAsync(ClipItem item) => RunAsync(async ct =>
    {
        await Media().EnsureAssetAsync(item, ct);
        var input = await Dialogs.Form(this, "标题与 Emoji", "用于本地搜索和 Telegram 贴纸关联。", ("标题", item.Title ?? "", false), ("Emoji", item.StickerEmojiLabel, false));
        if (input == null) return;
        var emojis = StickerLibraryService.ParseEmojis(input[1]);
        item.Title = input[0]; item.Sticker!.Emojis = emojis; item.Sticker.Revision++; Library().Save();
    });
    private Task ReplaceAsync(ClipItem item) => RunAsync(async ct =>
    {
        var files = await PickFiles(false);
        if (files.Length > 0) Library().ReplaceLocal(_folder!, item, await Media().ImportFileAsync(files[0], ct), preserveOriginal: true);
    });
    private Task EditAsync(ClipItem item) => RunAsync(async ct =>
    {
        var input = await Dialogs.Form(this, "编辑表情包", "保留原件。裁剪用 0～1 的比例；例如宽度 0.5 表示保留一半。动画可截取最多 3 秒后发布。",
            ("添加文字", "", false), ("裁剪：左、上、宽、高（逗号分隔）", "0, 0, 1, 1", false),
            ("动画：开始秒、时长秒、速度倍数", "0, 3, 1", false));
        if (input == null) return;
        static double[] Numbers(string text, int count)
        {
            var values = text.Replace('，', ',').Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (values.Length != count || values.Any(v => !double.IsFinite(v))) throw new ArgumentException("请按提示填写数字。");
            return values;
        }
        var crop = Numbers(input[1], 4); var animation = Numbers(input[2], 3);
        var edit = new StickerEditOptions(crop[0], crop[1], crop[2], crop[3], input[0], animation[0], animation[1], animation[2]);
        Library().ReplaceLocal(_folder!, item, await Media().EditAsync(item, edit, ct)); SetStatus("修改已保存，可预览或发布更新。");
    });
    private Task PreviewAsync(ClipItem item) => RunAsync(async ct =>
    {
        if (item.Kind == ClipKind.Image)
        {
            using var full = item.ImageBlobName != null ? App.Persistence.LoadImageBlob(item.ImageBlobName) : null;
            var window = new Window { Title = item.TitleOrUntitled, Width = 560, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Image { Source = full ?? item.Image, Stretch = Stretch.Uniform, Margin = new Thickness(18) } };
            await window.ShowDialog(this); return;
        }
        string path = await Media().CreatePreviewAsync(item, ct);
        using var animation = new GifPreview(path);
        var preview = new Window { Title = "动画预览", Width = 450, Height = 480, Content = animation, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        await preview.ShowDialog(this);
    });
    private Task SettingsAsync() => RunAsync(async ct =>
    {
        try { LoadConnection(); } catch (Exception ex) { SetStatus(ex.Message); }
        var input = await Dialogs.Form(this, "Telegram 连接", "Token 保存在 macOS 钥匙串。所有者填写你主账号的 Telegram 用户 ID；填 0 可读取你发给机器人的 /start 消息。", 
            ("机器人 token", _connection.Token, true), ("所有者用户 ID", _connection.OwnerUserId.ToString(), false), ("FFmpeg 目录（留空自动查找 Homebrew）", _connection.MediaToolsDirectory, false));
        if (input == null) return;
        if (!long.TryParse(input[1], out var owner) || owner < 0) throw new ArgumentException("用户 ID 应为正整数；填 0 可查找。");
        if (!string.IsNullOrWhiteSpace(input[0]))
        {
            using var client = new TelegramStickerClient(input[0].Trim());
            var bot = await client.GetMeAsync(ct);
            if (owner == 0)
            {
                var owners = await client.FindOwnersAsync(ct);
                if (owners.Count == 1) owner = owners[0].Id;
                else if (owners.Count > 1) throw new InvalidOperationException("找到多个账号，请明确填写所有者 ID：" + string.Join("，", owners.Select(o => $"{o.Name} ({o.Id})")));
                else SetStatus($"请打开 https://t.me/{bot.Username} 发送 /start，再保存一次连接以识别所有者。");
            }
        }
        var connection = new TelegramConnection(input[0].Trim(), owner, input[2].Trim());
        new TelegramConnectionStore(App.Persistence.RootDirectory).Save(connection);
        _connection = connection; _connectionLoaded = true;
        if (owner > 0) SetStatus($"连接已保存。贴纸包所有者：{owner}。");
    });
    private Task SystemSettingsAsync() => RunAsync(async _ =>
    {
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "⌘⌥V 显示面板；双击卡片粘贴。关闭窗口后继续在菜单栏运行。", TextWrapping = TextWrapping.Wrap });
        var login = new CheckBox { Content = "登录时启动 ClipBoard", IsEnabled = OperatingSystem.IsMacOS() && !App.Preview };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (login.IsEnabled) login.IsChecked = MacLogin.Enabled;
        login.IsCheckedChanged += (_, _) =>
        {
            try { message.Text = MacLogin.SetEnabled(login.IsChecked == true); }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        panel.Children.Add(login);
        panel.Children.Add(Button("打开辅助功能设置", () =>
        {
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open") { ArgumentList = { "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility" }, UseShellExecute = false });
            return Task.CompletedTask;
        }));
        panel.Children.Add(new TextBlock { Text = "复制和记录历史不需要辅助功能权限。自动粘贴需要授权；未授权时可手动按 ⌘V。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(message);
        var dialog = new Window { Title = "设置", Width = 470, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        panel.Children.Add(Button("完成", () => { dialog.Close(); return Task.CompletedTask; }));
        await dialog.ShowDialog(this);
    });
}
