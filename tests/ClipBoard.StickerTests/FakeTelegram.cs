using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipBoard.Services;

internal sealed class FakeTelegram : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    internal sealed record Media(string Id, string Unique, string Format, byte[] Bytes);
    internal sealed record Entry(Media File, string[] Emojis);
    internal sealed class Set(string name, string title)
    {
        public string Name = name, Title = title;
        public List<Entry> Entries = [];
    }
    public readonly Dictionary<string, Set> Sets = [];
    private readonly Dictionary<string, Media> _files = [];
    public int Creates, Adds, Replaces, Deletes, Positions;
    public long LastOwner;
    public bool TimeoutAfterReplace;
    public bool TimeoutAfterCreate;
    public const string Token = "123456:fake_token_for_local_tests_only";
    private int _remoteId;
    private Media AsSticker(Media uploaded)
    {
        // Telegram 建包/添加时会重新分配贴纸标识，不能把 uploadStickerFile 的 ID 当成最终 ID。
        string unique = "sticker_" + ++_remoteId;
        var sticker = uploaded with { Id = "file_" + unique, Unique = unique };
        _files[sticker.Id] = sticker;
        return sticker;
    }

    public Media Register(byte[] bytes, string format)
    {
        string unique = Convert.ToHexString(SHA256.HashData(bytes))[..24];
        var file = new Media("file_" + unique, unique, format, bytes);
        _files[file.Id] = file;
        return file;
    }
    public void Seed(string name, params (byte[] Bytes, string Format)[] files)
    {
        var set = new Set(name, "测试混合贴纸包");
        foreach (var file in files) set.Entries.Add(new(Register(file.Bytes, file.Format), ["🙂"]));
        Sets[name] = set;
    }
    private static TelegramStickerSet View(Set set) => new()
    {
        Name = set.Name, Title = set.Title,
        Stickers = set.Entries.Select(e => new TelegramSticker
        {
            FileId = e.File.Id, FileUniqueId = e.File.Unique, Emoji = e.Emojis[0],
            IsAnimated = e.File.Format == "animated", IsVideo = e.File.Format == "video",
        }).ToArray(),
    };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.RequestUri?.Scheme != "https") throw new InvalidOperationException("Telegram request must use HTTPS");
        string method = request.RequestUri!.Segments.Last().Trim('/');
        if (request.Method == HttpMethod.Get)
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(_files[method].Bytes) };
        if (method == "uploadStickerFile")
        {
            var form = (MultipartFormDataContent)request.Content!;
            string Name(HttpContent c) => c.Headers.ContentDisposition!.Name!.Trim('"');
            var data = await form.First(c => Name(c) == "sticker").ReadAsByteArrayAsync(ct);
            var format = await form.First(c => Name(c) == "sticker_format").ReadAsStringAsync(ct);
            var file = Register(data, format);
            return Ok(new TelegramFile { FileId = file.Id, FileUniqueId = file.Unique });
        }
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var body = document.RootElement;
        string Get(string key) => body.GetProperty(key).GetString()!;
        Entry Input(JsonElement value) => new(AsSticker(_files[value.GetProperty("sticker").GetString()!]),
            value.GetProperty("emoji_list").EnumerateArray().Select(e => e.GetString()!).ToArray());
        switch (method)
        {
            case "getMe": return Ok(new TelegramBot(42, "test_stickers_bot"));
            case "getUpdates": return Ok(Array.Empty<object>());
            case "getFile":
                var file = _files[Get("file_id")];
                return Ok(new TelegramFile { FileId = file.Id, FileUniqueId = file.Unique, FilePath = "files/" + file.Id });
            case "getStickerSet":
                return Sets.TryGetValue(Get("name"), out var found) ? Ok(View(found)) : Error("Bad Request: STICKERSET_INVALID");
            case "createNewStickerSet":
                if (Sets.ContainsKey(Get("name"))) return Error("STICKERSET_NAME_OCCUPIED");
                var created = new Set(Get("name"), Get("title"));
                var initial = body.GetProperty("stickers").EnumerateArray().Select(Input).ToList();
                if (initial.Count is < 1 or > 50) return Error("STICKERS_TOO_MUCH");
                created.Entries.AddRange(initial); Sets[created.Name] = created; Creates++;
                LastOwner = body.GetProperty("user_id").GetInt64();
                if (TimeoutAfterCreate) { TimeoutAfterCreate = false; throw new HttpRequestException("simulated connection loss after create"); }
                return Ok(true);
            case "addStickerToSet":
                var addedSet = Sets[Get("name")];
                var addition = Input(body.GetProperty("sticker"));
                if (addedSet.Entries.Any(e => e.File.Unique == addition.File.Unique)) return Ok(true);
                if (addedSet.Entries.Count >= 120) return Error("STICKERS_TOO_MUCH");
                addedSet.Entries.Add(addition); Adds++; return Ok(true);
            case "replaceStickerInSet":
                var replacedSet = Sets[Get("name")];
                int index = replacedSet.Entries.FindIndex(e => e.File.Id == Get("old_sticker"));
                if (index < 0) return Error("STICKER_INVALID");
                replacedSet.Entries[index] = Input(body.GetProperty("sticker")); Replaces++;
                if (TimeoutAfterReplace) { TimeoutAfterReplace = false; throw new HttpRequestException("simulated connection loss after replace"); }
                return Ok(true);
            case "deleteStickerFromSet":
                foreach (var set in Sets.Values) set.Entries.RemoveAll(e => e.File.Id == Get("sticker"));
                Deletes++; return Ok(true);
            case "setStickerPositionInSet":
                var positionSet = Sets.Values.First(s => s.Entries.Any(e => e.File.Id == Get("sticker")));
                var moving = positionSet.Entries.First(e => e.File.Id == Get("sticker"));
                positionSet.Entries.Remove(moving); positionSet.Entries.Insert(body.GetProperty("position").GetInt32(), moving);
                Positions++; return Ok(true);
            case "setStickerEmojiList":
                foreach (var set in Sets.Values)
                {
                    int i = set.Entries.FindIndex(e => e.File.Id == Get("sticker"));
                    if (i >= 0) set.Entries[i] = set.Entries[i] with { Emojis = body.GetProperty("emoji_list").EnumerateArray().Select(e => e.GetString()!).ToArray() };
                }
                return Ok(true);
            case "setStickerSetTitle": Sets[Get("name")].Title = Get("title"); return Ok(true);
            default: throw new InvalidOperationException("Unexpected method: " + method);
        }
    }
    private static HttpResponseMessage Ok(object result) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result }, Json), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Error(string description) => new(HttpStatusCode.BadRequest)
    { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = 400, description }), Encoding.UTF8, "application/json") };
}
