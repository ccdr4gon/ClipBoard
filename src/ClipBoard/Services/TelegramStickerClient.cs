using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipBoard.Models;

namespace ClipBoard.Services;

public sealed record TelegramBot(long Id, string Username);
public sealed class TelegramFile
{
    public string FileId { get; set; } = "";
    public string FileUniqueId { get; set; } = "";
    public string? FilePath { get; set; }
}
public sealed class TelegramSticker
{
    public string FileId { get; set; } = "";
    public string FileUniqueId { get; set; } = "";
    public string? Emoji { get; set; }
    public bool IsAnimated { get; set; }
    public bool IsVideo { get; set; }
    public TelegramFile? Thumbnail { get; set; }
    public StickerFormat Format => IsVideo ? StickerFormat.Webm : IsAnimated ? StickerFormat.Tgs : StickerFormat.Webp;
}
public sealed class TelegramStickerSet
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string StickerType { get; set; } = "regular";
    public TelegramSticker[] Stickers { get; set; } = [];
}
public sealed class TelegramApiException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public bool IsSetMissing => Code == 400 && Message.Contains("STICKERSET_INVALID", StringComparison.OrdinalIgnoreCase);
}

/// <summary>仅封装本地贴纸管理所需的接口；不记录含机器人凭据的 URL。</summary>
public sealed class TelegramStickerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _token;
    private readonly Uri _baseUri;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public TelegramStickerClient(string token, HttpClient? client = null, Uri? baseUri = null)
    {
        if (!Regex.IsMatch(token, @"^\d+:[A-Za-z0-9_-]{10,}$"))
            throw new ArgumentException("机器人 Token 格式不正确，请从 @BotFather 获取。", nameof(token));
        _token = token;
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _ownsClient = client == null;
        _baseUri = baseUri ?? new Uri("https://api.telegram.org/");
    }

    public static string ParseSetName(string input)
    {
        string name = input.Trim();
        if (Uri.TryCreate(name, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == "tg" && uri.Host == "addstickers")
                name = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                    .Where(p => p.Length == 2 && p[0] == "set").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault() ?? "";
            else if (uri.Scheme == "https" && uri.Host is "t.me" or "www.t.me" or "telegram.me")
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/');
                name = parts.Length == 2 && parts[0] == "addstickers" ? parts[1] : "";
            }
            else name = "";
        }
        if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]{0,63}$") || name.Contains("__"))
            throw new ArgumentException("请输入 t.me/addstickers/… 贴纸包链接或有效的包名称。");
        return name;
    }

    public Task<TelegramBot> GetMeAsync(CancellationToken ct) => CallAsync<TelegramBot>("getMe", new { }, ct);
    public Task<TelegramStickerSet> GetSetAsync(string name, CancellationToken ct)
        => CallAsync<TelegramStickerSet>("getStickerSet", new { name = ParseSetName(name) }, ct);

    public async Task<IReadOnlyList<(long Id, string Name)>> FindOwnersAsync(CancellationToken ct)
    {
        var updates = await CallAsync<JsonElement>("getUpdates", new { limit = 100, timeout = 0 }, ct);
        var owners = new Dictionary<long, string>();
        foreach (var update in updates.EnumerateArray())
        {
            if (!update.TryGetProperty("message", out var message)
                || !message.TryGetProperty("chat", out var chat) || chat.GetProperty("type").GetString() != "private"
                || !message.TryGetProperty("text", out var text) || !text.GetString()!.StartsWith("/start", StringComparison.Ordinal)
                || !message.TryGetProperty("from", out var from)) continue;
            long id = from.GetProperty("id").GetInt64();
            owners[id] = from.TryGetProperty("first_name", out var first) ? first.GetString() ?? id.ToString() : id.ToString();
        }
        return owners.Select(p => (p.Key, p.Value)).ToArray();
    }

    public async Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
    {
        var file = await CallAsync<TelegramFile>("getFile", new { file_id = fileId }, ct);
        if (string.IsNullOrEmpty(file.FilePath)) throw new IOException("Telegram 未返回贴纸文件路径。");
        try
        {
            using var response = await _http.GetAsync(new Uri(_baseUri, $"file/bot{_token}/{file.FilePath}"), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > 20 * 1024 * 1024) throw new IOException("贴纸文件超过 20 MB，已停止下载。");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            return output.ToArray();
        }
        catch (HttpRequestException) { throw new IOException("下载贴纸失败，请检查网络连接后重试。"); }
    }

    public async Task<TelegramFile> UploadAsync(long owner, string path, string format, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(owner.ToString(System.Globalization.CultureInfo.InvariantCulture)), "user_id");
        form.Add(new StringContent(format), "sticker_format");
        var file = new StreamContent(File.OpenRead(path));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "sticker", Path.GetFileName(path));
        return await SendAsync<TelegramFile>("uploadStickerFile", form, ct);
    }

    public Task<bool> CreateSetAsync(long owner, string name, string title, IEnumerable<object> stickers, CancellationToken ct)
        => CallAsync<bool>("createNewStickerSet", new { user_id = owner, name, title, stickers, sticker_type = "regular" }, ct);
    public Task<bool> AddAsync(long owner, string name, object sticker, CancellationToken ct)
        => CallAsync<bool>("addStickerToSet", new { user_id = owner, name, sticker }, ct);
    public Task<bool> ReplaceAsync(long owner, string name, string oldFile, object sticker, CancellationToken ct)
        => CallAsync<bool>("replaceStickerInSet", new { user_id = owner, name, old_sticker = oldFile, sticker }, ct);
    public Task<bool> DeleteAsync(string fileId, CancellationToken ct)
        => CallAsync<bool>("deleteStickerFromSet", new { sticker = fileId }, ct);
    public Task<bool> SetPositionAsync(string fileId, int position, CancellationToken ct)
        => CallAsync<bool>("setStickerPositionInSet", new { sticker = fileId, position }, ct);
    public Task<bool> SetTitleAsync(string name, string title, CancellationToken ct)
        => CallAsync<bool>("setStickerSetTitle", new { name, title }, ct);
    public Task<bool> SetEmojisAsync(string fileId, string[] emojis, CancellationToken ct)
        => CallAsync<bool>("setStickerEmojiList", new { sticker = fileId, emoji_list = emojis }, ct);
    public static object Input(string fileId, string format, string[] emojis)
        => new { sticker = fileId, format, emoji_list = emojis };

    private async Task<T> CallAsync<T>(string method, object payload, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
        return await SendAsync<T>(method, content, ct);
    }

    private async Task<T> SendAsync<T>(string method, HttpContent content, CancellationToken ct)
    {
        try
        {
            // Token 含冒号，前缀 ./ 避免 Uri 把 bot123:… 误识别为 URI scheme。
            using var response = await _http.PostAsync(new Uri(_baseUri, $"./bot{_token}/{method}"), content, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                int code = root.TryGetProperty("error_code", out var number) ? number.GetInt32() : (int)response.StatusCode;
                string description = root.TryGetProperty("description", out var error) ? error.GetString() ?? "请求失败" : "请求失败";
                throw new TelegramApiException(code, $"Telegram {method}：{description.Replace(_token, "[已隐藏]")}");
            }
            return root.GetProperty("result").Deserialize<T>(Json)!;
        }
        catch (HttpRequestException) { throw new IOException("Telegram 连接失败。请检查网络；如果刚执行过发布，请重试以核对远端结果。"); }
        catch (JsonException) { throw new IOException("Telegram 返回了无法识别的响应，请稍后重试。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new IOException("Telegram 请求超时。发布结果可能已生效，下次重试会先核对远端。"); }
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
