using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipBoard.Services;

public sealed record TelegramConnection(string Token = "", long OwnerUserId = 0, string MediaToolsDirectory = "");

public sealed class TelegramConnectionStore(string rootDirectory)
{
    private sealed record StoredConnection(string ProtectedToken, long OwnerUserId, string MediaToolsDirectory);
    private string FilePath => Path.Combine(rootDirectory, "telegram-connection.json");

    public TelegramConnection Load()
    {
        if (!File.Exists(FilePath)) return LoadEnvironment();
        var stored = JsonSerializer.Deserialize<StoredConnection>(File.ReadAllText(FilePath))
            ?? throw new IOException("Telegram 配置文件无法读取。");
#if AVALONIA
        string token = stored.ProtectedToken.Length == 0 ? "" : stored.ProtectedToken == "keychain:v1"
            ? MacKeychain.Read(rootDirectory) : throw new IOException("请在 Mac 上重新填写机器人 token；Windows 加密配置不能直接解密。");
#else
        string token = stored.ProtectedToken.Length == 0 ? "" : Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedToken), null, DataProtectionScope.CurrentUser));
#endif
        return new(token, stored.OwnerUserId, stored.MediaToolsDirectory);
    }

    public static TelegramConnection LoadEnvironment(string? directory = null)
    {
        var candidates = new List<string> { Path.Combine(directory ?? Environment.CurrentDirectory, ".env") };
        for (var current = new DirectoryInfo(directory ?? AppContext.BaseDirectory); current != null; current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, ".env"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, ".env"));
        string token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
        long owner = 0;
        string tools = "";
        var file = candidates.FirstOrDefault(File.Exists);
        if (file != null)
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var parts = line.Trim().Replace("export ", "").Split('=', 2);
                if (parts.Length != 2 || parts[0].StartsWith('#')) continue;
                string value = parts[1].Trim().Trim('\'', '"');
                switch (parts[0].Trim().ToUpperInvariant())
                {
                    case "BOT_TOKEN": case "TELEGRAM_BOT_TOKEN": token = value; break;
                    case "TELEGRAM_USER_ID": case "OWNER_USER_ID": long.TryParse(value, out owner); break;
                    case "FFMPEG_DIRECTORY": tools = value; break;
                }
            }
        }
        return new(token, owner, tools);
    }

    public void Save(TelegramConnection connection)
    {
        Directory.CreateDirectory(rootDirectory);
#if AVALONIA
        MacKeychain.Write(rootDirectory, connection.Token);
        string encrypted = connection.Token.Length == 0 ? "" : "keychain:v1";
#else
        string encrypted = connection.Token.Length == 0 ? "" : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(connection.Token), null, DataProtectionScope.CurrentUser));
#endif
        var stored = new StoredConnection(encrypted, connection.OwnerUserId, connection.MediaToolsDirectory);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(stored));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }
}
