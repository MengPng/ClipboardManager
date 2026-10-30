using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args[0]);
            var app = new System.Windows.Application();
            foreach (var file in new[] { "Palette.xaml", "Controls.xaml" })
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(
                    File.ReadAllText(Path.Combine(root, "src/ClipboardManager.Windows/Themes", file))));

            // Theme override layers may only redefine keys the standard palette already owns,
            // and must parse; a typo here would silently fall back to the standard value.
            var themeDir = Path.Combine(root, "src/ClipboardManager.Windows/Themes");
            var jiajia = (ResourceDictionary)XamlReader.Parse(File.ReadAllText(Path.Combine(themeDir, "Palette.Jiajia.xaml")));
            var standardKeys = ((ResourceDictionary)app.Resources.MergedDictionaries[0]).Keys.Cast<object>().ToHashSet();
            var unknown = jiajia.Keys.Cast<object>().Where(key => !standardKeys.Contains(key)).ToArray();
            if (unknown.Length > 0)
                throw new InvalidOperationException("Palette.Jiajia.xaml defines keys absent from Palette.xaml: " + string.Join(", ", unknown));
            // Shape and copy tokens: the standard palette owns every one (with 0.8.5 values) and
            // the jiajia layer overrides all of them, so a template never resolves a missing key.
            var shapeTokens = new[]
            {
                "RowCornerRadius", "RowBorderThickness", "RowCardInset", "RowCardBrush", "RowBorderBrush", "RowSelectedBorderBrush",
                "RowHoverBrush", "RowSelectedMetaBrush", "RowSeparatorVisibility", "SelectionDecorVisibility", "SelectionDecorBrush",
                "EmptyPetVisibility", "NavPillRadius", "NavPillBorderThickness", "NavPillBorderBrush", "NavBottomBorderThickness",
                "NavMarkBrush", "NavSelectedBackgroundBrush", "NavSelectedForegroundBrush", "ChipCornerRadius",
                "ChipCheckedBackgroundBrush", "ChipMarkBrush", "SearchCornerRadius", "SearchFillBrush", "RadiusPill",
                "CopyEmptyHistory", "CopyPausedHistory", "CopyNoResults"
            };
            var missingStandard = shapeTokens.Where(key => !standardKeys.Contains(key)).ToArray();
            var missingJiajia = shapeTokens.Where(key => !jiajia.Contains(key)).ToArray();
            if (missingStandard.Length > 0 || missingJiajia.Length > 0)
                throw new InvalidOperationException("Theme tokens missing. standard: " + string.Join(", ", missingStandard) + " jiajia: " + string.Join(", ", missingJiajia));
            var standardRow = (ResourceDictionary)app.Resources.MergedDictionaries[0];
            if (!Equals(standardRow["SelectionDecorVisibility"], Visibility.Collapsed) || !Equals(jiajia["SelectionDecorVisibility"], Visibility.Visible) ||
                !Equals(standardRow["RowBorderThickness"], new Thickness(2, 0, 0, 0)))
                throw new InvalidOperationException("Standard theme must keep the 0.8.5 row shape (left bar, no decor); jiajia must show the selection decor.");

            var document = XDocument.Load(Path.Combine(root, "src/ClipboardManager.Windows/MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var presentation = document.Root!.Name.Namespace;
            if (document.Root.Attribute("Width")?.Value != "340" ||
                document.Root.Attribute("Height")?.Value != "540" ||
                document.Root.Attribute("MinWidth")?.Value != "340" ||
                document.Root.Attribute("MinHeight")?.Value != "450")
                throw new InvalidOperationException("The compact shell must default to 340x540 DIP with a 340x450 DIP minimum.");
            var historyList = document.Descendants(presentation + "ListBox")
                .Single(element => element.Attribute(x + "Name")?.Value == "HistoryList");
            // Each history row keeps exactly one separator: a theme-switched hairline inside the row
            // (standard) or the card outline (jiajia). The row has a fixed pitch from HistoryRowHeight.
            var separator = historyList.Descendants(presentation + "Border")
                .Single(element => element.Attribute("Height")?.Value == "1");
            if (separator.Attribute("Visibility")?.Value != "{DynamicResource RowSeparatorVisibility}" ||
                separator.Attribute("VerticalAlignment")?.Value != "Bottom")
                throw new InvalidOperationException("History separator must be the bottom hairline driven by RowSeparatorVisibility, not a second line beside the card outline.");
            var rowGrid = historyList.Descendants(presentation + "DataTemplate").Single().Elements(presentation + "Grid").Single();
            // The pitch is pinned on the container (HistoryRowItemStyle) so the card inset sits inside it.
            if (rowGrid.Attribute("Height") is not null ||
                !File.ReadAllText(Path.Combine(root, "src", "ClipboardManager.Windows", "Themes", "Controls.xaml")).Contains("<Setter Property=\"Height\" Value=\"{Binding DataContext.HistoryRowHeight"))
                throw new InvalidOperationException("History rows must take their fixed pitch from HistoryRowHeight on the item container.");
            if (historyList.Attribute("ItemContainerStyle")?.Value != "{StaticResource HistoryRowItemStyle}")
                throw new InvalidOperationException("History rows must use the token-driven HistoryRowItemStyle.");
            var calculatorPanel = document.Descendants(presentation + "ScrollViewer")
                .Single(element => element.Attribute(x + "Name")?.Value == "CalculatorPanel");
            var calculatorTemplates = calculatorPanel.Descendants(presentation + "DataTemplate").ToArray();
            var calculationTemplate = calculatorTemplates.Single(template =>
                template.Descendants(presentation + "Button").Any(button => button.Attribute("Content")?.Value == "复制原算式"));
            var resultText = calculationTemplate.Descendants(presentation + "TextBlock")
                .Single(text => text.Attribute("Text")?.Value == "{Binding ResultText}");
            var expressionText = calculationTemplate.Descendants(presentation + "TextBlock")
                .Single(text => text.Attribute("Text")?.Value == "{Binding Expression}");
            if (resultText.Attribute("FontSize")?.Value != "18" ||
                expressionText.Attribute("Grid.Row")?.Value != "1" ||
                calculationTemplate.Descendants(presentation + "Border").All(border => border.Attribute("Height")?.Value != "1"))
                throw new InvalidOperationException("Calculator history must show the result first and separate records with a rule.");
            // Keep the production templates and layout, without starting services or accessing user data.
            foreach (var element in document.Descendants())
                foreach (var attribute in element.Attributes().ToArray())
                    if (attribute.Name == x + "Class" || attribute.Name.LocalName == "Icon" ||
                        System.Text.RegularExpressions.Regex.IsMatch(attribute.Value, @"^\w+_\w+$"))
                        attribute.Remove();
            var window = (Window)XamlReader.Parse(document.ToString());
            var selectionSettings = new ClipboardManager.Windows.SelectionAiSettingsPanel(new PreviewSettings(), new PreviewVault());
            RunSelectionSettingsRegressionChecks();
            ((ContentControl)window.FindName("SelectionAiSettingsHost")).Content = selectionSettings;
            var navigation = (TabControl)window.FindName("MainTabs");
            var headers = navigation.Items.Cast<TabItem>()
                .Where(tab => tab.Visibility == Visibility.Visible).Select(tab => tab.Header?.ToString()).ToArray();
            if (!headers.SequenceEqual(new[] { "历史", "配置", "待办", "计算", "划词" }))
                throw new InvalidOperationException("Five visible two-character navigation labels must preserve their approved order.");
            var pin = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("PinButton");
            if (pin.Visibility != Visibility.Visible || !pin.Focusable)
                throw new InvalidOperationException("The global always-on-top toggle must remain visible and keyboard accessible.");
            // The footer pet is added from code-behind (loose XAML here strips x:Class),
            // so probe it directly: it must load its frames and render a non-empty cell.
            var pet = new ClipboardManager.Windows.Services.PetCompanion { Width = 56, Height = 40 };
            pet.SetListening(true);
            var petHost = new Border { Width = 56, Height = 40, Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xF9, 0xF5)), Child = pet };
            // Unrooted host: manual measure/arrange, UpdateLayout alone renders nothing.
            petHost.Measure(new Size(56, 40));
            petHost.Arrange(new Rect(0, 0, 56, 40));
            petHost.UpdateLayout();
            var petCheck = new RenderTargetBitmap(56, 40, 96, 96, PixelFormats.Pbgra32);
            petCheck.Render(petHost);
            Directory.CreateDirectory(Path.Combine(root, "artifacts", "review", "0.8.6", "layout"));
            SavePng(petCheck, Path.Combine(root, "artifacts", "review", "0.8.6", "layout", "PanelPet-probe.png"));
            var petBytes = new byte[56 * 40 * 4];
            petCheck.CopyPixels(petBytes, 56 * 4, 0);
            if (ClipboardManager.Windows.Services.PetCompanion.LoadError is not null)
                throw new InvalidOperationException($"Pet frame load failed: {ClipboardManager.Windows.Services.PetCompanion.LoadError.GetType().Name}: {ClipboardManager.Windows.Services.PetCompanion.LoadError.Message} | {ClipboardManager.Windows.Services.PetCompanion.LoadError}");
            if (petBytes.All(b => b == 0))
                throw new InvalidOperationException("The panel pet rendered an empty frame; pack URI resource resolution failed in this host.");
            if (pet.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The panel pet must stay visible when its frames load.");
            // Same pet class hosted in the real transparent collapsed window: the
            // visual tree must render before DWM composition can ever show it.
            var collapsedProbe = new ClipboardManager.Windows.Services.CollapsedPetWindow { Left = 40, Top = 40 };
            collapsedProbe.Show();
            collapsedProbe.UpdateLayout();
            System.Threading.Thread.Sleep(350);
            var probeBitmap = new RenderTargetBitmap(56, 40, 96, 96, PixelFormats.Pbgra32);
            probeBitmap.Render(collapsedProbe);
            SavePng(probeBitmap, Path.Combine(root, "artifacts", "review", "0.8.6", "layout", "CollapsedPetWindow-probe.png"));
            var probeBytes = new byte[56 * 40 * 4];
            probeBitmap.CopyPixels(probeBytes, 56 * 4, 0);
            collapsedProbe.Close();
            if (probeBytes.All(b => b == 0))
                throw new InvalidOperationException("CollapsedPetWindow renders an empty visual tree; the pet never draws inside the collapsed host.");
            Console.WriteLine("PASS CollapsedPetWindow visual probe");
            var output = Path.Combine(root, "artifacts", "review", "0.8.6", "layout");
            Directory.CreateDirectory(output);
            // Rows are the production view-models, so the icon, link, badge and time rules under
            // test are the shipped ones. The xlsx exists on disk so the row is not marked as stale.
            var sampleDir = Path.Combine(output, "_sample");
            Directory.CreateDirectory(sampleDir);
            var sampleFile = Path.Combine(sampleDir, "发布检查记录.xlsx");
            File.WriteAllBytes(sampleFile, []);
            static ClipboardItem SampleItem(ClipboardItemType type, string preview, string source, DateTime at, string? manifest = null) => new()
            {
                Id = Guid.NewGuid(), WorkspaceId = Guid.Empty, DeviceId = Guid.Empty, ItemType = type, ContentHash = preview,
                TextContent = type == ClipboardItemType.Text ? preview : null, PreviewText = preview, SourceApp = source,
                CreatedAt = at, UpdatedAt = at, CapturedAt = at, FileManifest = manifest
            };
            var sampleRows = new[]
            {
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "本周产品检查清单", "记事本", DateTime.Today.AddHours(14).AddMinutes(32))),
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "https://clipboard.netkaize.com/design/v2", "Edge", DateTime.Today.AddHours(14).AddMinutes(29))),
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Files, "发布检查记录.xlsx", "资源管理器", DateTime.Today.AddHours(14).AddMinutes(4),
                    System.Text.Json.JsonSerializer.Serialize(new[] { sampleFile }))),
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "长文本示例：核对搜索、配置、待办、计算与划词的完整操作路径，并确认单行省略号不会撑开行高。", "记事本", DateTime.Today.AddHours(13).AddMinutes(10))),
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "本地优先，让每一次复制都安心。", "微信", DateTime.Today.AddDays(-1).AddHours(9).AddMinutes(41))),
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "季度复盘会议纪要", "记事本", DateTime.Today.AddDays(-1).AddHours(9).AddMinutes(12)))
            };
            if (sampleRows[0].IconText != "Aa" || sampleRows[1].IconText != "\uE71B" || sampleRows[2].IconText != "XLS" ||
                !sampleRows[1].Metadata.StartsWith("链接 · ") || !sampleRows[0].Metadata.StartsWith("文本 · "))
                throw new InvalidOperationException($"History type icons: {sampleRows[0].IconText}/{sampleRows[1].IconText}/{sampleRows[2].IconText} | {sampleRows[1].Metadata}");
            if (!sampleRows[0].Metadata.EndsWith(" · 14:32") || sampleRows[0].Metadata.Contains("今天") ||
                !System.Text.RegularExpressions.Regex.IsMatch(sampleRows[4].Metadata, @" · \d\d-\d\d \d\d:\d\d$"))
                throw new InvalidOperationException($"History time format: {sampleRows[0].Metadata} | {sampleRows[4].Metadata}");
            window.DataContext = new
            {
                RowMargin = new Thickness(10, 9, 10, 9),
                ThumbnailSize = 34d,
                HistoryRowHeight = 56d,
                HistoryIconSize = 30d,
                HistoryRowPadding = new Thickness(10, 0, 10, 0),
                Items = sampleRows
            };
            var surface = (FrameworkElement)window.Content;
            var tabs = (FrameworkElement)window.FindName("MainTabs");
            var title = (FrameworkElement)window.FindName("TitleBar");
            var listening = (FrameworkElement)window.FindName("ListeningStatusText");
            var settings = (FrameworkElement)window.FindName("SettingsButton");
            var count = (FrameworkElement)window.FindName("CountText");
            var panels = new[] { "HistoryPanel", "TodoPanel", "ConfigPanel", "CalculatorPanel", "SelectionAiPanel" }
                .Select(name => (FrameworkElement)window.FindName(name)).ToArray();
            window.Show();
            // Same wiring MainWindow's constructor does in code (loose XAML has no code-behind).
            var emptyPet = new ClipboardManager.Windows.Services.PetCompanion { Width = 112, Height = 80 };
            var emptyHost = (Grid)window.FindName("EmptyPetHost");
            emptyHost.Children.Add(emptyPet);
            emptyHost.SetResourceReference(UIElement.VisibilityProperty, "EmptyPetVisibility");
            var emptyPanel = (FrameworkElement)window.FindName("EmptyStatePanel");
            emptyPanel.Visibility = Visibility.Collapsed;
            ((TextBlock)window.FindName("HistoryCountText")).Text = $"{sampleRows.Length} 条";
            ((TextBlock)window.FindName("CountText")).Text = "5 条历史";
            for (var cycle = 0; cycle < 3; cycle++)
            {
                var hidden = new[] { "WindowTitleText", "ListeningStatusText", "PinButton", "PauseButton", "SettingsButton", "CloseButton" }
                    .Select(name => (FrameworkElement)window.FindName(name)).ToArray();
                foreach (var element in hidden) element.Visibility = Visibility.Collapsed;
                window.MaxWidth = 84;
                window.MinWidth = 84;
                window.MinHeight = 46;
                window.Width = 84;
                window.Height = 46;
                window.UpdateLayout();
                window.MaxWidth = double.PositiveInfinity;
                window.MinWidth = 340;
                window.MinHeight = 450;
                foreach (var element in hidden) element.Visibility = Visibility.Visible;
                window.Width = 340;
                window.Height = 900;
                window.UpdateLayout();
                var close = (FrameworkElement)window.FindName("CloseButton");
                var bounds = Bounds(close, surface);
                Console.WriteLine($"RESTORE {cycle}: close={bounds}, root={surface.RenderSize}, title={title.RenderSize}");
                if (bounds.Right > surface.ActualWidth || bounds.Width < 1)
                    throw new InvalidOperationException("Title controls overflow after collapse/expand.");
            }
            // Pass 1 renders the standard palette; pass 2 merges the jiajia override layer last
            // (same as ThemeService.Apply) and runs every layout assertion again.
            foreach (var themed in new[] { false, true })
            foreach (var size in new[] { new Size(340, 540), new Size(340, 450), new Size(520, 700) })
            foreach (var active in panels)
            {
                if (themed && !app.Resources.MergedDictionaries.Contains(jiajia))
                    app.Resources.MergedDictionaries.Add(jiajia);
                var renderDir = Path.Combine(output, themed ? "jiajia" : "standard");
                Directory.CreateDirectory(renderDir);
                foreach (var panel in panels)
                    panel.Visibility = panel == active ? Visibility.Visible : Visibility.Collapsed;
                // The history page shows its count beside the filters; the footer count is for the other pages.
                count.Visibility = active.Name == "HistoryPanel" ? Visibility.Collapsed : Visibility.Visible;
                window.Width = size.Width;
                window.Height = size.Height;
                surface.Measure(size);
                surface.Arrange(new Rect(size));
                surface.UpdateLayout();
                var tabBounds = Bounds(tabs, surface);
                var titleBounds = Bounds(title, surface);
                var listeningBounds = Bounds(listening, surface);
                var settingsBounds = Bounds(settings, surface);
                var contentBounds = Bounds(active, surface);
                var footerGrid = (FrameworkElement)window.FindName("FooterContentGrid");
                var footerBounds = Bounds(footerGrid, surface);
                var hintBounds = Bounds((FrameworkElement)window.FindName("ShortcutHintText"), surface);
                var countBounds = count.Visibility == Visibility.Visible ? Bounds(count, surface) : Rect.Empty;
                if (hintBounds.Right > size.Width || hintBounds.Width < 1 ||
                    (count.Visibility == Visibility.Visible && (countBounds.Left < 0 || countBounds.Right > hintBounds.Left)))
                    throw new InvalidOperationException($"Footer count and shortcut overlap: {countBounds}, {hintBounds}");
                // P1: the footer is thinner now that the history count moved up beside the filters.
                if (((FrameworkElement)footerGrid.Parent).ActualHeight > 30.5)
                    throw new InvalidOperationException($"Footer must be at most 30 DIP tall: {((FrameworkElement)footerGrid.Parent).ActualHeight}");
                var visibleTabs = navigation.Items.Cast<TabItem>().Where(tab => tab.Visibility == Visibility.Visible).ToArray();
                var tabWidths = visibleTabs.Select(tab => tab.ActualWidth).ToArray();
                if (tabWidths.Max() - tabWidths.Min() > 1 ||
                    visibleTabs.Any(tab => Bounds(tab, surface).Right > size.Width - 5))
                    throw new InvalidOperationException("Navigation must use equal-width cells without clipping.");
                var pinBounds = Bounds(pin, surface);
                var closeBounds = Bounds((FrameworkElement)window.FindName("CloseButton"), surface);
                var windowTitleBounds = Bounds((FrameworkElement)window.FindName("WindowTitleText"), surface);
                if (pinBounds.Width < 24 || closeBounds.Right > size.Width ||
                    windowTitleBounds.Right > listeningBounds.Left)
                    throw new InvalidOperationException("Window title, capture state and global controls must not overlap.");
                if (active.Name == "TodoPanel")
                {
                    var buttons = Descendants(active).OfType<Button>().ToArray();
                    var export = buttons.Single(button => Equals(button.Content, "导出明细"));
                    var exportBounds = Bounds(export, surface);
                    if (buttons.Any(button => Equals(button.Content, "标签管理")) ||
                        exportBounds.Left < 0 || exportBounds.Right > size.Width || exportBounds.Width < 1)
                        throw new InvalidOperationException("Todo tag management must be removed and export must remain visible.");
                    // P1 todo page: no duplicate "待办" heading, state and range filters on one row,
                    // four stat pills on one row followed by the export button; drill-down names unchanged.
                    var dateHeading = (FrameworkElement)window.FindName("TodoDateText");
                    if (dateHeading.IsVisible)
                        throw new InvalidOperationException("The todo page must not repeat the tab name as a heading.");
                    var pillNames = new[]
                    {
                        ("TodoCompletedSummaryButton", "完成统计"), ("TodoPendingSummaryButton", "待完成统计"),
                        ("TodoCreatedSummaryButton", "新增统计"), ("TodoRateSummaryButton", "完成率统计")
                    };
                    var pills = pillNames.Select(item => (System.Windows.Controls.Primitives.ToggleButton)window.FindName(item.Item1)).ToArray();
                    if (pills.Select((pill, index) => System.Windows.Automation.AutomationProperties.GetName(pill) != pillNames[index].Item2).Any(bad => bad))
                        throw new InvalidOperationException("Todo stat pills must keep their automation names.");
                    var filterButtons = new[] { "TodoAllStatsButton", "TodoActiveFilterButton", "TodoCompletedFilterButton", "TodoMonthStatsButton", "TodoYearStatsButton" }
                        .Select(name => Bounds((FrameworkElement)window.FindName(name), surface)).ToArray();
                    if (filterButtons.Max(b => b.Top) - filterButtons.Min(b => b.Top) > 1 || filterButtons.Max(b => b.Right) > size.Width - 12 + 1)
                        throw new InvalidOperationException($"Todo state and range filters must share one unclipped row: {string.Join(" ", filterButtons)}");
                    void AssertPills(string stage, bool oneRow)
                    {
                        surface.UpdateLayout();
                        var rects = pills.Select(pill => Bounds(pill, surface)).ToList();
                        var exportRect = Bounds(export, surface);
                        rects.Add(exportRect);
                        for (var i = 0; i < rects.Count; i++)
                        {
                            if (rects[i].Left < 12 - 1 || rects[i].Right > size.Width - 12 + 1 || rects[i].Bottom > contentBounds.Bottom)
                                throw new InvalidOperationException($"Todo summary {stage} clipped at {size}: item {i} {rects[i]}");
                            for (var j = i + 1; j < rects.Count; j++)
                                if (rects[i].IntersectsWith(new Rect(rects[j].X + 0.5, rects[j].Y + 0.5, rects[j].Width - 1, rects[j].Height - 1)))
                                    throw new InvalidOperationException($"Todo summary {stage} overlaps at {size}: {i} {rects[i]} / {j} {rects[j]}");
                        }

                        if (oneRow && (rects.Max(r => r.Top) - rects.Min(r => r.Top) > 8 || exportRect.Left < rects[3].Right))
                            throw new InvalidOperationException($"Todo summary pills and export must read as one row: {string.Join(" ", rects)}");
                    }
                    AssertPills("default", oneRow: true);
                    // Worst case: period-prefixed labels and a full one-decimal rate may wrap a pill, but never clip or overlap.
                    var labelTexts = new[] { "TodoCompletedLabel", "TodoCreatedLabel" }.Select(name => (TextBlock)window.FindName(name)).ToArray();
                    var valueTexts = new[] { "TodoTodayCompletedText", "TodoPendingText", "TodoTodayCreatedText", "TodoCompletionRateText" }.Select(name => (TextBlock)window.FindName(name)).ToArray();
                    var oldLabels = labelTexts.Select(text => text.Text).ToArray();
                    var oldValues = valueTexts.Select(text => text.Text).ToArray();
                    labelTexts[0].Text = "本月完成"; labelTexts[1].Text = "本月新增";
                    valueTexts[0].Text = "128"; valueTexts[1].Text = "128"; valueTexts[2].Text = "256"; valueTexts[3].Text = "83.3%";
                    AssertPills("worst-case", oneRow: false);
                    for (var i = 0; i < labelTexts.Length; i++) labelTexts[i].Text = oldLabels[i];
                    for (var i = 0; i < valueTexts.Length; i++) valueTexts[i].Text = oldValues[i];
                    surface.UpdateLayout();
                }
                if (tabBounds.Top < titleBounds.Bottom - 1 ||
                    contentBounds.Top < tabBounds.Bottom - 1 ||
                    contentBounds.Bottom > footerBounds.Top + 1 ||
                    contentBounds.Height < 100 || footerBounds.Top < size.Height - 85 ||
                    listeningBounds.Right > size.Width || settingsBounds.Right > size.Width)
                    throw new InvalidOperationException(
                        $"{active.Name} {size}: title={titleBounds}, listening={listeningBounds}, settings={settingsBounds}, tabs={tabBounds}, content={contentBounds}, footer={footerBounds}");
                if (active.Name == "HistoryPanel")
                {
                    var historyBox = (ListBox)window.FindName("HistoryList");
                    historyBox.SelectedIndex = 0;
                    surface.UpdateLayout();
                    var historyBounds = Bounds(historyBox, surface);
                    var countRect = Bounds((FrameworkElement)window.FindName("HistoryCountText"), surface);
                    var searchRect = Bounds((FrameworkElement)window.FindName("SearchBox"), surface);
                    var chipRects = new[] { "AllFilterButton", "TextFilterButton", "ImageFilterButton", "FileFilterButton" }
                        .Select(name => Bounds((FrameworkElement)window.FindName(name), surface)).ToArray();
                    if (countRect.Right > size.Width - 12 + 1 || countRect.Left < chipRects.Max(r => r.Right) ||
                        chipRects.Max(r => r.Top) - chipRects.Min(r => r.Top) > 1 || searchRect.Right > size.Width - 12 + 1 ||
                        historyBounds.Bottom > contentBounds.Bottom + 1)
                        throw new InvalidOperationException($"History filter row clipped at {size}: count={countRect}, chips={string.Join(" ", chipRects)}, search={searchRect}, list={historyBounds}");
                    var realized = Enumerable.Range(0, historyBox.Items.Count)
                        .Select(index => historyBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)
                        .Where(container => container is not null).Cast<ListBoxItem>().ToArray();
                    if (realized.Length < 2)
                        throw new InvalidOperationException($"History list must realize rows at {size}: {realized.Length}");
                    foreach (var container in realized)
                    {
                        if (Math.Abs(container.ActualHeight - 56) > 0.5)
                            throw new InvalidOperationException($"History row pitch must be 56 DIP at {size}: {container.ActualHeight}");
                        var rowText = Descendants(container).OfType<TextBlock>().First(text => text.Text == ((ClipboardManager.Windows.HistoryItemViewModel)container.DataContext).PreviewText);
                        if (rowText.ActualHeight > 22)
                            throw new InvalidOperationException($"History preview must stay on one line at {size}: {rowText.ActualHeight} for '{rowText.Text}'");
                        var rowIcon = Descendants(container).OfType<Border>().First(border => border.Width == 30);
                        if (rowIcon.ActualWidth != 30 || rowIcon.ActualHeight != 30)
                            throw new InvalidOperationException($"History type icon must be 30x30: {rowIcon.ActualWidth}x{rowIcon.ActualHeight}");
                        var hairline = Descendants(container).OfType<Border>().Single(border => border.Height == 1);
                        if (hairline.IsVisible == themed)
                            throw new InvalidOperationException($"Row separator must be a hairline in standard only (themed={themed}, visible={hairline.IsVisible}).");
                    }

                    // Selection decor (shadow, clip, fold) belongs to the jiajia theme only.
                    var selectedRow = realized[0];
                    foreach (var part in new[] { "Shadow", "Clip", "Fold" })
                    {
                        var element = (FrameworkElement)selectedRow.Template.FindName(part, selectedRow);
                        if (element.IsVisible != themed)
                            throw new InvalidOperationException($"Selection decor '{part}' visibility must follow the theme (themed={themed}, visible={element.IsVisible}).");
                        if (realized.Length > 1 && ((FrameworkElement)realized[1].Template.FindName(part, realized[1])).IsVisible)
                            throw new InvalidOperationException($"Unselected rows must not show selection decor '{part}'.");
                    }

                    if (size.Width == 340 && size.Height == 540 && historyBox.ActualHeight / 56 < 5.4)
                        throw new InvalidOperationException($"340x540 must show about 5.5 history rows: viewport {historyBox.ActualHeight} DIP");
                    if (size.Width == 340 && size.Height == 450 && historyBox.ActualHeight < 56 * 2)
                        throw new InvalidOperationException($"340x450 must keep two full history rows: viewport {historyBox.ActualHeight} DIP");
                    Console.WriteLine($"  history viewport {historyBox.ActualHeight:F0} DIP = {historyBox.ActualHeight / 56:F2} rows at {size}");
                }
                if (active.Name == "ConfigPanel")
                {
                    var search = (FrameworkElement)window.FindName("CollectionSearchBox");
                    if (search.ActualWidth < 120)
                        throw new InvalidOperationException($"Configuration search clipped: {search.ActualWidth}");
                }
                if (active.Name == "SelectionAiPanel" && tabBounds.Right > size.Width)
                    throw new InvalidOperationException($"Selection tab overflows at {size}: {tabBounds}");
                if (active.Name == "SelectionAiPanel")
                {
                    var save = Descendants(selectionSettings).OfType<Button>()
                        .Single(button => Equals(button.Content, "保存划词设置"));
                    var saveBounds = Bounds(save, surface);
                    if (!save.IsEnabled || saveBounds.Width < 50 || saveBounds.Bottom > contentBounds.Bottom + 1)
                        throw new InvalidOperationException("Selection settings must load and keep its save action inside the content area.");
                }
                Console.WriteLine($"PASS {(themed ? "jiajia" : "standard")} {active.Name} {size}: content={contentBounds}, footerY={footerBounds.Top:F0}");
                navigation.SelectedItem = active.Name switch
                {
                    "TodoPanel" => window.FindName("TodoTab"),
                    "ConfigPanel" => window.FindName("PersonalSpaceTab"),
                    "CalculatorPanel" => window.FindName("CalculatorTab"),
                    "SelectionAiPanel" => window.FindName("SelectionAiTab"),
                    _ => window.FindName("HistoryTab")
                };
                count.SetCurrentValue(TextBlock.TextProperty, active.Name switch
                {
                    "TodoPanel" => "0 项待办",
                    "ConfigPanel" => "0 项配置",
                    "CalculatorPanel" => "本次计算",
                    "SelectionAiPanel" => "本机设置",
                    _ => "5 条历史"
                });
                ((TextBlock)window.FindName("ShortcutHintText")).Text = active.Name switch
                {
                    "TodoPanel" => "本机待办",
                    "CalculatorPanel" => "Enter 计算",
                    "SelectionAiPanel" => "保存后应用",
                    _ => "Enter 使用"
                };
                var list = (ListBox)window.FindName("HistoryList");
                if (active.Name == "HistoryPanel") list.SelectedIndex = 0;
                surface.UpdateLayout();
                SaveRender(surface, size, Path.Combine(renderDir, $"{active.Name}-{size.Width}x{size.Height}.png"));

                if (active.Name == "HistoryPanel")
                {
                    // Empty history: the jiajia theme shows the 112x80 pet and its own copy; standard shows text only.
                    var populated = window.DataContext;
                    window.DataContext = new
                    {
                        RowMargin = new Thickness(10, 9, 10, 9), ThumbnailSize = 34d, HistoryRowHeight = 56d,
                        HistoryIconSize = 30d, HistoryRowPadding = new Thickness(10, 0, 10, 0),
                        Items = Array.Empty<ClipboardManager.Windows.HistoryItemViewModel>()
                    };
                    var emptyText = (TextBlock)window.FindName("EmptyStateText");
                    foreach (var (variant, copyKey, paused) in new[]
                    {
                        ("empty", "CopyEmptyHistory", false), ("paused", "CopyPausedHistory", true), ("noresults", "CopyNoResults", false)
                    })
                    {
                        emptyText.Text = (string)app.FindResource(copyKey);
                        emptyPanel.Visibility = Visibility.Visible;
                        emptyPet.SetListening(!paused);
                        ((TextBlock)window.FindName("HistoryCountText")).Text = "0 条";
                        surface.UpdateLayout();
                        var expectedCopy = themed
                            ? copyKey switch { "CopyEmptyHistory" => "夹夹在等你复制点什么", "CopyPausedHistory" => "已暂停监听，夹夹在打盹", _ => "夹夹没找到，换个关键词试试？" }
                            : copyKey switch { "CopyEmptyHistory" => "还没有历史，复制内容试试", "CopyPausedHistory" => "监听已暂停", _ => "没有匹配的记录" };
                        if (emptyText.Text != expectedCopy)
                            throw new InvalidOperationException($"Empty copy for {variant} must come from the theme: '{emptyText.Text}' != '{expectedCopy}'");
                        if (emptyHost.IsVisible != themed)
                            throw new InvalidOperationException($"Empty-state pet visibility must follow the theme (themed={themed}, visible={emptyHost.IsVisible}).");
                        if (themed && (Math.Abs(emptyHost.ActualWidth - 112) > 0.5 || Math.Abs(emptyHost.ActualHeight - 80) > 0.5 ||
                                       Math.Abs(emptyPet.ActualWidth - 112) > 0.5 || Math.Abs(emptyPet.ActualHeight - 80) > 0.5))
                            throw new InvalidOperationException($"Empty-state pet must be an exact 2x of 56x40: {emptyPet.ActualWidth}x{emptyPet.ActualHeight}");
                        if (emptyPet.Focusable || emptyPet.IsHitTestVisible && emptyPanel.IsHitTestVisible)
                            throw new InvalidOperationException("Empty-state pet must be non-focusable and non-hit-testable.");
                        var emptyBounds = Bounds(emptyPanel, surface);
                        var emptyTextBounds = Bounds(emptyText, surface);
                        var emptyArea = Bounds((FrameworkElement)window.FindName("HistoryList"), surface);
                        if (emptyBounds.Left < 0 || emptyBounds.Right > size.Width || emptyBounds.Top < emptyArea.Top - 1 ||
                            emptyBounds.Bottom > emptyArea.Bottom + 1 || emptyTextBounds.Width < 1)
                            throw new InvalidOperationException($"Empty state is clipped at {size} ({variant}): panel={emptyBounds}, list={emptyArea}");
                        SaveRender(surface, size, Path.Combine(renderDir, $"HistoryPanel-{variant}-{size.Width}x{size.Height}.png"));
                    }
                    emptyPanel.Visibility = Visibility.Collapsed;
                    emptyPet.SetListening(true);
                    window.DataContext = populated;
                    ((TextBlock)window.FindName("HistoryCountText")).Text = $"{sampleRows.Length} 条";
                    surface.UpdateLayout();
                }
            }
            window.Close();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement surface) =>
        element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));

    private static void RunSelectionSettingsRegressionChecks()
    {
        var promptOne = new PromptTemplate(Guid.NewGuid(), "提示词一", "正文一 {text}");
        var site = new PromptTemplate(Guid.NewGuid(), "官网", "{text}", PromptActions.OpenUrl, "https://example.com");
        var promptTwo = new PromptTemplate(Guid.NewGuid(), "提示词二", "正文二 {text}");
        var promptThree = new PromptTemplate(Guid.NewGuid(), "提示词三", "正文三 {text}");

        AssertDeleteKeepsNeighbor(promptOne, site, promptTwo, promptThree, 0);
        AssertDeleteKeepsNeighbor(promptOne, site, promptTwo, promptThree, 1);
        AssertDeleteKeepsNeighbor(promptOne, site, promptTwo, promptThree, 3);
        AssertMoveKeepsIdentity(promptOne, site, promptTwo, promptThree, 1, -1);
        AssertMoveKeepsIdentity(promptOne, site, promptTwo, promptThree, 1, 1);
        Console.WriteLine("PASS SelectionAiSettingsPanel editor regression checks");
    }

    private static void AssertDeleteKeepsNeighbor(
        PromptTemplate promptOne,
        PromptTemplate site,
        PromptTemplate promptTwo,
        PromptTemplate promptThree,
        int deleteIndex)
    {
        var store = CreateSettingsStore(promptOne, site, promptTwo, promptThree);
        var panel = LoadPanel(store);
        SelectTemplate(panel, deleteIndex);
        Invoke(panel, "DeleteSelected");
        Save(panel);

        var saved = store.Saved.SelectionAi.Templates;
        var expected = new[] { promptOne, site, promptTwo, promptThree }.Where((_, index) => index != deleteIndex).ToArray();
        AssertTemplatesEqual(expected, saved, $"delete index {deleteIndex}");
        AssertReloadSaveKeepsTemplates(store, expected, $"delete index {deleteIndex}");
    }

    private static void AssertMoveKeepsIdentity(
        PromptTemplate promptOne,
        PromptTemplate site,
        PromptTemplate promptTwo,
        PromptTemplate promptThree,
        int selectedIndex,
        int delta)
    {
        var store = CreateSettingsStore(promptOne, site, promptTwo, promptThree);
        var panel = LoadPanel(store);
        SelectTemplate(panel, selectedIndex);
        Invoke(panel, "Move", delta);
        Save(panel);

        var expected = new List<PromptTemplate> { promptOne, site, promptTwo, promptThree };
        (expected[selectedIndex], expected[selectedIndex + delta]) =
            (expected[selectedIndex + delta], expected[selectedIndex]);
        AssertTemplatesEqual(expected, store.Saved.SelectionAi.Templates, $"move {delta}");
        AssertReloadSaveKeepsTemplates(store, expected, $"move {delta}");
    }

    private static PreviewSettings CreateSettingsStore(params PromptTemplate[] templates) =>
        new(new AppSettings
        {
            SelectionAi = new SelectionAiSettings
            {
                Provider = "LocalGateway",
                BaseUrl = "http://127.0.0.1:3425/v1",
                Model = "qoder/dfmodel",
                Templates = templates
            }
        });

    private static ClipboardManager.Windows.SelectionAiSettingsPanel LoadPanel(PreviewSettings store)
    {
        var panel = new ClipboardManager.Windows.SelectionAiSettingsPanel(store, new PreviewVault());
        panel.ReloadAsync().GetAwaiter().GetResult();
        return panel;
    }

    private static void SelectTemplate(ClipboardManager.Windows.SelectionAiSettingsPanel panel, int index) =>
        Invoke(panel, "Select", index);

    private static void Save(ClipboardManager.Windows.SelectionAiSettingsPanel panel) =>
        ((Task)(Invoke(panel, "SaveAsync") ?? throw new InvalidOperationException("SaveAsync did not return a task.")))
            .GetAwaiter().GetResult();

    private static object? Invoke(object instance, string name, params object[] arguments) =>
        (instance.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: arguments.Select(argument => argument.GetType()).ToArray(),
            modifiers: null)
            ?? throw new InvalidOperationException($"Missing {name} method."))
        .Invoke(instance, arguments);

    private static T GetField<T>(object instance, string name) where T : class =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Missing {name} field."));

    private static void AssertTemplatesEqual(
        IReadOnlyList<PromptTemplate> expected,
        IReadOnlyList<PromptTemplate> actual,
        string operation)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Selection settings {operation} corrupted a template identity, order, or fields.");
    }

    private static void AssertReloadSaveKeepsTemplates(
        PreviewSettings store,
        IReadOnlyList<PromptTemplate> expected,
        string operation)
    {
        Save(LoadPanel(store));
        AssertTemplatesEqual(expected, store.Saved.SelectionAi.Templates, $"{operation} reload/save");
    }

    private static void SaveRender(FrameworkElement surface, Size size, string path)
    {
        var bitmap = new RenderTargetBitmap((int)size.Width * 2, (int)size.Height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        SavePng(bitmap, path);
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class PreviewSettings(AppSettings? initial = null) : IAppSettingsStore
    {
        private AppSettings _current = initial ?? new AppSettings();

        public AppSettings Saved { get; private set; } = initial ?? new AppSettings();

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            _current = settings;
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class PreviewVault : ICredentialVault
    {
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
