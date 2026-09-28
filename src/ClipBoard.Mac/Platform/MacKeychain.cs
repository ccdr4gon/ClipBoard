using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClipBoard.Services;

// Generic-password entries are local to the user's login keychain. Never put a token in process arguments.
internal static class MacKeychain
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("ClipBoard.Telegram");
    private static byte[] Account(string root) => Encoding.UTF8.GetBytes(Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)))));

    public static string Read(string root)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("钥匙串仅在 macOS 可用。");
        var account = Account(root);
        int status = Find(0, (uint)Service.Length, Service, (uint)account.Length, account, out var length, out var data, out var item);
        if (status == -25300) throw new IOException("钥匙串中没有机器人 token，请重新保存 Telegram 连接。");
        Check(status);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { Free(0, data); if (item != 0) MacNative.CFRelease(item); }
    }

    public static void Write(string root, string token)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("钥匙串仅在 macOS 可用。");
        var account = Account(root);
        int status = Find(0, (uint)Service.Length, Service, (uint)account.Length, account, out _, out var data, out var item);
        if (status == 0) Free(0, data);
        else if (status != -25300) Check(status);
        try
        {
            if (token.Length == 0) { if (item != 0) Check(Delete(item)); return; }
            var bytes = Encoding.UTF8.GetBytes(token);
            try
            {
                Check(item == 0 ? Add(0, (uint)Service.Length, Service, (uint)account.Length, account, (uint)bytes.Length, bytes, out item)
                    : Modify(item, 0, (uint)bytes.Length, bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (item != 0) MacNative.CFRelease(item); }
    }

    private static void Check(int status) { if (status != 0) throw new IOException($"钥匙串访问失败（{status}），请检查系统授权后重试。"); }
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")] private static extern int Find(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint length, out nint data, out nint item);
    [DllImport(Security, EntryPoint = "SecKeychainAddGenericPassword")] private static extern int Add(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint length, byte[] data, out nint item);
    [DllImport(Security, EntryPoint = "SecKeychainItemModifyAttributesAndData")] private static extern int Modify(nint item, nint attributes, uint length, byte[] data);
    [DllImport(Security, EntryPoint = "SecKeychainItemFreeContent")] private static extern int Free(nint attributes, nint data);
    [DllImport(Security, EntryPoint = "SecKeychainItemDelete")] private static extern int Delete(nint item);
}
