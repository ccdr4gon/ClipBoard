using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBoard.Models;
using ClipBoard.Services;
using WpfAnimatedGif;

namespace ClipBoard.Views;

/// <summary>把贴纸操作接到原有标签页；只为连接、编辑等单次操作打开对话框。</summary>
public sealed class StickerTabActions
{
    private readonly Window _owner;
    private readonly FavoritesStore _favorites;
    private readonly PersistenceService _persistence;
    private readonly Action<FavoriteFolder> _selectFolder;
    private readonly TelegramConnectionStore _connections;
    private TelegramConnection _connection;
    private StickerMediaService _media;
    private StickerLibraryService _library;
    private CancellationTokenSource? _operation;
    public bool IsBusy => _operation != null;
    public event Action<string>? StatusChanged;
    public event Action<bool>? BusyChanged;

    public StickerTabActions(Window owner, FavoritesStore favorites, PersistenceService persistence, Action<FavoriteFolder> selectFolder)
    {
        _owner = owner; _favorites = favorites; _persistence = persistence; _selectFolder = selectFolder;
        _connections = new(persistence.RootDirectory);
        try { _connection = _connections.Load(); } catch { _connection = new(); }
        _media = new(persistence, _connection.MediaToolsDirectory);
        _library = new(favorites, persistence, _media);
    }
    private void Status(string text) => StatusChanged?.Invoke(text);
    private IProgress<string> Progress() => new Progress<string>(Status);
    public void Cancel() => _operation?.Cancel();

    public Task ConfigureAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        var dialog = new TelegramConnectionWindow(_connections, _connection) { Owner = _owner };
        if (dialog.ShowDialog() == true)
        {
            _connection = dialog.Connection;
            _media = new(_persistence, _connection.MediaToolsDirectory);
            _library = new(_favorites, _persistence, _media);
            Status("Telegram 连接已保存。");
        }
        return Task.CompletedTask;
    }

    private async Task<bool> RequireConnection()
    {
        if (_connection.Token.Length == 0) await ConfigureAsync();
        return _connection.Token.Length > 0;
    }
    public async Task ImportTelegramAsync()
    {
        if (IsBusy || !await RequireConnection()) return;
        var dialog = new FolderNameDialog("导入 Telegram 贴纸包", "", showKindPicker: false, labelText: "贴纸包链接或名称：") { Owner = _owner };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            await ImportLinkAsync(dialog.FolderName);
    }

    public async Task ImportLinkAsync(string link)
    {
        if (IsBusy || !await RequireConnection()) return;
        await Run(async ct =>
        {
            using var client = new TelegramStickerClient(_connection.Token);
            var remote = await client.GetSetAsync(TelegramStickerClient.ParseSetName(link), ct);
            var result = await _library.ImportTelegramAsync(client, remote, remote.Stickers, Progress(), ct, _selectFolder);
            ReportImport(result);
        });
    }

    public async Task ImportSelectedAsync()
    {
        if (IsBusy || !await RequireConnection()) return;
        var dialog = new TelegramImportWindow(_connection) { Owner = _owner };
        if (dialog.ShowDialog() != true || dialog.RemoteSet == null) return;
        await Run(async ct =>
        {
            using var client = new TelegramStickerClient(_connection.Token);
            ReportImport(await _library.ImportTelegramAsync(client, dialog.RemoteSet, dialog.SelectedStickers, Progress(), ct, _selectFolder));
        });
    }

    public async Task ImportFilesAsync(FavoriteFolder folder, string[]? paths = null)
    {
        if (IsBusy) return;
        if (paths == null)
        {
            var dialog = FileDialog(true);
            if (dialog.ShowDialog(_owner) != true) return;
            paths = dialog.FileNames;
        }
        await Run(async ct => ReportImport(await _library.ImportFilesAsync(folder, paths, Progress(), ct)));
    }
    private void ReportImport(StickerImportResult result)
    {
        _selectFolder(result.Folder);
        Status($"{result.Folder.Name} · 新增 {result.Added} 张，已存在 {result.Skipped} 张，失败 {result.Errors.Count} 张");
        if (result.Errors.Count > 0) MessageBox.Show(_owner, string.Join("\n", result.Errors), "部分素材未导入");
    }

    public async Task EditAsync(FavoriteFolder folder, ClipItem item)
    {
        if (IsBusy) return;
        if (!await Run(async ct => { await _media.EnsureAssetAsync(item, ct); _library.Save(); })) return;
        var dialog = new StickerEditWindow(item, _media, _persistence) { Owner = _owner };
        if (dialog.ShowDialog() == true && dialog.Result is { } edited)
        {
            _library.ReplaceLocal(folder, item, edited);
            Status("修改已保存，可点击“发布 / 更新”同步到 Telegram。");
        }
    }
    public async Task ReplaceAsync(FavoriteFolder folder, ClipItem item)
    {
        if (IsBusy) return;
        var dialog = FileDialog(false);
        if (dialog.ShowDialog(_owner) != true) return;
        await Run(async ct =>
        {
            await _media.EnsureAssetAsync(item, ct);
            var replacement = await _media.ImportFileAsync(dialog.FileName, ct);
            _library.ReplaceLocal(folder, item, replacement, preserveOriginal: true);
            Status("素材已替换，原件已保留。");
        });
    }
    public async Task RestoreAsync(FavoriteFolder folder, ClipItem item)
    {
        if (IsBusy) return;
        await Run(async ct =>
        {
            _library.ReplaceLocal(folder, item, await _media.RestoreAsync(item, ct));
            Status("已恢复原件。");
        });
    }
    public async Task EmojiAsync(ClipItem item)
    {
        if (IsBusy) return;
        var dialog = new FolderNameDialog("关联 emoji", string.Join(" ", item.Sticker?.Emojis ?? ["🙂"]),
            showKindPicker: false, labelText: "关联 emoji（空格分隔）：") { Owner = _owner };
        if (dialog.ShowDialog() != true) return;
        await Run(async ct =>
        {
            var emojis = StickerLibraryService.ParseEmojis(dialog.FolderName);
            await _media.EnsureAssetAsync(item, ct);
            if (!item.Sticker!.Emojis.SequenceEqual(emojis)) { item.Sticker.Emojis = emojis; item.Sticker.Revision++; }
            item.NotifyMediaChanged(); _library.Save(); Status("关联 emoji 已保存。");
        });
    }
    public Task MoveAsync(FavoriteFolder folder, ClipItem item, int delta)
    {
        if (IsBusy) return Task.CompletedTask;
        int index = folder.Items.IndexOf(item), next = index + delta;
        if (index >= 0 && next >= 0 && next < folder.Items.Count)
        { folder.Items.Move(index, next); _library.Save(); Status("顺序已保存。"); }
        return Task.CompletedTask;
    }
    public Task RemoveAsync(ClipItem item)
    {
        if (IsBusy) return Task.CompletedTask;
        if (item.Sticker?.PublishedFileId != null && MessageBox.Show(_owner,
                "这张贴纸已发布。移除后，下次确认发布时也会从你的 Telegram 包删除。", "移除贴纸", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return Task.CompletedTask;
        _favorites.RemoveFavorite(item); _library.Save(); Status("贴纸已移除。");
        return Task.CompletedTask;
    }

    public async Task PreviewAsync(ClipItem item)
    {
        if (IsBusy) return;
        string? path = null;
        if (!await Run(async ct => { path = await _media.CreatePreviewAsync(item, ct); _library.Save(); })) return;
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path!); bitmap.EndInit(); bitmap.Freeze();
        ImageBehavior.SetAnimatedSource(image, bitmap);
        var dialog = new Window { Title = item.TitleOrUntitled, Width = 430, Height = 440, Owner = _owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = StickerUi.Paper, Content = image, ShowInTaskbar = false };
        dialog.Closed += (_, _) => ImageBehavior.SetAnimatedSource(image, null);
        dialog.ShowDialog();
    }

    public async Task ExportAsync(FavoriteFolder folder)
    {
        if (IsBusy) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择导出目录（将创建独立子目录）" };
        if (dialog.ShowDialog(_owner) == true)
            await Run(async ct => Status("已导出到：" + await _library.ExportAsync(folder, dialog.FolderName, Progress(), ct)));
    }

    public async Task PublishAsync(FavoriteFolder folder)
    {
        if (IsBusy || !await RequireConnection()) return;
        if (_connection.OwnerUserId <= 0)
        {
            if (!await Run(async ct =>
                {
                    using var client = new TelegramStickerClient(_connection.Token);
                    var owners = await client.FindOwnersAsync(ct);
                    if (owners.Count == 1)
                    { _connection = _connection with { OwnerUserId = owners[0].Id }; _connections.Save(_connection); }
                })) return;
            if (_connection.OwnerUserId <= 0) await ConfigureAsync();
            if (_connection.OwnerUserId <= 0) return;
        }
        await Run(async ct =>
        {
            using var client = new TelegramStickerClient(_connection.Token);
            var publisher = new TelegramStickerPublisher(client, _media, _library.Save);
            var plan = await publisher.PrepareAsync(folder, _connection.OwnerUserId, "", Progress(), ct);
            string summary = $"{(plan.NewSet ? "创建自己的新包" : "更新已有包")}：{plan.Title}\n所有者用户 ID：{plan.OwnerId}\n"
                + $"新增 {plan.Added} 张，修改 {plan.Replaced} 张，删除 {plan.Deleted} 张。\n按本地顺序排列。\n\nhttps://t.me/addstickers/{plan.SetName}\n\n现在发布这些改动？";
            if (MessageBox.Show(_owner, summary, "确认发布到 Telegram", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            { Status("已取消发布，本地修改仍保留。"); return; }
            await publisher.PublishAsync(plan, Progress(), ct);
            Status("已发布：https://t.me/addstickers/" + plan.SetName);
        });
    }

    public Task OpenRemoteAsync(FavoriteFolder folder)
    {
        string? name = folder.Telegram?.SetName ?? folder.Telegram?.SourceSetName;
        if (name != null) Process.Start(new ProcessStartInfo("https://t.me/addstickers/" + name) { UseShellExecute = true });
        else Status("这个包还没有 Telegram 链接。");
        return Task.CompletedTask;
    }
    private static Microsoft.Win32.OpenFileDialog FileDialog(bool multiple) => new()
    { Multiselect = multiple, Filter = "贴纸与图片|*.png;*.webp;*.gif;*.webm;*.tgs;*.jpg;*.jpeg;*.bmp|所有文件|*.*" };

    private async Task<bool> Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return false;
        _operation = new CancellationTokenSource(); BusyChanged?.Invoke(true);
        try { await action(_operation.Token); return true; }
        catch (OperationCanceledException) { Status("已取消，已完成的内容会保留。"); return false; }
        catch (Exception ex) { Status(ex.Message); return false; }
        finally { _operation.Dispose(); _operation = null; BusyChanged?.Invoke(false); }
    }
}
