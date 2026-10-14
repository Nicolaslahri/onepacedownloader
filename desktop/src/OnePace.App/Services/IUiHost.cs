using OnePace.Core;

namespace OnePace.App.Services;

public enum ToastKind { Info, Success, Caution, Danger }

public sealed record BatchRequest(
    string Kind, Arc Arc, IReadOnlyList<Episode> Episodes,
    string Version, string Quality, bool AllowSubs, IReadOnlySet<int> SavedNums);

public sealed record BatchChoice(string Version, string Quality, bool AllowSubs, bool SkipSaved);

/// <summary>What the view model asks of the window: dialogs, pickers, toasts.</summary>
public interface IUiHost
{
    Task<bool> ConfirmAsync(string title, string message, string primary, string close = "Cancel");
    Task AlertAsync(string title, string message);
    Task<BatchChoice?> AskBatchAsync(BatchRequest request);
    Task ShowNoTorrentClientAsync();
    Task<bool> AskUsenetSetupAsync();
    Task ShowDnsAsync();
    string? PickFolder(string initial);
    void Toast(string title, string message, ToastKind kind = ToastKind.Info);
}
