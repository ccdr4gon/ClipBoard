using System.Globalization;
using System.IO;
using System.Text.Json;
using ClipBoard.Models;

namespace ClipBoard.Services;

public sealed record StickerImportResult(FavoriteFolder Folder, int Added, int Skipped, IReadOnlyList<string> Errors);

public sealed class StickerLibraryService(FavoritesStore favorites, PersistenceService persistence, StickerMediaService media)
{
    public async Task<StickerImportResult> ImportFilesAsync(FavoriteFolder folder, IEnumerable<string> files,
        IProgress<string>? progress, CancellationToken ct)
    {
        int added = 0;
        var errors = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在导入 {Path.GetFileName(file)}…");
            try
            {
                var item = await media.ImportFileAsync(file, ct);
                item.FolderId = folder.Id;
                folder.Items.Add(item);
                Save();
                added++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}：{ex.Message}"); }
        }
        return new(folder, added, 0, errors);
    }

    public async Task<StickerImportResult> ImportTelegramAsync(TelegramStickerClient client, TelegramStickerSet remote,
        IEnumerable<TelegramSticker> selection, IProgress<string>? progress, CancellationToken ct, Action<FavoriteFolder>? folderReady = null)
    {
        if (remote.StickerType != "regular") throw new InvalidOperationException("这一版导入普通贴纸包；自定义 emoji 和面具包暂不作为普通贴纸处理。");
        var folder = favorites.Folders.FirstOrDefault(f => f.Kind == FolderKind.Meme && f.Telegram?.SourceSetName == remote.Name);
        if (folder == null)
        {
            folder = favorites.CreateFolder(remote.Title, FolderKind.Meme);
            folder.Telegram = new TelegramPackBinding { SourceSetName = remote.Name };
        }
        folderReady?.Invoke(folder);
        int added = 0, skipped = 0, index = 0;
        var errors = new List<string>();
        foreach (var sticker in selection)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            if (folder.Items.Any(i => i.Sticker?.SourceSetName == remote.Name && i.Sticker.SourceUniqueId == sticker.FileUniqueId))
            { skipped++; continue; }
            progress?.Report($"导入 {remote.Title}：第 {index} 张…");
            try
            {
                var bytes = await client.DownloadAsync(sticker.FileId, ct);
                var item = await media.ImportBytesAsync(bytes, sticker.Format, ct);
                item.FolderId = folder.Id;
                item.Title = $"{remote.Title} {Array.IndexOf(remote.Stickers, sticker) + 1}";
                item.Sticker!.SourceSetName = remote.Name;
                item.Sticker.SourceFileId = sticker.FileId;
                item.Sticker.SourceUniqueId = sticker.FileUniqueId;
                item.Sticker.Emojis = ParseEmojis(sticker.Emoji ?? "🙂");
                folder.Items.Add(item);
                Save();
                added++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"第 {index} 张：{ex.Message}"); }
        }
        Save();
        return new(folder, added, skipped, errors);
    }

    public void ReplaceLocal(FavoriteFolder folder, ClipItem old, ClipItem replacement, bool preserveOriginal = false)
    {
        int index = folder.Items.IndexOf(old);
        if (index < 0) throw new InvalidOperationException("该素材已经不在当前收藏夹。");
        var importedCopy = replacement.Clone();
        if (preserveOriginal && old.Sticker != null)
        {
            var asset = old.Sticker.Clone();
            asset.WorkingBlobName = replacement.Sticker!.WorkingBlobName;
            asset.Format = replacement.Sticker.Format;
            asset.DurationSeconds = replacement.Sticker.DurationSeconds;
            asset.Revision++;
            replacement.Sticker = asset;
        }
        replacement.Id = old.Id;
        replacement.FolderId = folder.Id;
        replacement.Title = old.Title;
        folder.Items[index] = replacement;
        Save();
        favorites.DeleteUnusedBlob(old);
        if (preserveOriginal) favorites.DeleteUnusedBlob(importedCopy);
    }

    public static string[] ParseEmojis(string input)
    {
        var result = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(input);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (string.IsNullOrWhiteSpace(element) || element is "," or "，") continue;
            result.Add(element);
        }
        var emojis = result.Distinct().ToArray();
        if (emojis.Length is < 1 or > 20) throw new ArgumentException("请填写 1～20 个关联 emoji。");
        return emojis;
    }

    public async Task<string> ExportAsync(FavoriteFolder folder, string destination, IProgress<string>? progress, CancellationToken ct)
    {
        if (folder.Items.Count == 0) throw new InvalidOperationException("当前包没有素材。");
        var files = new List<(ClipItem Item, PreparedSticker Media)>();
        foreach (var item in folder.Items.ToArray())
        {
            progress?.Report($"检查并转换：{item.TitleOrUntitled}");
            files.Add((item, await media.PrepareTelegramAsync(item, ct)));
        }
        Save();
        string output = Path.Combine(destination, $"Telegram-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(output);
        var manifest = new List<object>();
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            string name = $"{i + 1:D3}" + Path.GetExtension(file.Media.Path);
            File.Copy(file.Media.Path, Path.Combine(output, name));
            manifest.Add(new { file = name, format = file.Media.Format, title = file.Item.Title,
                emoji = file.Item.Sticker!.Emojis });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "stickers.json"),
            JsonSerializer.Serialize(new { title = folder.Name, stickers = manifest }, new JsonSerializerOptions { WriteIndented = true }), ct);
        return output;
    }

    public void Save() { favorites.Save(); persistence.FlushSync(); }
}
