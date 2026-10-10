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
using Avalonia.VisualTree;
using ClipBoard.Models;
using ClipBoard.Services;
using ClipBoard.Views;

namespace ClipBoard;

public sealed partial class MainWindow : Window
{
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
        BuildPanel();
        Opened += (_, _) => FocusSearch();
        _cancel.Click += (_, _) => _work?.Cancel();
        // 同步响应文字变化（TextChanged 是异步派发的），打开面板时清空搜索后立即回到顶部。
        _search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { RenderCards(); ResetPosition(); } };
        App.History.Items.CollectionChanged += ItemsChanged;
        App.Favorites.PinnedHistory.CollectionChanged += ItemsChanged;
        App.Favorites.Folders.CollectionChanged += (_, _) => { SubscribeFolders(); RenderTabs(); };
        SubscribeFolders(); RenderTabs(); RenderToolbar(); RenderCards();
        Closing += (_, e) => { if (!Application.Quitting) { e.Cancel = true; Hide(); } };
        AddHandler(KeyDownEvent, OnPanelKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // 列表里有控件拿到过焦点（单击、Tab 键、右键菜单）后，隐藏时的渲染不再沿用旧行（见 SameRenderState）。
        _items.AddHandler(GotFocusEvent, (_, _) => _rowsTouched = true, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        // 记下最后拿到焦点的标签，重绘标签时不保留它（见 RenderTabs）。
        AddHandler(GotFocusEvent, (_, e) => _focusedTab = (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } tab && tab.Parent == _tabs ? tab : null,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        // 与 Windows 一致：单击即粘贴，按住 Shift 粘贴为纯文本。在列表上处理，点到行内空白或内边距也算；
        // Control+单击是 macOS 的右键，留给菜单。
        _items.Tapped += async (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not ClipItem item) return;
            e.Handled = true;
            await CopyAsync(item, true, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        };
        Deactivated += (_, _) => { if (!_pinned && !_contextOpen && _work == null && !Dialogs.HasModal(this)) Hide(); };
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
    public void SetStatus(string message) { _status.Text = message; _status.IsVisible = !string.IsNullOrEmpty(message); }
    internal void FocusSearch() => _search.Focus();
    /// <summary>每次重新打开面板：清空搜索，列表回到顶部并选中第一条，回车即可粘贴最新内容。</summary>
    internal void ResetView()
    {
        if (string.IsNullOrEmpty(_search.Text)) { ResyncHiddenRows(); ResetPosition(); }
        else _search.Text = ""; // 文字变化会重新渲染并回到顶部
    }
    private void ResetPosition()
    {
        _items.SelectedIndex = _items.ItemCount > 0 ? 0 : -1;
        _items.FindDescendantOfType<ScrollViewer>()?.ScrollToHome();
    }
    internal void CancelWork() => _work?.Cancel();
    private IProgress<string> Progress() => new Progress<string>(SetStatus);
    private static Button Button(string title, Func<Task> action)
    {
        var b = new Button { Content = title, VerticalAlignment = VerticalAlignment.Center };
        b.Click += async (_, _) => await action(); return b;
    }
    public IReadOnlyList<ClipItem> VisibleItems => (_folder != null ? _folder.Items
        : App.Favorites.PinnedHistory.Concat(App.History.Items))
        .Where(i => _mode != "images" || i.Kind is ClipKind.Image or ClipKind.Gif or ClipKind.VideoSticker or ClipKind.VectorSticker)
        .Where(i => _mode != "emoji" || i.Kind == ClipKind.Text && IsPureEmoji(i.Text))
        .Where(i => string.IsNullOrWhiteSpace(_search.Text) || (i.Title + " " + i.Preview + " " + i.StickerEmojiLabel).Contains(_search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    private static bool IsPureEmoji(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (string.IsNullOrWhiteSpace(element)) continue;
            if (!element.EnumerateRunes().Any(r => r.Value is >= 0x1F000 and <= 0x1FFFF
                or >= 0x2600 and <= 0x27BF or >= 0x2190 and <= 0x21FF or >= 0x2300 and <= 0x23FF
                or 0x200D or 0xFE0F or 0x20E3 or >= 0xE0020 and <= 0xE007F)) return false;
        }
        return true;
    }
    private void AttachItemActions(Control card, ClipItem item)
    {
        // 菜单项等第一次打开时再生成：右键 / Control+单击 / 菜单键都会先触发 Opening 再弹出。原先每行一生成就建好
        // 全部菜单项（每个收藏夹一项），几百行就是几千个 MenuItem，而绝大多数菜单从不打开。
        // 内容仍按生成这一行时的状态决定，与原先一致。
        var menu = new ContextMenu();
        var current = _folder; bool rich = item.HasRichText, pinned = item.IsPinned;
        var targets = App.Favorites.Folders.Where(f => f != current).Select(f => (Folder: f, f.Name)).ToArray();
        bool filled = false;
        void Fill()
        {
            if (filled) return;
            filled = true;
            if (rich)
            {
                AddMenu(menu, "粘贴（保留格式）", () => CopyAsync(item, true));
                AddMenu(menu, "粘贴为纯文本", () => CopyAsync(item, true, plainText: true));
                AddMenu(menu, "复制为纯文本", () => CopyAsync(item, false, plainText: true));
            }
            else AddMenu(menu, "粘贴到原应用", () => CopyAsync(item, true));
            AddMenu(menu, "复制", () => CopyAsync(item, false));
            if (item.Kind is ClipKind.Image or ClipKind.Gif or ClipKind.VideoSticker or ClipKind.VectorSticker)
                AddMenu(menu, "预览", () => PreviewAsync(item));
            if (current != null)
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
                AddMenu(menu, pinned ? "取消置顶" : "置顶", () => { if (item.IsPinned) App.Favorites.UnpinHistory(item, App.History); else App.Favorites.PinHistory(item, App.History); return Task.CompletedTask; });
                AddMenu(menu, "删除", () => { if (item.IsPinned) App.Favorites.RemovePinnedHistory(item); else App.History.Remove(item); return Task.CompletedTask; });
            }
            var collect = new MenuItem { Header = "收藏到" };
            foreach (var (target, name) in targets)
            {
                var option = new MenuItem { Header = name };
                option.Click += async (_, _) => await RunAsync(_ => { App.Favorites.AddToFolder(item, target); return Task.CompletedTask; });
                collect.Items.Add(option);
            }
            menu.Items.Add(collect);
        }
        menu.Tag = (Action)Fill;
        menu.Opening += (_, _) => Fill(); // ContextMenu.Open() 不触发 Opening；这类菜单只由 ContextRequested 打开
        card.ContextMenu = menu;
        menu.Opened += (_, _) => _contextOpen = _rowsTouched = true;
        menu.Closed += (_, _) => _contextOpen = false;
    }
    /// <summary>不弹出菜单就生成菜单项（测试读取菜单内容用）。</summary>
    internal static void EnsureMenuItems(ContextMenu menu) => (menu.Tag as Action)?.Invoke();
    private static void AddMenu(ContextMenu menu, string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title }; item.Click += async (_, _) => await action(); menu.Items.Add(item);
    }
    private Task CopyAsync(ClipItem item, bool paste, bool plainText = false) => RunAsync(_ => Application.CopyAsync(item, paste, plainText));
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
        => (await Dialogs.PickAsync(this, () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入图片或贴纸", AllowMultiple = multiple, FileTypeFilter = [Images] })))
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
        var selected = await Dialogs.PickAsync(this, () => StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "导出 Telegram 规格素材" }));
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
            await Dialogs.ShowAsync(window, this); return;
        }
        string path = await Media().CreatePreviewAsync(item, ct);
        using var animation = new GifPreview(path);
        var preview = new Window { Title = "动画预览", Width = 450, Height = 480, Content = animation, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        await Dialogs.ShowAsync(preview, this);
    });
    private Task SettingsAsync() => RunAsync(SettingsFormAsync);
    private async Task SettingsFormAsync(CancellationToken ct)
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
    }
    private Task SystemSettingsAsync() => RunAsync(async _ =>
    {
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "⌃⌘V 显示面板；单击或回车粘贴，按住 Shift 粘贴为纯文本。关闭窗口后继续在菜单栏运行。", TextWrapping = TextWrapping.Wrap });
        var login = new CheckBox { Content = "登录时启动 ClipBoard", IsEnabled = OperatingSystem.IsMacOS() && !App.Preview };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (login.IsEnabled) login.IsChecked = MacLogin.Enabled;
        login.IsCheckedChanged += (_, _) =>
        {
            try { message.Text = MacLogin.SetEnabled(login.IsChecked == true); }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        panel.Children.Add(login);

        panel.Children.Add(Button("打开" + (OperatingSystem.IsMacOSVersionAtLeast(27) ? "设备控制与数据访问" : "辅助功能") + "设置", () =>
        {
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open") { ArgumentList = { "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility" }, UseShellExecute = false });
            return Task.CompletedTask;
        }));
        string trust = !OperatingSystem.IsMacOS() || App.Preview ? "" :
            $"当前状态：辅助功能{(MacNative.IsAxTrusted ? "已授权" : "未授权")}，发送按键{(MacNative.CanPostEvents ? "已授权" : "未授权")}。";
        panel.Children.Add(new TextBlock { Text = "复制和记录历史不需要辅助功能权限。自动粘贴需要在 隐私与安全性 → " + App.AccessibilityPane + " 中允许 ClipBoard；未授权时可手动按 ⌘V。" + trust +
            "\n更新应用后若开关显示已打开但仍无法自动粘贴，请在列表里用“−”移除 ClipBoard 再重新添加，然后退出并重新打开 ClipBoard。" +
            "\n请只保留“应用程序”里的一份 ClipBoard.app：在“下载”里反复解压会生成 ClipBoard 2、3… 多个副本，授权会对不上。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(message);
        var dialog = new Window { Title = "设置", Width = 470, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        bool telegram = false;
        panel.Children.Add(Button("Telegram 连接…", () => { telegram = true; dialog.Close(); return Task.CompletedTask; }));
        panel.Children.Add(Button("完成", () => { dialog.Close(); return Task.CompletedTask; }));
        await Dialogs.ShowAsync(dialog, this);
        if (telegram) await SettingsFormAsync(default);
    });
}
