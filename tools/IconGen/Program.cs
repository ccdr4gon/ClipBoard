using System.Buffers.Binary;
using SkiaSharp;
using Svg.Skia;

// Renders assets/icons/*.svg into the icon files the apps ship:
//   dotnet run --project tools/IconGen [-- --preview <dir>]
var root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "ClipBoard.sln"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("ClipBoard.sln");
string Source(string name) => Path.Combine(root, "assets", "icons", name);

// Windows: one .ico for the exe, the taskbar and the tray. Small sizes come from the pixel-aligned variant.
var ico = new List<(int Size, SKBitmap Image)>();
foreach (int size in new[] { 16, 20, 24, 32 }) ico.Add((size, Render(Source("app-small.svg"), size)));
foreach (int size in new[] { 40, 48, 64, 256 }) ico.Add((size, Render(Source("app.svg"), size)));
WriteIco(Path.Combine(root, "src", "ClipBoard", "Assets", "tray.ico"), ico);

// macOS: the bundle icon, plus the menu bar template image (black + alpha, macOS tints it).
var icns = new (string Type, int Size)[] { ("icp4", 16), ("icp5", 32), ("ic11", 32), ("icp6", 64), ("ic12", 64), ("ic07", 128),
    ("ic08", 256), ("ic13", 256), ("ic09", 512), ("ic14", 512), ("ic10", 1024) };
var mac = Directory.CreateDirectory(Path.Combine(root, "src", "ClipBoard.Mac", "Assets")).FullName;
WriteIcns(Path.Combine(mac, "ClipBoard.icns"), icns.Select(e => (e.Type, Render(Source("app-macos.svg"), e.Size))).ToList());
File.WriteAllBytes(Path.Combine(mac, "menubar.png"), Png(Render(Source("menubar.svg"), 72)));
File.WriteAllBytes(Source("app-256.png"), Png(Render(Source("app-macos.svg"), 256))); // README

if (args.Length == 2 && args[0] == "--preview") WritePreview(args[1]);
Console.WriteLine("Icons written.");

SKBitmap Render(string svgPath, int size)
{
    using var svg = new SKSvg();
    var picture = svg.Load(svgPath) ?? throw new InvalidDataException(svgPath);
    var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / picture.CullRect.Width, size / picture.CullRect.Height);
    canvas.DrawPicture(picture);
    return bitmap;
}

static byte[] Png(SKBitmap bitmap)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// 32-bit DIB entries (BGRA, bottom-up, with an AND mask) for small sizes, PNG for 256: readable by WPF, WinForms and the shell.
static void WriteIco(string path, List<(int Size, SKBitmap Image)> images)
{
    var frames = images.Select(i => i.Size >= 256 ? Png(i.Image) : Dib(i.Image)).ToList();
    using var file = new BinaryWriter(File.Create(path));
    file.Write((ushort)0); file.Write((ushort)1); file.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    for (int i = 0; i < images.Count; i++)
    {
        byte dimension = (byte)(images[i].Size >= 256 ? 0 : images[i].Size);
        file.Write(dimension); file.Write(dimension); file.Write((byte)0); file.Write((byte)0);
        file.Write((ushort)1); file.Write((ushort)32); file.Write(frames[i].Length); file.Write(offset);
        offset += frames[i].Length;
    }
    foreach (var frame in frames) file.Write(frame);
}

static byte[] Dib(SKBitmap bitmap)
{
    int size = bitmap.Width, maskStride = (size + 31) / 32 * 4;
    var pixels = new byte[size * size * 4];
    var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul);
    unsafe { fixed (byte* p = pixels) bitmap.PeekPixels().ReadPixels(info, (nint)p, size * 4); }
    using var stream = new MemoryStream();
    using var dib = new BinaryWriter(stream);
    dib.Write(40); dib.Write(size); dib.Write(size * 2); dib.Write((ushort)1); dib.Write((ushort)32);
    dib.Write(0); dib.Write(size * size * 4 + maskStride * size); dib.Write(0); dib.Write(0); dib.Write(0); dib.Write(0);
    for (int y = size - 1; y >= 0; y--) dib.Write(pixels, y * size * 4, size * 4);
    for (int y = size - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (int x = 0; x < size; x++) if (pixels[(y * size + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
        dib.Write(row);
    }
    return stream.ToArray();
}

static void WriteIcns(string path, List<(string Type, SKBitmap Image)> images)
{
    var chunks = images.Select(i => (i.Type, Data: Png(i.Image))).ToList();
    using var file = new BinaryWriter(File.Create(path));
    void BigEndian(int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); file.Write(b); }
    file.Write("icns"u8); BigEndian(8 + chunks.Sum(c => 8 + c.Data.Length));
    foreach (var (type, data) in chunks) { file.Write(System.Text.Encoding.ASCII.GetBytes(type)); BigEndian(8 + data.Length); file.Write(data); }
}

// A contact sheet for checking the artwork: every shipped size, and the menu bar glyph tinted as macOS does.
void WritePreview(string dir)
{
    Directory.CreateDirectory(dir);
    using var sheet = new SKBitmap(1180, 560);
    using var canvas = new SKCanvas(sheet);
    canvas.Clear(new SKColor(0xF5, 0xF1, 0xE7));
    Draw(canvas, Render(Source("app-macos.svg"), 256), 20, 20);
    int x = 300;
    foreach (var size in new[] { 128, 64, 32, 16 }) { Draw(canvas, Render(Source("app-macos.svg"), size), x, 20); x += size + 20; }
    x = 20;
    foreach (var (size, image) in ico) { Draw(canvas, image, x, 300); x += size + 24; }
    using var bar = new SKPaint { Color = new SKColor(0x1E, 0x1E, 0x20) };
    canvas.DrawRect(20, 470, 560, 70, bar);
    canvas.DrawRect(600, 470, 560, 70, new SKPaint { Color = new SKColor(0xEC, 0xEC, 0xEC) });
    var glyph = Render(Source("menubar.svg"), 36);
    using var white = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn) };
    using var black = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Black, SKBlendMode.SrcIn) };
    Draw(canvas, glyph, 40, 487, white);
    Draw(canvas, Render(Source("menubar.svg"), 18), 100, 496, white);
    Draw(canvas, glyph, 620, 487, black);
    Draw(canvas, Render(Source("menubar.svg"), 18), 680, 496, black);
    File.WriteAllBytes(Path.Combine(dir, "icons-preview.png"), Png(sheet));
    File.WriteAllBytes(Path.Combine(dir, "windows-16.png"), Png(Upscale(ico[0].Image, 8)));
}

static void Draw(SKCanvas canvas, SKBitmap bitmap, float x, float y, SKPaint? paint = null)
{
    using var image = SKImage.FromBitmap(bitmap);
    canvas.DrawImage(image, x, y, new SKSamplingOptions(SKFilterMode.Linear), paint);
}

static SKBitmap Upscale(SKBitmap source, int factor)
{
    var result = new SKBitmap(source.Width * factor, source.Height * factor);
    using var canvas = new SKCanvas(result);
    canvas.DrawImage(SKImage.FromBitmap(source), new SKRect(0, 0, result.Width, result.Height), new SKSamplingOptions(SKFilterMode.Nearest));
    return result;
}
