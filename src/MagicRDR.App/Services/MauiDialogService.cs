namespace MagicRDR.App.Services;

public sealed class MauiDialogService : IDialogService
{
    public Task ShowMessageAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(async () => await Page.DisplayAlertAsync(title, message, "OK"));

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        MainThread.InvokeOnMainThreadAsync(async () => await Page.DisplayAlertAsync(title, message, accept, cancel));

    private static Page Page =>
        Application.Current?.Windows.FirstOrDefault()?.Page
        ?? throw new InvalidOperationException("No page is available to show a dialog.");
}
