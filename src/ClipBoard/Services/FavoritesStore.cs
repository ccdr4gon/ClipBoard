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

    public FavoritesStore(PersistenceService persistence, PersistedData data) : this(persistence, data, loadMedia: true) { }

    /// <param name="loadMedia">
    /// false：只建集合，不载入缩略图 / GIF。调用方已用 <see cref="MediaPreload"/> 在后台解码，
    /// 并保证在任何界面用到这些条目之前把结果赋回（Windows 启动时让解码与建主窗口重叠进行）。
    /// </param>
    public FavoritesStore(PersistenceService persistence, PersistedData data, bool loadMedia)
    {
        _persistence = persistence;

        // 先把媒体全部载好再入集合：与原来逐项「载入 → 加入」的结果一致，此时集合还没有任何订阅者。
        if (loadMedia) MediaPreload.Start(persistence, data, includeHistory: false).Complete();

        foreach (var item in data.PinnedHistory)
            PinnedHistory.Add(item);

        foreach (var f in data.Folders.OrderBy(f => f.Order))
            Folders.Add(f);

        foreach (var fav in data.Favorites)
        {
            var folder = Folders.FirstOrDefault(f => f.Id == fav.FolderId);
            folder?.Items.Add(fav);
        }
    }

    /// <summary>
    /// 启动时批量载入条目的缩略图与 GIF 字节。每张图的解码彼此独立、不共享状态，
    /// 所以交给几个后台线程并行做；<see cref="Complete"/> 在调用线程上按原顺序把结果赋回条目，
    /// GIF 首帧（GifSource）也仍在调用线程上解码。载入规则与原来逐项载入时完全相同：
    /// 顶置 / 收藏——贴纸有海报只载海报缩略图，否则图片载缩略图，GIF 载字节并解首帧；
    /// 历史——图片载缩略图，GIF 只载字节。
    /// </summary>
    public sealed class MediaPreload
    {
        private sealed class Job(ClipItem item, string? thumb, string? gif, bool firstFrame)
        {
            public readonly ClipItem Item = item;
            public readonly string? Thumb = thumb;
            public readonly string? Gif = gif;
            public readonly bool FirstFrame = firstFrame;
            public BitmapSource? Image;
            public byte[]? Bytes;
            public bool Loaded;
        }

        // 每个线程同一时刻要握着一整张原图的解码缓冲（4K 截图约 33 MB），所以线程数封顶。
        private static readonly int MaxWorkers = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);

        private readonly PersistenceService _persistence;
        private readonly List<Job> _jobs = new();   // 赋回顺序：顶置 → 收藏 → 历史
        private Job[] _queue = [];                  // 解码顺序：历史大图在前，最后剩下的都是小图
        private int _next = -1;
        private int _running;
        private int _workers;
        private int _stolen;
        private long _decodeMs = -1;
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private readonly ManualResetEvent _workersDone = new(true);

        private MediaPreload(PersistenceService persistence) { _persistence = persistence; }

        /// <summary>历史里要载入的图片 / GIF 数（启动日志用）。</summary>
        public int HistoryImages { get; private set; }
        public int HistoryGifs { get; private set; }

        /// <summary>一行统计，写进启动日志。</summary>
        public string Summary => $"jobs={_jobs.Count} threads={_workers} doneByCaller={_stolen} decode={_decodeMs}ms";

        /// <summary>挑出要载入的条目并立即开始后台解码。可在任意线程调用；条目在 <see cref="Complete"/> 之前不会被改动。</summary>
        public static MediaPreload Start(PersistenceService persistence, PersistedData data, bool includeHistory)
        {
            var preload = new MediaPreload(persistence);
            foreach (var item in data.PinnedHistory.Concat(data.Favorites))
            {
                if (item.Sticker != null && !string.IsNullOrEmpty(item.ImageBlobName))
                    preload._jobs.Add(new Job(item, item.ImageBlobName, null, false));
                else if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
                    preload._jobs.Add(new Job(item, item.ImageBlobName, null, false)); // 只载入缩略图
                else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
                    preload._jobs.Add(new Job(item, null, item.GifBlobName, true));
            }
            int favoriteJobs = preload._jobs.Count;
            if (includeHistory)
            {
                foreach (var item in data.History)
                {
                    if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
                    {
                        preload._jobs.Add(new Job(item, item.ImageBlobName, null, false)); // 只载入缩略图，避免启动时把上百张大图全分辨率读进内存
                        preload.HistoryImages++;
                    }
                    else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
                    {
                        preload._jobs.Add(new Job(item, null, item.GifBlobName, false));
                        preload.HistoryGifs++;
                    }
                }
            }
            preload._queue = [.. preload._jobs.Skip(favoriteJobs), .. preload._jobs.Take(favoriteJobs)];
            preload.StartWorkers();
            return preload;
        }

        private void StartWorkers()
        {
            _clock.Start();
            if (_queue.Length < 2) return; // 不值得开线程，Complete 在调用线程上载
            _workers = Math.Min(MaxWorkers, _queue.Length);
            _running = _workers;
            _workersDone.Reset();
            for (int i = 0; i < _workers; i++)
            {
                try
                {
                    var thread = new Thread(Work) { IsBackground = true, Name = "startup-media" };
#if !AVALONIA
                    thread.SetApartmentState(ApartmentState.STA);
#endif
                    thread.Start();
                }
                catch
                {
                    WorkerExited(); // 线程开不起来：剩下的活由 Complete 在调用线程上做
                }
            }
        }

        private void Work()
        {
            try
            {
                for (int i; (i = Interlocked.Increment(ref _next)) < _queue.Length;) Load(_queue[i]);
            }
            finally
            {
                WorkerExited();
#if !AVALONIA
                // 建过 BitmapImage 的线程会有自己的 Dispatcher 和一个隐藏消息窗口；结果都已冻结，用完就关掉。
                try { System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); } catch { }
#endif
            }
        }

        private void WorkerExited()
        {
            if (Interlocked.Decrement(ref _running) != 0) return;
            _decodeMs = _clock.ElapsedMilliseconds;
            _workersDone.Set();
        }

        private void Load(Job job)
        {
            try
            {
                // 两个方法都自带兜底，失败返回 null；结果已冻结，可以交给别的线程。
                if (job.Thumb != null) job.Image = _persistence.LoadImageThumbnail(job.Thumb);
                else job.Bytes = _persistence.LoadGifBlob(job.Gif!);
                job.Loaded = true;
            }
            catch { } // 万一抛出，Complete 会在调用线程上再载一次
        }

        /// <summary>
        /// 在调用线程上：把还没被后台线程领走的条目自己载完，等后台线程收尾，再按原顺序赋回。
        /// <paramref name="wait"/> 决定怎么等（Windows 的 UI 线程要用不泵消息的等待）。只能调用一次。
        /// </summary>
        public void Complete(Action<WaitHandle>? wait = null)
        {
            for (int i; (i = Interlocked.Increment(ref _next)) < _queue.Length; _stolen++) Load(_queue[i]);
            if (wait != null) wait(_workersDone); else _workersDone.WaitOne();
            if (_decodeMs < 0) _decodeMs = _clock.ElapsedMilliseconds;
            foreach (var job in _jobs)
            {
                if (!job.Loaded) Load(job);
                if (job.Thumb != null)
                {
                    job.Item.Image = job.Image;
                }
                else
                {
                    job.Item.GifBytes = job.Bytes;
                    if (job.FirstFrame) { try { job.Item.Image = job.Item.GifSource; } catch { } }
                }
            }
            _workersDone.Dispose();
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
        // 包括原件和修改版；原件仍被其他收藏引用时不可清理。
        foreach (var name in BlobNames(item).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsBlobUsed(name!))
                _persistence.DeleteImageBlob(name!);
        }
    }

    // 按调用时的集合逐字段比较；删除文件不改动集合，不必先复制一份，也不为每个条目分配数组。
    private bool IsBlobUsed(string name)
    {
        for (int i = 0; i < PinnedHistory.Count; i++)
            if (UsesBlob(PinnedHistory[i], name)) return true;
        for (int f = 0; f < Folders.Count; f++)
        {
            var items = Folders[f].Items;
            for (int i = 0; i < items.Count; i++)
                if (UsesBlob(items[i], name)) return true;
        }
        if (_history != null)
        {
            var history = _history.Items;
            for (int i = 0; i < history.Count; i++)
                if (UsesBlob(history[i], name)) return true;
        }
        return false;
    }

    private static bool UsesBlob(ClipItem item, string name)
        => SameBlob(item.ImageBlobName, name) || SameBlob(item.GifBlobName, name) || SameBlob(item.RichBlobName, name)
           || (item.Sticker is { } sticker && (SameBlob(sticker.OriginalBlobName, name) || SameBlob(sticker.WorkingBlobName, name)));

    private static bool SameBlob(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string?[] BlobNames(ClipItem item)
        => [item.ImageBlobName, item.GifBlobName, item.RichBlobName, item.Sticker?.OriginalBlobName, item.Sticker?.WorkingBlobName];

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
        RichBlobName = src.RichBlobName,
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
