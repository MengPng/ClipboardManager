using ClipboardManager.Application.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipboardManager.Windows;

public partial class MainWindow
{
    private CalculationResult? _currentCalculation;
    private bool _calculatorSettingsBusy;
    public ObservableCollection<CalculationResult> Calculations { get; } = [];

    private async Task LoadCalculatorSettingsAsync()
    {
        ClipboardCalculatorCheckBox.IsChecked = (await _settingsStore.LoadAsync()).EnableClipboardCalculator;
        UpdateCalculatorStatus();
    }

    private async void ClipboardCalculatorCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_calculatorSettingsBusy) return;
        _calculatorSettingsBusy = true;
        ClipboardCalculatorCheckBox.IsEnabled = false;
        try
        {
            await RunActionAsync("保存自动计算设置", async () =>
            {
                var settings = await _settingsStore.LoadAsync();
                await _settingsStore.SaveAsync(settings with { EnableClipboardCalculator = ClipboardCalculatorCheckBox.IsChecked == true });
            });
            await LoadCalculatorSettingsAsync();
        }
        catch
        {
            CalculatorErrorText.Text = "设置未能读取，请稍后重新进入计算器。";
        }
        finally { _calculatorSettingsBusy = false; ClipboardCalculatorCheckBox.IsEnabled = true; }
    }

    private void UpdateCalculatorStatus()
    {
        if (IsCalculatorTabActive)
            CountText.Text = ClipboardCalculatorCheckBox.IsChecked == true ? "自动计算已开启 · 完整算式将改写剪切板" : "自动计算已关闭 · 可手动计算";
    }

    private void CalculateButton_Click(object sender, RoutedEventArgs e) => CalculateExpression();
    private void CalculatorExpressionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CalculateExpression();
    }

    private void CalculatorExpressionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentCalculation = null;
        if (CalculatorResultBox is null) return;
        CalculatorResultBox.Text = "输入算式后按 Enter";
        CalculatorErrorText.Text = string.Empty;
        CopyCalculatorValueButton.IsEnabled = CopyCalculatorEquationButton.IsEnabled = false;
    }

    private void CalculateExpression()
    {
        if (!CalculatorService.TryCalculate(CalculatorExpressionBox.Text, out var result, out var error))
        {
            _currentCalculation = null;
            CalculatorResultBox.Text = "无法计算";
            CalculatorErrorText.Text = error;
            CopyCalculatorValueButton.IsEnabled = CopyCalculatorEquationButton.IsEnabled = false;
            return;
        }
        _currentCalculation = result;
        CalculatorResultBox.Text = result!.ResultText;
        CalculatorErrorText.Text = string.Empty;
        CopyCalculatorValueButton.IsEnabled = CopyCalculatorEquationButton.IsEnabled = true;
        AddCalculation(result);
    }

    private void AddCalculation(CalculationResult result)
    {
        var existing = Calculations.FirstOrDefault(item => item.Output == result.Output);
        if (existing is not null) Calculations.Remove(existing);
        Calculations.Insert(0, result);
        while (Calculations.Count > 30) Calculations.RemoveAt(Calculations.Count - 1);
        CalculationEmptyText.Visibility = Visibility.Collapsed;
    }

    private async void CopyCalculatorValueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCalculation is { } result) await CopyCalculationTextAsync(result.ResultText);
    }
    private async void CopyCalculatorEquationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCalculation is { } result) await CopyCalculationTextAsync(result.Output);
    }
    private Task CopyCalculationTextAsync(string text) => RunActionAsync("复制计算内容", async () =>
    {
        await _restoreService.CopyTextAsync(text);
        CountText.Text = "计算内容已复制";
    });

    private void CalculatorKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string key }) return;
        var box = CalculatorExpressionBox;
        if (key == "=") { CalculateExpression(); return; }
        if (key == "清空") box.Clear();
        else if (key == "退格")
        {
            if (box.SelectionLength > 0) box.SelectedText = string.Empty;
            else if (box.CaretIndex > 0)
            {
                var caret = box.CaretIndex;
                box.Text = box.Text.Remove(caret - 1, 1);
                box.CaretIndex = caret - 1;
            }
        }
        else if (box.Text.Length - box.SelectionLength + key.Length <= CalculatorService.MaxExpressionLength)
        {
            var start = box.SelectionStart;
            box.SelectedText = key;
            box.CaretIndex = start + key.Length;
            box.SelectionLength = 0;
        }
        box.Focus();
    }

    private void ReuseCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CalculationResult result }) return;
        CalculatorExpressionBox.Text = result.Expression;
        CalculatorExpressionBox.Focus();
        CalculatorExpressionBox.CaretIndex = CalculatorExpressionBox.Text.Length;
    }
    private async void CopyOriginalCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalculationResult result }) await CopyCalculationTextAsync(result.Expression);
    }
    private async void CopyRecentCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalculationResult result }) await CopyCalculationTextAsync(result.ResultText);
    }
    private void DeleteCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalculationResult result }) Calculations.Remove(result);
        CalculationEmptyText.Visibility = Calculations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ClearCalculationsButton_Click(object sender, RoutedEventArgs e)
    {
        Calculations.Clear();
        CalculationEmptyText.Visibility = Visibility.Visible;
    }
}
