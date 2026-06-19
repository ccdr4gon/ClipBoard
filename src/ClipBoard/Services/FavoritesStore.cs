using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
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

        foreach (var item in data.PinnedHistory.OrderByDescending(i => i.Timestamp))
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
        if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
        {
            item.Image = _persistence.LoadImageThumbnail(item.ImageBlobName); // 只载入缩略图
        }
        else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
        {
            item.GifBytes = _persistence.LoadGifBlob(item.GifBlobName);
        }
    }

    public void PinHistory(ClipItem src, HistoryStore history)
    {
        // history.Remove(src) 会删除 src 的图片 blob，所以要先把全分辨率原图取出来，
        // 再为顶置项保存一份独立 blob（修复了以前“顶置后重启图片丢失”的隐患）。
        BitmapSource? fullRes = null;
        if (src.Kind == ClipKind.Image)
            fullRes = !string.IsNullOrEmpty(src.ImageBlobName) ? _persistence.LoadImageBlob(src.ImageBlobName) : src.Image;

        history.Remove(src);
        var clone = src.Clone();
        clone.IsPinned = true;
        clone.Timestamp = DateTime.Now;
        if (clone.Kind == ClipKind.Image)
        {
            clone.ImageBlobName = null;
            if (fullRes != null)
            {
                clone.ImageBlobName = _persistence.SaveImageBlob(fullRes);
                clone.PixelW = fullRes.PixelWidth;
                clone.PixelH = fullRes.PixelHeight;
                clone.Image = _persistence.LoadImageThumbnail(clone.ImageBlobName);
            }
        }
        else if (clone.Kind == ClipKind.Gif && clone.GifBytes is { Length: > 0 } && string.IsNullOrEmpty(clone.GifBlobName))
        {
            clone.GifBlobName = _persistence.SaveGifBlob(clone.GifBytes);
        }
        PinnedHistory.Insert(0, clone);
        Save();
    }

    public void UnpinHistory(ClipItem pinned, HistoryStore history)
    {
        PinnedHistory.Remove(pinned);

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
        foreach (var item in f.Items)
        {
            if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
                _persistence.DeleteImageBlob(item.ImageBlobName);
            else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
                _persistence.DeleteGifBlob(item.GifBlobName);
        }
        Folders.Remove(f);
        for (int i = 0; i < Folders.Count; i++) Folders[i].Order = i;
        Save();
    }

    public void AddToFolder(ClipItem src, FavoriteFolder folder)
    {
        var clone = src.Clone();
        clone.FolderId = folder.Id;
        if (clone.Kind == ClipKind.Image)
        {
            // 取源的全分辨率原图，为收藏项保存独立 blob（与历史项解耦：历史被淘汰不影响收藏）。
            BitmapSource? fullRes = !string.IsNullOrEmpty(src.ImageBlobName)
                ? _persistence.LoadImageBlob(src.ImageBlobName) : src.Image;
            if (fullRes != null)
            {
                var toSave = folder.Kind == FolderKind.Meme ? Downscale(fullRes, 960) : fullRes;
                clone.ImageBlobName = _persistence.SaveImageBlob(toSave);
                clone.PixelW = toSave.PixelWidth;
                clone.PixelH = toSave.PixelHeight;
                clone.Image = _persistence.LoadImageThumbnail(clone.ImageBlobName);
            }
        }
        else if (clone.Kind == ClipKind.Gif && clone.GifBytes is { Length: > 0 }
                 && string.IsNullOrEmpty(clone.GifBlobName))
        {
            clone.GifBlobName = _persistence.SaveGifBlob(clone.GifBytes);
        }
        folder.Items.Insert(0, clone);
        Save();
    }

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

    public void RemoveFavorite(ClipItem item)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == item.FolderId);
        folder?.Items.Remove(item);
        if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
            _persistence.DeleteImageBlob(item.ImageBlobName);
        else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
            _persistence.DeleteGifBlob(item.GifBlobName);
        Save();
    }

    public void MoveFavorite(ClipItem item, FavoriteFolder target)
    {
        var src = Folders.FirstOrDefault(f => f.Id == item.FolderId);
        src?.Items.Remove(item);
        item.FolderId = target.Id;
        target.Items.Insert(0, item);
        Save();
    }

    public void Save()
    {
        var data = new PersistedData
        {
            Folders = Folders.Select(f => new FavoriteFolder { Id = f.Id, Name = f.Name, Order = f.Order, Kind = f.Kind }).ToList(),
            PinnedHistory = PinnedHistory.Select(StripImage).ToList(),
            Favorites = Folders.SelectMany(f => f.Items.Select(i => { var c = StripImage(i); c.FolderId = f.Id; return c; })).ToList(),
            History = _history?.Items.Select(StripImage).ToList() ?? new(),
            Settings = App.Settings,
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
    };
}
