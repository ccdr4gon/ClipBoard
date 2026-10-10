using System.IO;
using System.Security.Cryptography;
using ClipBoard.Models;

namespace ClipBoard.Services;

public sealed record StickerPublishEntry(ClipItem Item, int Revision, string[] Emojis, PreparedSticker? Media);
public sealed record StickerPublishPlan(FavoriteFolder Folder, string SetName, string Title, long OwnerId, TelegramBot Bot,
    IReadOnlyList<StickerPublishEntry> Entries, int Added, int Replaced, int Deleted, bool NewSet);

public sealed class TelegramStickerPublisher(TelegramStickerClient client, StickerMediaService media, Action save)
{
    public async Task<StickerPublishPlan> PrepareAsync(FavoriteFolder folder, long ownerId, string requestedName,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (ownerId <= 0) throw new InvalidOperationException("请先在连接设置中填写贴纸包所有者的 Telegram 用户 ID。");
        if (folder.Items.Count is < 1 or > 120) throw new InvalidOperationException("普通贴纸包需要 1～120 张素材。");
        if (string.IsNullOrWhiteSpace(folder.Name) || folder.Name.Length > 64) throw new InvalidOperationException("包标题需要 1～64 个字符。");
        var bot = await client.GetMeAsync(ct);
        var binding = folder.Telegram;
        if (binding?.SetName != null && (binding.BotId != bot.Id || binding.OwnerUserId != ownerId))
            throw new InvalidOperationException("当前包绑定了另一个机器人或所有者；请切换回原来的连接设置。");
        string name = binding?.SetName ?? (string.IsNullOrWhiteSpace(requestedName)
            ? $"clipboard_{folder.Id.ToString("N")[..12]}_by_{bot.Username}" : TelegramStickerClient.ParseSetName(requestedName));
        TelegramStickerClient.ParseSetName(name);
        if (!name.EndsWith("_by_" + bot.Username, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"发布名称必须以 _by_{bot.Username} 结尾。");
        var remote = await FindSetAsync(name, ct);
        if (remote != null && binding?.SetName == null)
            throw new InvalidOperationException("这个包名称已被使用，请换一个名称。本程序不会接管未知的远端包。");
        if (remote == null && binding?.SetName != null && folder.Items.Any(i => i.Sticker?.PublishedFileId != null))
            throw new InvalidOperationException("之前发布的远端包已不存在，请新建一个本地包重新发布。");

        var entries = new List<StickerPublishEntry>();
        var hashes = new HashSet<string>();
        int added = 0, replaced = 0;
        foreach (var item in folder.Items.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"准备发布：{item.TitleOrUntitled}");
            await media.EnsureAssetAsync(item, ct);
            var asset = item.Sticker!;
            if (asset.Emojis.Length is < 1 or > 20) throw new InvalidOperationException($"{item.TitleOrUntitled} 需要 1～20 个关联 emoji。");
            bool changed = asset.PublishedFileId == null || asset.PublishedRevision != asset.Revision || asset.PendingFileId != null;
            PreparedSticker? prepared = changed ? await media.PrepareTelegramAsync(item, ct) : null;
            if (prepared != null)
            {
                using var stream = File.OpenRead(prepared.Path);
                if (!hashes.Add(Convert.ToHexString(SHA256.HashData(stream))))
                    throw new InvalidOperationException("当前包有完全相同的素材；Telegram 不允许同一个文件重复加入同一包，请先移除重复项。");
            }
            if (asset.PublishedFileId == null) added++;
            else if (changed) replaced++;
            entries.Add(new(item, asset.Revision, asset.Emojis.ToArray(), prepared));
        }
        save();
        return new(folder, name, folder.Name, ownerId, bot, entries, added, replaced,
            binding?.DeletedFileIds.Distinct().Count() ?? 0, remote == null);
    }

    public async Task PublishAsync(StickerPublishPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        if (!plan.Folder.Items.Select(i => i.Id).SequenceEqual(plan.Entries.Select(e => e.Item.Id))
            || plan.Entries.Any(e => e.Item.Sticker!.Revision != e.Revision) || plan.Folder.Name != plan.Title)
            throw new InvalidOperationException("本地内容已变化，请重新预览发布清单。");
        var binding = plan.Folder.Telegram ??= new TelegramPackBinding();
        binding.SetName = plan.SetName;
        binding.BotId = plan.Bot.Id;
        binding.OwnerUserId = plan.OwnerId;
        save();
        var remote = await FindSetAsync(plan.SetName, ct);
        ResolvePendingMutation(plan.Folder, remote);

        // 先核对上次超时的请求，再上传新修改；不把未知结果当成失败后重复添加。
        if (remote != null)
        {
            foreach (var entry in plan.Entries)
            {
                var asset = entry.Item.Sticker!;
                var pending = remote.Stickers.FirstOrDefault(s => s.FileUniqueId == asset.PendingUniqueId);
                if (pending != null && asset.PendingRevision != entry.Revision)
                {
                    asset.PublishedFileId = pending.FileId;
                    asset.PublishedUniqueId = pending.FileUniqueId;
                    asset.PublishedRevision = asset.PendingRevision;
                    asset.PendingFileId = asset.PendingUniqueId = null;
                    // 卡片提示里的「已同步 / 待更新」靠通知刷新；以前每次切标签都会重建卡片，碰巧掩盖了这里漏掉的通知。
                    entry.Item.NotifyMediaChanged();
                    save();
                }
                if (asset.PublishedFileId != null
                    && !remote.Stickers.Any(s => s.FileUniqueId == asset.PublishedUniqueId)
                    && pending == null)
                    throw new InvalidOperationException("远端贴纸被其他客户端删除或替换，请先核对该包，避免覆盖外部修改。");
            }
        }
        foreach (var entry in plan.Entries.Where(e => e.Media != null))
        {
            var asset = entry.Item.Sticker!;
            if (asset.PendingFileId != null && asset.PendingRevision == entry.Revision) continue;
            progress?.Report($"上传素材：{entry.Item.TitleOrUntitled}");
            var uploaded = await client.UploadAsync(plan.OwnerId, entry.Media!.Path, entry.Media.Format, ct);
            asset.PendingFileId = uploaded.FileId;
            asset.PendingUniqueId = uploaded.FileUniqueId;
            asset.PendingRevision = entry.Revision;
            save();
        }
        var expected = plan.Entries.Select(e => e.Item.Sticker!.PendingUniqueId ?? e.Item.Sticker.PublishedUniqueId).ToArray();
        if (expected.Distinct().Count() != expected.Length)
            throw new InvalidOperationException("Telegram 识别到重复素材，请先删除本地重复项再发布。");
        if (remote == null)
        {
            progress?.Report("正在创建你的 Telegram 贴纸包…");
            RecordMutation(plan.Folder, "create", plan.Entries.Take(50).ToArray(), null);
            await client.CreateSetAsync(plan.OwnerId, plan.SetName, plan.Title,
                plan.Entries.Take(50).Select(Input), ct);
            remote = await client.GetSetAsync(plan.SetName, ct);
            ResolvePendingMutation(plan.Folder, remote, acknowledged: true);
        }
        expected = plan.Entries.Select(e => e.Item.Sticker!.PendingUniqueId ?? e.Item.Sticker.PublishedUniqueId).ToArray();
        var knownIds = plan.Entries.SelectMany(e => new[] { e.Item.Sticker!.PublishedFileId, e.Item.Sticker.PendingFileId })
            .Concat(binding.DeletedFileIds).Where(id => id != null).ToHashSet();
        if (remote.Stickers.Any(s => !knownIds.Contains(s.FileId) && !expected.Contains(s.FileUniqueId)))
            throw new InvalidOperationException("远端包出现了本地未知的贴纸，请先核对外部修改，再继续同步。");

        // 先执行明确确认过的删除，满 120 张的包才能继续添加；相同素材重新导入时取消删除。
        foreach (var id in binding.DeletedFileIds.Distinct().ToArray())
        {
            var deleting = remote.Stickers.FirstOrDefault(s => s.FileId == id);
            if (deleting != null && !expected.Contains(deleting.FileUniqueId))
            {
                await client.DeleteAsync(id, ct);
                remote = await client.GetSetAsync(plan.SetName, ct);
            }
            binding.DeletedFileIds.RemoveAll(value => value == id);
            save();
        }

        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var asset = entry.Item.Sticker!;
            if (entry.Media == null) continue;
            progress?.Report($"更新贴纸：{entry.Item.TitleOrUntitled}");
            var match = remote.Stickers.FirstOrDefault(s => s.FileUniqueId == asset.PendingUniqueId);
            if (match == null)
            {
                var old = remote.Stickers.FirstOrDefault(s => s.FileUniqueId == asset.PublishedUniqueId);
                RecordMutation(plan.Folder, old == null ? "add" : "replace", [entry], remote, old?.FileUniqueId);
                if (old == null) await client.AddAsync(plan.OwnerId, plan.SetName, Input(entry), ct);
                else await client.ReplaceAsync(plan.OwnerId, plan.SetName, old.FileId, Input(entry), ct);
                remote = await client.GetSetAsync(plan.SetName, ct);
                ResolvePendingMutation(plan.Folder, remote, acknowledged: true);
                match = remote.Stickers.FirstOrDefault(s => s.FileUniqueId == asset.PendingUniqueId)
                    ?? throw new IOException("Telegram 尚未返回刚更新的贴纸，请重试核对。");
            }
            await client.SetEmojisAsync(match.FileId, entry.Emojis, ct);
            asset.PublishedFileId = match.FileId;
            asset.PublishedUniqueId = match.FileUniqueId;
            asset.PublishedRevision = entry.Revision;
            asset.PendingFileId = asset.PendingUniqueId = null;
            asset.PendingRevision = 0;
            entry.Item.NotifyMediaChanged();
            save();
        }
        for (int i = 0; i < plan.Entries.Count; i++)
        {
            string id = plan.Entries[i].Item.Sticker!.PublishedFileId!;
            if (i >= remote.Stickers.Length || remote.Stickers[i].FileId != id)
            {
                await client.SetPositionAsync(id, i, ct);
                remote = await client.GetSetAsync(plan.SetName, ct);
            }
        }
        if (remote.Title != plan.Title) await client.SetTitleAsync(plan.SetName, plan.Title, ct);
        binding.PublishedTitle = plan.Title;
        binding.PublishedOrder = plan.Entries.Select(e => e.Item.Id).ToList();
        save();
        progress?.Report("发布完成：https://t.me/addstickers/" + plan.SetName);
    }

    private static object Input(StickerPublishEntry entry)
    {
        var asset = entry.Item.Sticker!;
        return TelegramStickerClient.Input(asset.PendingFileId ?? asset.PublishedFileId!,
            entry.Media?.Format ?? (asset.Format == StickerFormat.Tgs ? "animated" : StickerMediaService.IsAnimated(asset.Format) ? "video" : "static"), entry.Emojis);
    }

    private void RecordMutation(FavoriteFolder folder, string kind, StickerPublishEntry[] entries,
        TelegramStickerSet? remote, string? oldUnique = null)
    {
        folder.Telegram!.PendingMutation = new TelegramPackMutation
        {
            Kind = kind, ItemIds = entries.Select(e => e.Item.Id).ToArray(),
            Formats = entries.Select(e => e.Media!.Format).ToArray(),
            BeforeUniqueIds = remote?.Stickers.Select(s => s.FileUniqueId).ToArray() ?? [], OldUniqueId = oldUnique,
        };
        save();
    }

    private void ResolvePendingMutation(FavoriteFolder folder, TelegramStickerSet? remote, bool acknowledged = false)
    {
        var binding = folder.Telegram!;
        var mutation = binding.PendingMutation;
        if (mutation == null) return;
        if (remote == null)
        {
            if (mutation.Kind != "create") throw new InvalidOperationException("同步中的远端包已消失，请先核对。");
            binding.PendingMutation = null; save(); return;
        }
        TelegramSticker[] matches;
        if (mutation.Kind == "create")
        {
            // 新包的初始数组按提交顺序保存。服务端会将上传文件转换成新的贴纸 ID。
            if (remote.Stickers.Length != mutation.ItemIds.Length)
                throw new InvalidOperationException("新包回读数量与创建清单不同，请先核对远端内容。");
            matches = remote.Stickers;
        }
        else
        {
            var introduced = remote.Stickers.Where(s => !mutation.BeforeUniqueIds.Contains(s.FileUniqueId)).ToArray();
            bool unchanged = remote.Stickers.Select(s => s.FileUniqueId).SequenceEqual(mutation.BeforeUniqueIds);
            if (unchanged)
            {
                if (acknowledged && mutation.Kind == "replace")
                    matches = [remote.Stickers.Single(s => s.FileUniqueId == mutation.OldUniqueId)];
                else { binding.PendingMutation = null; save(); return; }
            }
            else
            {
                var retained = mutation.BeforeUniqueIds.Where(id => mutation.Kind != "replace" || id != mutation.OldUniqueId).ToArray();
                if (introduced.Length != 1 || retained.Any(id => !remote.Stickers.Any(s => s.FileUniqueId == id))
                    || remote.Stickers.Length != retained.Length + 1)
                    throw new InvalidOperationException("远端变化与上次提交不同，请先核对，避免重复添加或替换错误的贴纸。");
                matches = introduced;
            }
        }
        for (int i = 0; i < mutation.ItemIds.Length; i++)
        {
            var sticker = matches[i];
            string format = sticker.IsVideo ? "video" : sticker.IsAnimated ? "animated" : "static";
            if (format != mutation.Formats[i]) throw new InvalidOperationException("远端贴纸格式与提交清单不同，请先核对。");
            var item = folder.Items.FirstOrDefault(item => item.Id == mutation.ItemIds[i]);
            if (item?.Sticker == null) binding.DeletedFileIds.Add(sticker.FileId);
            else
            {
                item.Sticker.PendingFileId = sticker.FileId;
                item.Sticker.PendingUniqueId = sticker.FileUniqueId;
            }
        }
        binding.PendingMutation = null;
        save();
    }
    private async Task<TelegramStickerSet?> FindSetAsync(string name, CancellationToken ct)
    {
        try { return await client.GetSetAsync(name, ct); }
        catch (TelegramApiException ex) when (ex.IsSetMissing) { return null; }
    }
}
