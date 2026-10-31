using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

public sealed class ClipboardCalculationService(
    IClipboardTextRewriter clipboard,
    IAppSettingsStore settingsStore,
    SelfWriteGuard selfWriteGuard)
{
    public event EventHandler<CalculationResult>? CalculationCompleted;

    public async Task<bool> TryCalculateAsync(string? text, uint sequence, ClipboardSource source,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        // Only Rewrite touches the clipboard; Hint is display-only (the history row shows the result).
        if (settings.ClipboardCalculatorMode != ClipboardCalculatorMode.Rewrite ||
            !CapturePrivacyPolicy.CanRead(settings, source, DateTimeOffset.UtcNow) ||
            CalculatorService.IsDashSeparatedIdentifier(text) ||
            !CalculatorService.TryCalculate(text, out var calculation, out _, requireBinaryOperator: true)) return false;
        var output = calculation!.Output;
        selfWriteGuard.Expect(TextContentService.ComputeHash(output));
        try
        {
            if (!await clipboard.TryReplaceTextAsync(text!, sequence, output, cancellationToken))
            {
                selfWriteGuard.Clear();
                return false;
            }
        }
        catch { selfWriteGuard.Clear(); throw; }
        CalculationCompleted?.Invoke(this, calculation);
        return true;
    }
}
