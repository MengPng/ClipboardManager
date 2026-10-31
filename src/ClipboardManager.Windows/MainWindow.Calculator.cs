using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipboardManager.Windows;

public partial class MainWindow
{
    private const int MaxTapeEntries = 30;
    private const string CalculatorHint = "Enter 记录并继续 · Ctrl+Enter 复制结果";

    private ClipboardCalculatorMode _calculatorMode = ClipboardCalculatorMode.Hint;
    private bool _calculatorModeLoaded;
    private bool _suppressCalculatorEvents;
    // True right after Enter: the input holds the committed result, fully selected. An operator continues
    // the calculation from it; a digit replaces it. Cleared by any edit.
    private bool _chainPending;
    private int _recallIndex = -1;
    private string _recallDraft = string.Empty;
    public ObservableCollection<CalculationResult> Calculations { get; } = [];

    private async Task LoadCalculatorSettingsAsync()
    {
        var settings = await _settingsStore.LoadAsync();
        _calculatorMode = settings.ClipboardCalculatorMode;
        _calculatorModeLoaded = true;
        _suppressCalculatorEvents = true;
        try
        {
            foreach (ComboBoxItem item in ClipboardCalculatorModeBox.Items)
                if (item.Tag is string tag && tag == _calculatorMode.ToString()) ClipboardCalculatorModeBox.SelectedItem = item;
            CalculatorKeypadToggle.IsChecked = settings.CalculatorKeypadOpen;
            CalculatorKeypad.Visibility = settings.CalculatorKeypadOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _suppressCalculatorEvents = false; }
        UpdateCalculatorStatus();
        UpdateLiveResult();
    }

    private async void ClipboardCalculatorModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCalculatorEvents) return;
        if (ClipboardCalculatorModeBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<ClipboardCalculatorMode>(tag, out var mode) || mode == _calculatorMode) return;
        await RunActionAsync("保存剪切板算式模式", async () =>
        {
            var settings = await _settingsStore.LoadAsync();
            await _settingsStore.SaveAsync(settings with { ClipboardCalculatorMode = mode });
            _calculatorMode = mode;
            _calculatorModeLoaded = true;
            // History rows decide at creation whether they show the "= result" hint.
            await RefreshHistoryAsync();
        });
    }

    // Checked/Unchecked rather than Click: assistive technology toggles the button without raising Click.
    private async void CalculatorKeypadToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCalculatorEvents) return;
        var open = CalculatorKeypadToggle.IsChecked == true;
        CalculatorKeypad.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        await RunActionAsync("保存小键盘状态", async () =>
        {
            var settings = await _settingsStore.LoadAsync();
            await _settingsStore.SaveAsync(settings with { CalculatorKeypadOpen = open });
        });
    }

    private void UpdateCalculatorStatus()
    {
        if (!IsCalculatorTabActive) return;
        CountText.Text = CalculatorHint;
        CalculatorTapeCountText.Text = Calculations.Count == 0 ? string.Empty : $"最近 {Calculations.Count} 条";
        CalculationEmptyText.Visibility = Calculations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- live result ----

    private void CalculatorExpressionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressCalculatorEvents) { _chainPending = false; _recallIndex = -1; }
        UpdateLiveResult();
    }

    /// <param name="emphasize">Set on Enter only: a failure is then shown in the warning colour.</param>
    private void UpdateLiveResult(bool emphasize = false)
    {
        if (CalculatorResultRun is null) return;
        var text = CalculatorExpressionBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowResult("输入算式，例如 (1250+380)/3", FontSizeKey.Caption, FontWeights.Normal, "LabelTertiaryBrush", null, null);
            CopyCalculatorValueButton.IsEnabled = false;
            CopyCalculatorUppercaseButton.IsEnabled = false;
            return;
        }
        var outcome = CalculatorService.Evaluate(text);
        switch (outcome.Status)
        {
            case CalculationStatus.Success:
                var result = outcome.Result!;
                ShowResult("= " + result.DisplayText, FontSizeKey.Title, FontWeights.SemiBold, "LabelPrimaryBrush",
                    result.ExpandedExpression is { } expanded ? "  " + expanded : null,
                    result.IsApproximate ? "完整值 " + result.FullValueText : null);
                break;
            case CalculationStatus.Incomplete:
                ShowResult(emphasize ? outcome.Message : "= …", FontSizeKey.Title, FontWeights.Normal,
                    emphasize ? "WarningBrush" : "LabelTertiaryBrush", null, null);
                break;
            default:
                ShowResult(outcome.Message, FontSizeKey.Caption, FontWeights.Normal,
                    emphasize ? "WarningBrush" : "LabelSecondaryBrush", null, null);
                break;
        }
        CopyCalculatorValueButton.IsEnabled = outcome.IsSuccess;
        CopyCalculatorUppercaseButton.IsEnabled = outcome.IsSuccess;
    }

    private enum FontSizeKey { Title, Caption }

    private void ShowResult(string text, FontSizeKey size, FontWeight weight, string brushKey, string? expanded, string? toolTip)
    {
        CalculatorResultRun.Text = text;
        CalculatorResultRun.FontWeight = weight;
        CalculatorResultRun.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty,
            size == FontSizeKey.Title ? "FontSizeTitle" : "FontSizeCaption");
        CalculatorResultRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brushKey);
        CalculatorExpandedRun.Text = expanded ?? string.Empty;
        CalculatorResultText.ToolTip = toolTip;
        System.Windows.Automation.AutomationProperties.SetName(CalculatorResultText, "计算结果：" + text + (expanded ?? string.Empty));
    }

    // ---- committing ----

    /// <summary>
    /// Enter: record on the tape and put the result back in the input (selected). Ctrl+Enter / 复制结果:
    /// record and copy the plain number without touching the input.
    /// </summary>
    private async Task CommitCalculationAsync(bool copy, bool uppercase = false)
    {
        var text = CalculatorExpressionBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var outcome = CalculatorService.Evaluate(text);
        if (!outcome.IsSuccess) { UpdateLiveResult(emphasize: true); return; }
        var result = outcome.Result!;
        if (uppercase)
        {
            if (!ChineseAmountFormatter.TryFormat(result.Value, out _, out _))
            {
                await ShowCalculatorNoticeAsync("超过 9999 万亿，无法转大写");
                return;
            }
            AddCalculation(result);
            await CopyUppercaseAmountAsync(result);
            return;
        }
        AddCalculation(result);
        if (copy)
        {
            await CopyCalculationTextAsync(result.ResultText);
            return;
        }
        SetCalculatorText(result.ResultText);
        CalculatorExpressionBox.SelectAll();
        _chainPending = true;
    }

    private void SetCalculatorText(string text)
    {
        _suppressCalculatorEvents = true;
        try { CalculatorExpressionBox.Text = text; CalculatorExpressionBox.CaretIndex = text.Length; }
        finally { _suppressCalculatorEvents = false; }
        UpdateLiveResult();
    }

    private void AddCalculation(CalculationResult result)
    {
        var existing = Calculations.FirstOrDefault(item => item.Output == result.Output);
        if (existing is not null) Calculations.Remove(existing);
        Calculations.Add(result);
        while (Calculations.Count > MaxTapeEntries) Calculations.RemoveAt(0);
        UpdateCalculatorStatus();
        CalculatorTapeList.ScrollIntoView(result);
    }

    private async void CopyCalculatorValueButton_Click(object sender, RoutedEventArgs e) => await CommitCalculationAsync(copy: true);
    private async void CopyCalculatorUppercaseButton_Click(object sender, RoutedEventArgs e) => await CommitCalculationAsync(copy: true, uppercase: true);

    /// <summary>Copies the 报销单 capital-letter amount (to 分). More than two decimals are rounded and the footer says so.</summary>
    private Task CopyUppercaseAmountAsync(CalculationResult result) => RunActionAsync("复制大写金额", async () =>
    {
        if (!ChineseAmountFormatter.TryFormat(result.Value, out var text, out var rounded))
        {
            await ShowCalculatorNoticeAsync("超过 9999 万亿，无法转大写");
            return;
        }
        await _restoreService.CopyTextAsync(text);
        CopyCalculatorUppercaseButton.Content = "已复制";
        var notice = ShowCalculatorNoticeAsync(rounded ? "已复制大写（已四舍五入到分）" : "已复制大写金额");
        await Task.Delay(1200);
        CopyCalculatorUppercaseButton.Content = "大写";
        await notice;
    });

    private int _calculatorNoticeVersion;

    private async Task ShowCalculatorNoticeAsync(string message)
    {
        var version = ++_calculatorNoticeVersion;
        if (!IsCalculatorTabActive) return;
        CountText.Text = message;
        await Task.Delay(3500);
        if (version == _calculatorNoticeVersion) UpdateCalculatorStatus();
    }

    private Task CopyCalculationTextAsync(string text) => RunActionAsync("复制计算内容", async () =>
    {
        await _restoreService.CopyTextAsync(text);
        CopyCalculatorValueButton.Content = "已复制";
        await Task.Delay(1200);
        CopyCalculatorValueButton.Content = "复制结果";
    });

    // ---- keyboard ----

    private async void CalculatorExpressionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await CommitCalculationAsync(copy: control, uppercase: control && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                RecallCalculation(1);
                break;
            case Key.Down when Keyboard.Modifiers == ModifierKeys.None && _recallIndex >= 0:
                e.Handled = true;
                RecallCalculation(-1);
                break;
        }
    }

    // After Enter the result is selected: an operator must continue from it instead of replacing it.
    private void CalculatorExpressionBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        PrepareChainedInput(e.Text);
    }

    private void PrepareChainedInput(string incoming)
    {
        var box = CalculatorExpressionBox;
        if (!_chainPending || box.Text.Length == 0 || box.SelectionLength != box.Text.Length) return;
        if (incoming.Length == 1 && "+-−*×xX/÷^%".Contains(incoming[0]))
        {
            box.SelectionLength = 0;
            box.CaretIndex = box.Text.Length;
        }
        _chainPending = false;
    }

    /// <summary>↑ steps back through the tape (newest first); ↓ steps forward and finally restores the draft.</summary>
    private void RecallCalculation(int direction)
    {
        if (Calculations.Count == 0) return;
        var next = Math.Clamp(_recallIndex + direction, -1, Calculations.Count - 1);
        if (next == _recallIndex) return;
        if (_recallIndex < 0) _recallDraft = CalculatorExpressionBox.Text;
        _recallIndex = next;
        SetCalculatorText(next < 0 ? _recallDraft : Calculations[Calculations.Count - 1 - next].Expression);
        _chainPending = false;
    }

    private void ClearCalculatorInput()
    {
        _chainPending = false;
        _recallIndex = -1;
        CalculatorExpressionBox.Clear();
        CalculatorExpressionBox.Focus();
    }

    private void CalculatorKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var key = button.Tag as string ?? button.Content as string ?? string.Empty;
        var box = CalculatorExpressionBox;
        switch (key)
        {
            case "enter": _ = CommitCalculationAsync(copy: false); break;
            case "clear": ClearCalculatorInput(); break;
            case "back": Backspace(); break;
            case "()": InsertCalculatorText("()", caretFromEnd: 1); break;
            default: InsertCalculatorText(key, caretFromEnd: 0); break;
        }
        box.Focus();
    }

    private void Backspace()
    {
        var box = CalculatorExpressionBox;
        _chainPending = false;
        if (box.SelectionLength > 0) { box.SelectedText = string.Empty; return; }
        var caret = box.CaretIndex;
        if (caret == 0) return;
        box.Text = box.Text.Remove(caret - 1, 1);
        box.CaretIndex = caret - 1;
    }

    private void InsertCalculatorText(string text, int caretFromEnd)
    {
        var box = CalculatorExpressionBox;
        PrepareChainedInput(text);
        if (box.Text.Length - box.SelectionLength + text.Length > CalculatorService.MaxExpressionLength) return;
        var start = box.SelectionStart;
        box.SelectedText = text;
        box.CaretIndex = start + text.Length - caretFromEnd;
        box.SelectionLength = 0;
    }

    // ---- tape ----

    private void ReuseCalculation(CalculationResult result)
    {
        _chainPending = false;
        _recallIndex = -1;
        SetCalculatorText(result.Expression);
        CalculatorExpressionBox.Focus();
    }

    private void CalculatorTape_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // The row's own action buttons handle their clicks; anything else on the row reuses it.
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
            if (node is ListBoxItem { DataContext: CalculationResult result }) { ReuseCalculation(result); return; }
        }
    }

    private void CalculatorTape_KeyDown(object sender, KeyEventArgs e)
    {
        if (CalculatorTapeList.SelectedItem is not CalculationResult result) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; ReuseCalculation(result); }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; RemoveCalculation(result); }
    }

    private void CalculatorTape_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = FindItem<CalculationResult>(e.OriginalSource as DependencyObject);
        if (hit is not null) CalculatorTapeList.SelectedItem = hit;
        if (CalculatorTapeList.SelectedItem is not CalculationResult result) { e.Handled = true; return; }
        var menu = new ContextMenu();
        AddMenu(menu, "复制结果", () => CopyCalculationTextAsync(result.ResultText));
        AddMenu(menu, "复制大写金额", () => CopyUppercaseAmountAsync(result));
        AddMenu(menu, "复制算式与结果", () => CopyCalculationTextAsync(result.Output));
        AddMenu(menu, "复用算式", () => { ReuseCalculation(result); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        AddMenu(menu, "删除这条", () => { RemoveCalculation(result); return Task.CompletedTask; }, "Delete");
        AddMenu(menu, "清空纸带", () => { Calculations.Clear(); UpdateCalculatorStatus(); return Task.CompletedTask; });
        CalculatorTapeList.ContextMenu = menu;
    }

    private void ReuseCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalculationResult result }) ReuseCalculation(result);
    }
    private async void CopyRecentCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalculationResult result }) await CopyCalculationTextAsync(result.ResultText);
    }
    private void DeleteCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalculationResult result }) RemoveCalculation(result);
    }

    private void RemoveCalculation(CalculationResult result)
    {
        Calculations.Remove(result);
        UpdateCalculatorStatus();
    }

    // ---- history rows (Hint mode) ----

    /// <summary>Copies the plain result of the selected history row; the row's own text stays untouched.</summary>
    private async Task CopyCalculationResultAsync()
    {
        if (SelectedItem?.Calculation is not { } calculation) return;
        await _restoreService.CopyTextAsync(calculation.ResultText);
        HistoryCountText.Text = "已复制计算结果";
        await Task.Delay(1500);
        HistoryCountText.Text = $"{Items.Count} 条";
    }
}
