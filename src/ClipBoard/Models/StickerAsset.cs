namespace ClipBoard.Models;

public enum StickerFormat { Png, Webp, Gif, Webm, Tgs }

/// <summary>原件始终保留；WorkingBlobName 指向当前修改版。来源和发布目标分开记录。</summary>
public class StickerAsset
{
    public string OriginalBlobName { get; set; } = "";
    public StickerFormat OriginalFormat { get; set; }
    public string WorkingBlobName { get; set; } = "";
    public StickerFormat Format { get; set; }
    public string[] Emojis { get; set; } = ["🙂"];
    public double DurationSeconds { get; set; }
    public int Revision { get; set; } = 1;
    public string? SourceSetName { get; set; }
    public string? SourceFileId { get; set; }
    public string? SourceUniqueId { get; set; }
    public string? PublishedFileId { get; set; }
    public string? PublishedUniqueId { get; set; }
    public int PublishedRevision { get; set; }

    // 请求超时后，先回读远端查找这次已上传的文件，不能盲目重复添加。
    public string? PendingFileId { get; set; }
    public string? PendingUniqueId { get; set; }
    public int PendingRevision { get; set; }

    public StickerAsset Clone() => new()
    {
        OriginalBlobName = OriginalBlobName, OriginalFormat = OriginalFormat,
        WorkingBlobName = WorkingBlobName, Format = Format, Emojis = Emojis.ToArray(),
        DurationSeconds = DurationSeconds, Revision = Revision,
        SourceSetName = SourceSetName, SourceFileId = SourceFileId, SourceUniqueId = SourceUniqueId,
        PublishedFileId = PublishedFileId, PublishedUniqueId = PublishedUniqueId, PublishedRevision = PublishedRevision,
        PendingFileId = PendingFileId, PendingUniqueId = PendingUniqueId, PendingRevision = PendingRevision,
    };

    public void ClearPublication()
    {
        PublishedFileId = PublishedUniqueId = PendingFileId = PendingUniqueId = null;
        PublishedRevision = PendingRevision = 0;
    }
}

public class TelegramPackBinding
{
    public string? SourceSetName { get; set; }
    public string? SetName { get; set; }
    public long BotId { get; set; }
    public long OwnerUserId { get; set; }
    public string? PublishedTitle { get; set; }
    public List<string> DeletedFileIds { get; set; } = [];
    public List<Guid> PublishedOrder { get; set; } = [];
    public TelegramPackMutation? PendingMutation { get; set; }
    public TelegramPackBinding Clone() => new()
    {
        SourceSetName = SourceSetName, SetName = SetName, BotId = BotId, OwnerUserId = OwnerUserId,
        PublishedTitle = PublishedTitle, DeletedFileIds = DeletedFileIds.ToList(), PublishedOrder = PublishedOrder.ToList(),
        PendingMutation = PendingMutation?.Clone(),
    };
}

public class TelegramPackMutation
{
    public string Kind { get; set; } = "";
    public Guid[] ItemIds { get; set; } = [];
    public string[] Formats { get; set; } = [];
    public string[] BeforeUniqueIds { get; set; } = [];
    public string? OldUniqueId { get; set; }
    public TelegramPackMutation Clone() => new()
    {
        Kind = Kind, ItemIds = ItemIds.ToArray(), Formats = Formats.ToArray(),
        BeforeUniqueIds = BeforeUniqueIds.ToArray(), OldUniqueId = OldUniqueId,
    };
}

public sealed record StickerEditOptions(
    double CropX = 0, double CropY = 0, double CropWidth = 1, double CropHeight = 1,
    string Caption = "", double StartSeconds = 0, double DurationSeconds = 3, double Speed = 1);
