using System.Collections.ObjectModel;
#if AVALONIA
using BitmapSource = Avalonia.Media.Imaging.Bitmap;
#else
using System.Windows.Media.Imaging;
#endif
using ClipBoard.Models;

namespace ClipBoard.Services;

public class FavoritesStore
{
    public static readonly Guid DefaultMemeFolderId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private readonly PersistenceService _persistence;
    private HistoryStore? _history;

    public ObservableCollection<ClipItem> PinnedHistory { get; } = new();
    public ObservableCollection<FavoriteFolder> Folders { get; } = new();

    public void SetHistoryStore(HistoryStore history) { _history = history; }

    public FavoriteFolder EnsureDefaultMemeFolder()
    {
        var existing = Folders.FirstOrDefault(f => f.Id == DefaultMemeFolderId);
        if (existing != null) return existing;
        var f = new FavoriteFolder { Id = DefaultMemeFolderId, Name = "表情包", Kind = FolderKind.Meme, Order = -1 };
        Folders.Insert(0, f);
        Save();
        return f;
    }

    public FavoritesStore(PersistenceService persistence, PersistedData data)
    {
        _persistence = persistence;

        foreach (var item in data.PinnedHistory)
        {
            RehydrateImage(item);
            PinnedHistory.Add(item);
        }

        foreach (var f in data.Folders.OrderBy(f => f.Order))
            Folders.Add(f);

        foreach (var fav in data.Favorites)
        {
            RehydrateImage(fav);
            var folder = Folders.FirstOrDefault(f => f.Id == fav.FolderId);
            folder?.Items.Add(fav);
        }
    }

    private void RehydrateImage(ClipItem item)
    {
        if (item.Sticker != null && !string.IsNullOrEmpty(item.ImageBlobName))
        {
            item.Image = _persistence.LoadImageThumbnail(item.ImageBlobName);
            return;
        }
        if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
        {
            item.Image = _persistence.LoadImageThumbnail(item.ImageBlobName); // 只载入缩略图
        }
        else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
        {
            item.GifBytes = _persistence.LoadGifBlob(item.GifBlobName);
            try { item.Image = item.GifSource; } catch { }
        }
    }

    public void PinHistory(ClipItem src, HistoryStore history)
    {
        var clone = src.Clone();
        clone.IsPinned = true;
        clone.Timestamp = DateTime.Now;
        // 和取消顶置一样转移文件，无需删除并重写原图或 GIF。
        if (!history.Detach(src)) return;
        PinnedHistory.Insert(0, clone);
        Save();
    }

    public void UnpinHistory(ClipItem pinned, HistoryStore history)
    {
        if (!PinnedHistory.Remove(pinned)) return;

        // 把 blob 所有权转移给恢复后的历史项（不删除），保留全分辨率；
        // 之后该历史项被淘汰时再由 HistoryStore 统一清理 blob。
        var back = pinned.Clone();   // Clone 已复制 ImageBlobName/GifBlobName
        back.IsPinned = false;
        if (back.Kind == ClipKind.Image && !string.IsNullOrEmpty(back.ImageBlobName))
            back.Image = _persistence.LoadImageThumbnail(back.ImageBlobName);
        history.InsertExisting(back);
        Save();
    }

    public FavoriteFolder CreateFolder(string name, FolderKind kind = FolderKind.Normal)
    {
        var folder = new FavoriteFolder
        {
            Name = name,
            Order = Folders.Count,
            Kind = kind,
        };
        Folders.Add(folder);
        Save();
        return folder;
    }

    public void RenameFolder(FavoriteFolder f, string newName)
    {
        f.Name = newName;
        Save();
    }

    public void DeleteFolder(FavoriteFolder f)
    {
        if (f.Id == DefaultMemeFolderId) return;
        if (!Folders.Remove(f)) return;
        foreach (var item in f.Items)
            DeleteUnusedBlob(item);
        for (int i = 0; i < Folders.Count; i++) Folders[i].Order = i;
        Save();
    }

    public void AddToFolder(ClipItem src, FavoriteFolder folder)
    {
        var clone = src.Clone();
        clone.FolderId = folder.Id;
        clone.Sticker?.ClearPublication();
        if (clone.Kind == ClipKind.Image)
        {
            // 取源的全分辨率原图，为收藏项保存独立 blob（与历史项解耦：历史被淘汰不影响收藏）。
            BitmapSource? fullRes = !string.IsNullOrEmpty(src.ImageBlobName)
                ? _persistence.LoadImageBlob(src.ImageBlobName) : src.Image;
            if (fullRes != null)
            {
                var toSave = fullRes;
                clone.ImageBlobName = _persistence.SaveImageBlob(toSave);
#if AVALONIA
                clone.PixelW = toSave.PixelSize.Width;
                clone.PixelH = toSave.PixelSize.Height;
#else
                clone.PixelW = toSave.PixelWidth;
                clone.PixelH = toSave.PixelHeight;
#endif
                clone.Image = _persistence.LoadImageThumbnail(clone.ImageBlobName);
#if AVALONIA
                if (!ReferenceEquals(fullRes, src.Image)) fullRes.Dispose();
#endif
            }
        }
        else if (clone.Kind == ClipKind.Gif)
        {
            var bytes = src.GifBytes ?? (src.GifBlobName is { Length: > 0 } name ? _persistence.LoadGifBlob(name) : null);
            if (bytes is { Length: > 0 })
            {
                clone.GifBytes = bytes;
                clone.GifBlobName = _persistence.SaveGifBlob(bytes);
                try { clone.Image = clone.GifSource; } catch { }
            }
        }
        folder.Items.Insert(0, clone);
        Save();
    }

#if !AVALONIA
    public static System.Windows.Media.Imaging.BitmapSource Downscale(System.Windows.Media.Imaging.BitmapSource src, int maxSide)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        int longest = Math.Max(w, h);
        if (longest <= maxSide) return src;
        double scale = (double)maxSide / longest;
        var t = new System.Windows.Media.Imaging.TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
        t.Freeze();
        return t;
    }
#endif

    public void RemoveFavorite(ClipItem item)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == item.FolderId);
        if (folder?.Items.Remove(item) != true) return;
        if (item.Sticker?.PublishedFileId is { } published && folder.Telegram != null)
            folder.Telegram.DeletedFileIds.Add(published);
        if (item.Sticker?.PendingFileId is { } pending && folder.Telegram != null)
            folder.Telegram.DeletedFileIds.Add(pending);
        DeleteUnusedBlob(item);
        Save();
    }

    public void RemovePinnedHistory(ClipItem item)
    {
        if (!PinnedHistory.Remove(item)) return;
        DeleteUnusedBlob(item);
        Save();
    }

    internal void DeleteUnusedBlob(ClipItem item)
    {
        var remaining = PinnedHistory.Concat(Folders.SelectMany(f => f.Items))
            .Concat(_history?.Items ?? Enumerable.Empty<ClipItem>()).ToArray();
        // 包括原件和修改版；原件仍被其他收藏引用时不可清理。
        foreach (var name in new[] { item.ImageBlobName, item.GifBlobName, item.Sticker?.OriginalBlobName, item.Sticker?.WorkingBlobName }
                     .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!remaining.Any(i => new[] { i.ImageBlobName, i.GifBlobName, i.Sticker?.OriginalBlobName, i.Sticker?.WorkingBlobName }
                    .Contains(name, StringComparer.OrdinalIgnoreCase)))
                _persistence.DeleteImageBlob(name!);
        }
    }

    public void MoveFavorite(ClipItem item, FavoriteFolder target)
    {
        var src = Folders.FirstOrDefault(f => f.Id == item.FolderId);
        if (src == target) return;
        if (item.Sticker?.PublishedFileId is { } published && src?.Telegram != null)
            src.Telegram.DeletedFileIds.Add(published);
        item.Sticker?.ClearPublication();
        src?.Items.Remove(item);
        item.FolderId = target.Id;
        target.Items.Insert(0, item);
        Save();
    }

    public void Save()
    {
        var data = new PersistedData
        {
            Folders = Folders.Select(f => new FavoriteFolder { Id = f.Id, Name = f.Name, Order = f.Order, Kind = f.Kind, Telegram = f.Telegram?.Clone() }).ToList(),
            PinnedHistory = PinnedHistory.Select(StripImage).ToList(),
            Favorites = Folders.SelectMany(f => f.Items.Select(i => { var c = StripImage(i); c.FolderId = f.Id; return c; })).ToList(),
            History = _history?.Items.Select(StripImage).ToList() ?? new(),
            Settings = new AppSettings
            {
                StartWithWindows = App.Settings.StartWithWindows,
                ShowInvisibleChars = App.Settings.ShowInvisibleChars,
            },
        };
        _persistence.SaveDebounced(data);
    }

    private static ClipItem StripImage(ClipItem src) => new()
    {
        Id = src.Id,
        Kind = src.Kind,
        Text = src.Text,
        ImageBlobName = src.ImageBlobName,
        GifBlobName = src.GifBlobName,
        FilePaths = src.FilePaths?.ToArray(),
        Timestamp = src.Timestamp,
        IsPinned = src.IsPinned,
        FolderId = src.FolderId,
        Title = src.Title,
        PixelW = src.PixelW,
        PixelH = src.PixelH,
        Sticker = src.Sticker?.Clone(),
    };
}
