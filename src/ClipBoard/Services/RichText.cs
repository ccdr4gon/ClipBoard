using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClipBoard.Models;

namespace ClipBoard.Services;

/// <summary>
/// 文本条目的格式：读取和还原 HTML / RTF，并在网页复制的纯文本丢了列表编号时从 HTML 重建纯文本。
/// Windows 和 Mac 共用；HTML 统一按普通 HTML 保存，Windows 读写时再处理 CF_HTML 头部。
/// </summary>
public static partial class RichText
{
    // 单个格式的上限；超大的网页或带图片的 RTF 只保留纯文本，避免历史目录膨胀。
    public const int MaxFormatLength = 4 * 1024 * 1024;

    private const string FragmentStart = "<!--StartFragment-->";
    private const string FragmentEnd = "<!--EndFragment-->";
    // 正则都在编译期生成（模式和选项与原来逐字相同），不再在运行时解析、用解释器匹配；每次匹配仍限时 2 秒。
    private const int TimeoutMs = 2000;

    public static RichContent? Create(string? html, string? rtf)
    {
        if (html is not { Length: > 0 and <= MaxFormatLength } || html.IndexOf('<') < 0) html = null;
        if (rtf is not { Length: > 0 and <= MaxFormatLength } || !rtf.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal)) rtf = null;
        return html == null && rtf == null ? null : new RichContent(html, rtf);
    }

    /// <summary>Windows 的 HTML Format 带偏移量头部；只取出 HTML 本身。</summary>
    public static string? FromCfHtml(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        int first = data.IndexOf('<');
        if (first < 0) return null;
        // 偏移量按 UTF-8 字节计算；来源程序写错时退回到第一个标签。
        string header = data[..first];
        int start = HeaderValue(header, StartHtmlHeader()), end = HeaderValue(header, EndHtmlHeader());
        if (start >= 0 && end > start)
        {
            int total = Encoding.UTF8.GetByteCount(data);
            if (end <= total)
            {
                // 常见情况：StartHTML 就是第一个标签，EndHTML 落在字符边界上，中间没有落单的代理项。
                // 这时直接截取字符串，结果与按字节截取再解码完全相同，省去把整段 HTML（可达数 MB）编码成字节再解码。
                int stop;
                if (start == Encoding.UTF8.GetByteCount(data.AsSpan(0, first))
                    && (stop = CharIndexFromEnd(data, total - end)) > first && IsWellFormed(data.AsSpan(first, stop - first)))
                    return data[first..stop];
                var bytes = Encoding.UTF8.GetBytes(data);
                if (bytes[start] == (byte)'<') return Encoding.UTF8.GetString(bytes, start, end - start);
            }
        }
        return data[first..].TrimEnd('\0');
    }

    // 从末尾往前数 tailBytes 个 UTF-8 字节对应的字符位置；落在一个字符的字节中间时返回 -1。
    // 落单的代理项按 U+FFFD 计 3 字节，与 Encoding.UTF8 编码时一致。
    private static int CharIndexFromEnd(string text, int tailBytes)
    {
        int index = text.Length;
        while (tailBytes > 0 && index > 0)
        {
            Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out Rune rune, out int used);
            tailBytes -= rune.Utf8SequenceLength;
            index -= used;
        }
        return tailBytes == 0 ? index : -1;
    }

    // 成对的代理项能原样往返 UTF-8；落单的会被编码成 U+FFFD，只能按字节截取。
    private static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        for (int i; (i = text.IndexOfAnyInRange('\uD800', '\uDFFF')) >= 0; text = text[(i + 2)..])
            if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
        return true;
    }

    /// <summary>生成 Windows 的 HTML Format：补上片段标记和按 UTF-8 字节计算的偏移量。</summary>
    public static string ToCfHtml(string html)
    {
        string document = html;
        if (IndexOf(document, FragmentStart) < 0 || IndexOf(document, FragmentEnd) < 0)
        {
            var body = BodyTag().Match(document);
            int close = document.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
            int inner = body.Index + body.Length;
            document = body.Success && close >= inner
                ? document[..inner] + FragmentStart + document[inner..close] + FragmentEnd + document[close..]
                : "<html><body>" + FragmentStart + document + FragmentEnd + "</body></html>";
        }
        const string Header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        int headerLength = string.Format(CultureInfo.InvariantCulture, Header, 0, 0, 0, 0).Length;
        int fragmentStart = headerLength + Encoding.UTF8.GetByteCount(document.AsSpan(0, IndexOf(document, FragmentStart) + FragmentStart.Length));
        int fragmentEnd = headerLength + Encoding.UTF8.GetByteCount(document.AsSpan(0, IndexOf(document, FragmentEnd)));
        int end = headerLength + Encoding.UTF8.GetByteCount(document);
        return string.Format(CultureInfo.InvariantCulture, Header, headerLength, end, fragmentStart, fragmentEnd) + document;
    }

    /// <summary>macOS 按字节读取 public.html；没有声明编码时中文会被当成 Latin-1。</summary>
    public static string WithCharset(string html)
        => html.Contains("charset", StringComparison.OrdinalIgnoreCase) ? html : "<meta charset=\"utf-8\">" + html;

    [GeneratedRegex(@"<li[\s>/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex ListItem();
    [GeneratedRegex(@"^[ \t]*(?:\d{1,3}[.)]|[-*+•·◦▪])[ \t]+\S", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex ListMarker();
    [GeneratedRegex(@"<body\b[^>]*>", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex BodyTag();
    [GeneratedRegex(@"(?:^|\s)checked\b", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex CheckedAttribute();
    [GeneratedRegex(@"\n{3,}", RegexOptions.None, TimeoutMs)]
    private static partial Regex ExtraNewlines();
    // 以下三组原来是运行时拼出的模式（键名 + 固定后缀），拆成每个键一个，模式字符串逐字相同。
    [GeneratedRegex(@"StartHTML:\s*(-?\d+)", RegexOptions.None, TimeoutMs)]
    private static partial Regex StartHtmlHeader();
    [GeneratedRegex(@"EndHTML:\s*(-?\d+)", RegexOptions.None, TimeoutMs)]
    private static partial Regex EndHtmlHeader();
    [GeneratedRegex(@"(?:^|\s)start\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex StartAttribute();
    [GeneratedRegex(@"(?:^|\s)value\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex ValueAttribute();
    [GeneratedRegex(@"(?:^|\s)type\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex TypeAttribute();

    /// <summary>
    /// 浏览器和 Electron 应用（Claude、ChatGPT 等）复制列表时，纯文本里没有编号和项目符号，只有 HTML 里有。
    /// 这时从 HTML 重建纯文本；原纯文本已经带编号（例如复制的 Markdown 原文）就保持不变。
    /// </summary>
    public static string PlainText(string text, string? html)
    {
        try
        {
            if (string.IsNullOrEmpty(html) || !ListItem().IsMatch(html) || ListMarker().IsMatch(text)) return text;
            string rebuilt = HtmlToText(html);
            // 去掉补上的编号后，内容应与原文基本一致；差得多说明 HTML 与纯文本不是同一段内容，保留原文。
            int original = VisibleLength(text), content = VisibleLength(ListMarker().Replace(rebuilt, m => m.Value[^1..]));
            if (rebuilt.Length == 0 || content < original * 0.9 || content > original * 1.1 + 10) return text;
            return text.Contains("\r\n", StringComparison.Ordinal) ? rebuilt.Replace("\n", "\r\n") : rebuilt;
        }
        catch (RegexMatchTimeoutException) { return text; }
    }

    private static int VisibleLength(string text)
    {
        int count = 0;
        foreach (char c in text) if (!char.IsWhiteSpace(c)) count++;
        return count;
    }

    [GeneratedRegex(
        @"<!--.*?-->|<![^>]*>|<\?.*?>|<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Token();
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "script", "style", "head", "title", "template", "noscript", "svg" };
    private static readonly HashSet<string> Paragraphs = new(StringComparer.Ordinal) { "p", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "table", "hr", "figure" };
    private static readonly HashSet<string> Lines = new(StringComparer.Ordinal)
        { "div", "section", "article", "header", "footer", "main", "aside", "nav", "address", "dl", "dt", "dd", "figcaption", "details", "summary", "form", "fieldset", "caption", "center" };

    /// <summary>把 HTML 转成保留段落、列表编号和项目符号的纯文本（类似 Word 的“只保留文本”）。</summary>
    public static string HtmlToText(string html)
    {
        int start = IndexOf(html, FragmentStart), stop = IndexOf(html, FragmentEnd);
        if (start >= 0 && stop > start) html = html[(start + FragmentStart.Length)..stop];
        var writer = new Writer();
        var lists = new Stack<ListLevel>();
        string? skip = null;
        int skipDepth = 0, pre = 0, cell = 0, position = 0;
        bool preStart = false;
        foreach (Match match in Token().Matches(html))
        {
            if (skip == null && match.Index > position) Emit(html[position..match.Index]);
            position = match.Index + match.Length;
            if (!match.Groups[2].Success) continue; // 注释、DOCTYPE
            string name = match.Groups[2].Value.ToLowerInvariant();
            bool closing = match.Groups[1].Length > 0;
            string attributes = match.Groups[3].Value;
            bool selfClosing = attributes.TrimEnd().EndsWith('/');
            if (skip != null)
            {
                if (name == skip && !selfClosing) skipDepth += closing ? -1 : 1;
                if (skipDepth == 0) skip = null;
                continue;
            }
            if (!closing && !selfClosing && Skipped.Contains(name)) { skip = name; skipDepth = 1; continue; }
            switch (name)
            {
                case "br": writer.LineBreak(); break;
                case "ul" or "ol" when !closing:
                    writer.Block(lists.Count == 0 ? 2 : 1);
                    lists.Push(new ListLevel(name == "ol", Number(attributes, StartAttribute()) ?? 1, writer.Indent));
                    break;
                case "ul" or "ol":
                    if (lists.Count == 0) break;
                    writer.Indent = lists.Pop().Indent;
                    writer.Marker = null;
                    writer.Block(lists.Count == 0 ? 2 : 1);
                    break;
                case "li" when !closing:
                    writer.Block(1);
                    var level = lists.Count > 0 ? lists.Peek() : null;
                    string marker = "- ";
                    if (level?.Ordered == true)
                    {
                        int number = Number(attributes, ValueAttribute()) ?? level.Next;
                        level.Next = number + 1;
                        marker = number.ToString(CultureInfo.InvariantCulture) + ". ";
                    }
                    string indent = level?.Indent ?? "";
                    writer.Marker = indent + marker;
                    writer.Indent = indent + new string(' ', marker.Length);
                    break;
                case "li":
                    writer.Block(1);
                    writer.Indent = lists.Count > 0 ? lists.Peek().Indent : "";
                    break;
                case "pre":
                    writer.Block(lists.Count > 0 ? 1 : 2);
                    if (closing) pre = Math.Max(0, pre - 1);
                    else { pre++; preStart = true; }
                    break;
                case "tr": writer.Block(1); cell = 0; break;
                case "td" or "th" when !closing:
                    if (cell++ > 0) writer.Raw("\t");
                    break;
                case "input" when string.Equals(Attribute(attributes, TypeAttribute()), "checkbox", StringComparison.OrdinalIgnoreCase):
                    writer.Text(CheckedAttribute().IsMatch(attributes) ? "[x] " : "[ ] ", false);
                    break;
                default:
                    if (Paragraphs.Contains(name)) writer.Block(lists.Count > 0 ? 1 : 2);
                    else if (Lines.Contains(name)) writer.Block(1);
                    break;
            }
        }
        if (skip == null && position < html.Length) Emit(html[position..]);
        return writer.ToString();

        void Emit(string raw)
        {
            string text = WebUtility.HtmlDecode(raw);
            if (pre > 0 && preStart && text.Length > 0)
            {
                // HTML 规定忽略紧跟 <pre> 的第一个换行。
                if (text.StartsWith("\r\n", StringComparison.Ordinal)) text = text[2..];
                else if (text[0] == '\n') text = text[1..];
                preStart = false;
            }
            writer.Text(text, pre > 0);
        }
    }

    private static int HeaderValue(string header, Regex key)
    {
        var match = key.Match(header);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;
    }

    private static string? Attribute(string attributes, Regex name)
    {
        var match = name.Match(attributes);
        if (!match.Success) return null;
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
    }

    private static int? Number(string attributes, Regex name)
        => int.TryParse(Attribute(attributes, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static int IndexOf(string text, string value) => text.IndexOf(value, StringComparison.OrdinalIgnoreCase);

    private sealed class ListLevel(bool ordered, int next, string indent)
    {
        public bool Ordered { get; } = ordered;
        public int Next { get; set; } = next;
        public string Indent { get; } = indent;
    }

    /// <summary>按块级元素换行、折叠空白，并给列表项加编号和续行缩进。</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _text = new();
        private int _trailingNewlines, _wantNewlines;
        private bool _space;
        public string Indent { get; set; } = "";
        public string? Marker { get; set; }

        public void Block(int newlines) { _wantNewlines = Math.Max(_wantNewlines, newlines); _space = false; }

        public void LineBreak()
        {
            if (_text.Length == 0 && Marker == null) return;
            if (Marker != null) Begin();
            else Flush();
            _text.Append('\n');
            _trailingNewlines++;
            _space = false;
        }

        public void Raw(string value) { Begin(); _text.Append(value); _space = false; }

        public void Text(string value, bool preformatted)
        {
            foreach (char raw in value)
            {
                char c = raw == ' ' ? ' ' : raw;
                if (preformatted)
                {
                    if (c == '\n') LineBreak();
                    else if (c != '\r') { Begin(); _text.Append(c); }
                    continue;
                }
                if (char.IsWhiteSpace(c)) { _space = true; continue; }
                bool fresh = Begin();
                if (_space && !fresh) _text.Append(' ');
                _space = false;
                _text.Append(c);
            }
        }

        private void Flush()
        {
            if (_text.Length > 0)
                for (; _trailingNewlines < _wantNewlines; _trailingNewlines++) _text.Append('\n');
            _wantNewlines = 0;
        }

        /// <summary>输出内容前补足换行；在行首写入编号或缩进。返回是否刚开始新的一行。</summary>
        private bool Begin()
        {
            Flush();
            if (_text.Length > 0 && _trailingNewlines == 0) return false;
            _text.Append(Marker ?? Indent);
            Marker = null;
            _trailingNewlines = 0;
            return true;
        }

        public override string ToString()
        {
            string text = string.Join("\n", _text.ToString().Split('\n').Select(line => line.TrimEnd()));
            return ExtraNewlines().Replace(text, "\n\n").Trim('\n');
        }
    }
}
