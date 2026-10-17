using OmniCore.Host;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void RefreshOpenRouterPicker(FrameView frame, Label feedback,
        IReadOnlyList<ProviderConnectionMenuRow> providers, TextField search,
        Func<ModelPolicyKeyDto?> highlighted, Action<string, ModelPolicyKeyDto?>? reopen = null)
    {
        if (_providerConnectionHost is not ITuiOpenRouterCatalogHost catalog) return;
        var button = frame.SubViews.OfType<Button>().Single(view => view.Id == "omni-palette-openrouter-refresh");
        if (!button.Enabled) return;
        // An explicit provider choice takes precedence over automatic subscription discovery.
        var previous = _catalogCancellation;
        _catalogCancellation = null;
        previous?.Cancel(); previous?.Dispose();
        button.Enabled = false;
        var cancellation = _catalogCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var token = cancellation.Token;
        feedback.Text = _localization.Resolve("openrouter.picker.loading");
        bool IsCurrent() => ReferenceEquals(_overlay, frame) && ReferenceEquals(_catalogCancellation, cancellation);
        _ = Task.Run(async () =>
        {
            string? notice = null;
            try
            {
                foreach (var provider in providers)
                {
                    var result = await catalog.RefreshOpenRouterCatalogAsync(provider.ProviderId, null,
                        0, 100, token).ConfigureAwait(false);
                    if (result.Error is not null || result.Stale)
                        notice = _localization.Resolve("openrouter.catalog.stale");
                }
                _uiActions.Enqueue(() =>
                {
                    if (!IsCurrent()) return;
                    var query = search.Text.ToString();
                    var focused = highlighted();
                    _catalogCancellation = null;
                    CloseOverlay();
                    if (reopen is null) ShowModelPicker(refreshCatalog: false, query: query, highlighted: focused);
                    else reopen(query, focused);
                    if (notice is not null && _overlay?.SubViews.OfType<Label>()
                            .FirstOrDefault(view => view.Id == "omni-palette-feedback") is { } label)
                        label.Text = notice;
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _uiActions.Enqueue(() =>
                {
                    if (!IsCurrent()) return;
                    _catalogCancellation = null;
                    feedback.Text = _localization.Resolve("openrouter.catalog.failed");
                    button.Enabled = true;
                });
            }
            catch (Exception)
            {
                _uiActions.Enqueue(() =>
                {
                    if (!IsCurrent()) return;
                    _catalogCancellation = null;
                    feedback.Text = _localization.Resolve("openrouter.catalog.failed");
                    button.Enabled = true;
                });
            }
            finally { _uiActions.Enqueue(() => cancellation.Dispose()); }
        });
    }
}
