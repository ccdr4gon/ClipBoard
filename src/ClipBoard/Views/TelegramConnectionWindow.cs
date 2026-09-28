using System.Windows;
using System.Windows.Controls;
using ClipBoard.Services;

namespace ClipBoard.Views;

public sealed class TelegramConnectionWindow : Window
{
    public TelegramConnection Connection { get; private set; }
    public TelegramConnectionWindow(TelegramConnectionStore store, TelegramConnection connection)
    {
        Connection = connection;
        Title = "Telegram 连接与媒体组件"; Width = 570; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = StickerUi.Paper;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(StickerUi.Label("使用你自己的机器人创建和维护贴纸包。Token 仅在本机加密保存，不会写入日志。"));
        root.Children.Add(StickerUi.Label("机器人 Token（也可从项目根目录 .env 读取）"));
        var token = new PasswordBox { Password = connection.Token, Padding = new Thickness(7) };
        root.Children.Add(token);
        var status = StickerUi.Label("");
        var owners = new ComboBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 5, 0, 0) };
        var ownerId = StickerUi.Input(connection.OwnerUserId == 0 ? "" : connection.OwnerUserId.ToString());
        var verify = StickerUi.Button("验证连接并查找 /start 账号", async (sender, _) =>
        {
            var button = (Button)sender;
            button.IsEnabled = false;
            try
            {
                using var client = new TelegramStickerClient(token.Password.Trim());
                var bot = await client.GetMeAsync(CancellationToken.None);
                var users = await client.FindOwnersAsync(CancellationToken.None);
                status.Text = users.Count == 0 ? $"已连接 @{bot.Username}。请先在 Telegram 给这个机器人发送 /start，再点一次验证。" : $"已连接 @{bot.Username}，请选择下面的贴纸包所有者。";
                owners.ItemsSource = users.Select(user => new OwnerChoice(user.Id, $"{user.Name} · {user.Id}")).ToArray();
                if (users.Count == 1) owners.SelectedIndex = 0;
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { button.IsEnabled = true; }
        });
        root.Children.Add(verify); root.Children.Add(status);
        root.Children.Add(StickerUi.Label("贴纸包所有者（在机器人聊天中发送 /start 后可选择）"));
        root.Children.Add(owners);
        root.Children.Add(ownerId);
        owners.SelectionChanged += (_, _) => { if (owners.SelectedItem is OwnerChoice selected) ownerId.Text = selected.Id.ToString(); };
        root.Children.Add(StickerUi.Label("FFmpeg 组件目录（留空使用程序随附的 Tools 目录）"));
        var tools = StickerUi.Input(connection.MediaToolsDirectory);
        root.Children.Add(tools);
        root.Children.Add(StickerUi.Button("选择目录…", (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog(this) == true) tools.Text = dialog.FolderName;
        }));
        root.Children.Add(StickerUi.Button("从 .env 重新读取", (_, _) =>
        {
            var env = TelegramConnectionStore.LoadEnvironment();
            token.Password = env.Token;
            if (env.OwnerUserId > 0) ownerId.Text = env.OwnerUserId.ToString();
            if (env.MediaToolsDirectory.Length > 0) tools.Text = env.MediaToolsDirectory;
            status.Text = env.Token.Length == 0 ? "未找到 Token。" : "已读取本地配置。";
        }));
        root.Children.Add(StickerUi.Button("保存连接", (_, _) =>
        {
            try
            {
                if (!long.TryParse(ownerId.Text, out long user) || user < 0) user = 0;
                var value = new TelegramConnection(token.Password.Trim(), user, tools.Text.Trim());
                if (value.Token.Length > 0) { using var client = new TelegramStickerClient(value.Token); }
                store.Save(value); Connection = value; DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }));
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private sealed record OwnerChoice(long Id, string Label);
}
