using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace OpenDownloadManager.Services;

/// <summary>Windows toast notifications with Open and Open folder buttons.</summary>
public static class Notifications
{
    private static bool _registered;

    /// <summary>Raised with the toast's arguments when the user clicks it or one of its buttons.</summary>
    public static event Action<IDictionary<string, string>>? Invoked;

    public static void Register()
    {
        if (_registered) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) => Invoked?.Invoke(args.Arguments);
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            // Notifications are a convenience; the app works without them.
        }
    }

    public static void Unregister()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch { }
        _registered = false;
    }

    public static void Completed(ulong id, string name, string folder)
    {
        Show(new AppNotificationBuilder()
            .AddArgument("action", "open").AddArgument("id", id.ToString())
            .AddText("Download complete")
            .AddText(name)
            .AddButton(new AppNotificationButton("Open").AddArgument("action", "open").AddArgument("id", id.ToString()))
            .AddButton(new AppNotificationButton("Open folder").AddArgument("action", "folder").AddArgument("id", id.ToString())));
    }

    public static void Failed(ulong id, string name, string error)
    {
        Show(new AppNotificationBuilder()
            .AddArgument("action", "show").AddArgument("id", id.ToString())
            .AddText("Download failed")
            .AddText(name)
            .AddText(error));
    }

    /// <summary>Offers a copied link: a file to download, or a video to pick a quality for.</summary>
    public static void ClipboardOffer(string url, bool video)
    {
        Show(new AppNotificationBuilder()
            .AddArgument("action", video ? "pick" : "add").AddArgument("url", url)
            .AddText(video ? "Download this video?" : "Download this file?")
            .AddText(url)
            .AddButton(new AppNotificationButton(video ? "Choose quality" : "Download")
                .AddArgument("action", video ? "pick" : "add").AddArgument("url", url)));
    }

    private static void Show(AppNotificationBuilder builder)
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Show(builder.BuildNotification()); } catch { }
    }
}
