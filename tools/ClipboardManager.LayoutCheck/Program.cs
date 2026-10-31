using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ClipboardManager.Application.Services;
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
            var kami = (ResourceDictionary)XamlReader.Parse(File.ReadAllText(Path.Combine(themeDir, "Palette.Kami.xaml")));
            var standardKeys = ((ResourceDictionary)app.Resources.MergedDictionaries[0]).Keys.Cast<object>().ToHashSet();
            foreach (var (layerName, layer) in new[] { ("Palette.Jiajia.xaml", jiajia), ("Palette.Kami.xaml", kami) })
            {
                var unknown = layer.Keys.Cast<object>().Where(key => !standardKeys.Contains(key)).ToArray();
                if (unknown.Length > 0)
                    throw new InvalidOperationException($"{layerName} defines keys absent from Palette.xaml: " + string.Join(", ", unknown));
            }
            // Shape and copy tokens: the standard palette owns every one (with 0.8.5 values) and
            // the jiajia layer overrides all of them, so a template never resolves a missing key.
            var shapeTokens = new[]
            {
                "RowCornerRadius", "RowBorderThickness", "RowCardInset", "RowCardBrush", "RowBorderBrush", "RowSelectedBorderBrush",
                "RowHoverBrush", "RowSelectedMetaBrush", "RowSeparatorVisibility", "SelectionEdgeThickness", "SelectionEdgeBrush",
                "SelectionDecorVisibility", "SelectionDecorBrush",
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
            // One selection rule for every list: the left edge bar comes from SelectionEdgeThickness
            // (standard and kami 2, jiajia 0) and decor only from SelectionDecorVisibility (jiajia only).
            if (!Equals(standardRow["SelectionEdgeThickness"], new Thickness(2, 0, 0, 0)) ||
                !Equals(jiajia["SelectionEdgeThickness"], new Thickness(0)) || !Equals(kami["SelectionEdgeThickness"], new Thickness(2, 0, 0, 0)) ||
                kami.Contains("SelectionDecorVisibility") || kami.Contains("EmptyPetVisibility") || kami.Contains("RowSeparatorVisibility"))
                throw new InvalidOperationException("Selection edge tokens must be standard 2 / jiajia 0 / kami 2, and kami must inherit decor, hairline and empty-pet tokens from standard.");
            var kamiDisplayFont = ((FontFamily)kami["AppDisplayFontFamily"]).Source;
            if (!kamiDisplayFont.StartsWith("Georgia") || kami.Contains("AppFontFamily"))
                throw new InvalidOperationException("Kami overrides only AppDisplayFontFamily; the body font stays Microsoft YaHei.");

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
            // Calculator page: tape (expression left, result right, actions in between), live result, 4x5 keypad.
            var calculatorPanel = document.Descendants(presentation + "Grid")
                .Single(element => element.Attribute(x + "Name")?.Value == "CalculatorPanel");
            var tapeTemplate = calculatorPanel.Descendants(presentation + "ListBox")
                .Single(element => element.Attribute(x + "Name")?.Value == "CalculatorTapeList")
                .Descendants(presentation + "DataTemplate").Single();
            var tapeResult = tapeTemplate.Descendants(presentation + "TextBlock")
                .Single(text => text.Attribute("Text")?.Value == "{Binding DisplayText}");
            var tapeExpression = tapeTemplate.Descendants(presentation + "TextBlock")
                .Single(text => text.Attribute("Text")?.Value == "{Binding Expression}");
            var tapeActionLabels = tapeTemplate.Descendants(presentation + "Button")
                .Select(button => button.Attribute("AutomationProperties.Name")?.Value ?? button.Attribute("Content")?.Value).ToArray();
            if (tapeResult.Attribute("Grid.Column")?.Value != "1" || tapeResult.Attribute("FontSize")?.Value != "{DynamicResource FontSizeTitle}" ||
                tapeExpression.Attribute("Grid.Column") is not null ||
                !tapeActionLabels.SequenceEqual(new[] { "复制", "复用", "删除" }))
                throw new InvalidOperationException("Calculator tape rows must read expression, then copy/reuse/delete, then the result at the right edge.");
            var keypad = calculatorPanel.Descendants(presentation + "UniformGrid")
                .Single(element => element.Attribute(x + "Name")?.Value == "CalculatorKeypad");
            var keypadKeys = keypad.Elements(presentation + "Button").ToArray();
            if (keypad.Attribute("Columns")?.Value != "4" || keypad.Attribute("Rows")?.Value != "5" || keypadKeys.Length != 20 ||
                keypadKeys.Any(key => string.IsNullOrEmpty(key.Attribute("AutomationProperties.Name")?.Value ?? key.Attribute("Content")?.Value)))
                throw new InvalidOperationException("The keypad must be a named 4x5 grid of 20 keys.");
            if (System.Text.RegularExpressions.Regex.IsMatch(calculatorPanel.ToString(), @"#[0-9A-Fa-f]{3,8}\b|\bColor=|\bCornerRadius=""\d"))
                throw new InvalidOperationException("The calculator page must take colours and corner radii from theme tokens, not literals.");
            // Share page: publish / subscribe segments, every colour and radius from tokens, every action named.
            var sharePanel = document.Descendants(presentation + "Grid")
                .Single(element => element.Attribute(x + "Name")?.Value == "SharePanel");
            if (System.Text.RegularExpressions.Regex.IsMatch(sharePanel.ToString(), @"#[0-9A-Fa-f]{3,8}\b|\bColor=|\bCornerRadius=""\d"))
                throw new InvalidOperationException("The share page must take colours and corner radii from theme tokens, not literals.");
            foreach (var brush in sharePanel.DescendantsAndSelf().Attributes()
                         .Where(attribute => attribute.Name.LocalName is "Foreground" or "Background" or "BorderBrush" or "Fill" or "Stroke"))
                if (!brush.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Share page brush '{brush.Name.LocalName}' must be a DynamicResource token: {brush.Value}");
            foreach (var shareButton in sharePanel.Descendants(presentation + "Button"))
                if (string.IsNullOrEmpty(shareButton.Attribute("AutomationProperties.Name")?.Value ?? shareButton.Attribute("Content")?.Value))
                    throw new InvalidOperationException("Every share page button needs an accessible name.");
            if (sharePanel.Descendants(presentation + "ScrollViewer").Count() != 2 ||
                sharePanel.Descendants(presentation + "RadioButton").Count() != 2)
                throw new InvalidOperationException("The share page must have exactly the publish and subscribe segments.");
            // Keep the production templates and layout, without starting services or accessing user data.
            foreach (var element in document.Descendants())
                foreach (var attribute in element.Attributes().ToArray())
                    if (attribute.Name == x + "Class" || attribute.Name.LocalName == "Icon" ||
                        System.Text.RegularExpressions.Regex.IsMatch(attribute.Value, @"^\w+_\w+$"))
                        attribute.Remove();
            var window = (Window)XamlReader.Parse(document.ToString());
            var navigation = (TabControl)window.FindName("MainTabs");
            var headers = navigation.Items.Cast<TabItem>()
                .Where(tab => tab.Visibility == Visibility.Visible).Select(tab => tab.Header?.ToString()).ToArray();
            if (!headers.SequenceEqual(new[] { "历史", "配置", "待办", "计算", "共享" }))
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
            Directory.CreateDirectory(Path.Combine(root, "artifacts", "review", "0.9.0", "layout"));
            SavePng(petCheck, Path.Combine(root, "artifacts", "review", "0.9.0", "layout", "PanelPet-probe.png"));
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
            SavePng(probeBitmap, Path.Combine(root, "artifacts", "review", "0.9.0", "layout", "CollapsedPetWindow-probe.png"));
            var probeBytes = new byte[56 * 40 * 4];
            probeBitmap.CopyPixels(probeBytes, 56 * 4, 0);
            collapsedProbe.Close();
            if (probeBytes.All(b => b == 0))
                throw new InvalidOperationException("CollapsedPetWindow renders an empty visual tree; the pet never draws inside the collapsed host.");
            Console.WriteLine("PASS CollapsedPetWindow visual probe");
            var output = Path.Combine(root, "artifacts", "review", "0.9.0", "layout");
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
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "长文本示例：核对搜索、配置、待办、计算与设置的完整操作路径，并确认单行省略号不会撑开行高。", "记事本", DateTime.Today.AddHours(13).AddMinutes(10))),
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "本地优先，让每一次复制都安心。", "微信", DateTime.Today.AddDays(-1).AddHours(9).AddMinutes(41))),
                new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "季度复盘会议纪要", "记事本", DateTime.Today.AddDays(-1).AddHours(9).AddMinutes(12)))
            };
            if (sampleRows[0].IconText != "Aa" || sampleRows[1].IconText != "\uE71B" || sampleRows[2].IconText != "XLS" ||
                !sampleRows[1].Metadata.StartsWith("链接 · ") || !sampleRows[0].Metadata.StartsWith("文本 · "))
                throw new InvalidOperationException($"History type icons: {sampleRows[0].IconText}/{sampleRows[1].IconText}/{sampleRows[2].IconText} | {sampleRows[1].Metadata}");
            if (!sampleRows[0].Metadata.EndsWith(" · 14:32") || sampleRows[0].Metadata.Contains("今天") ||
                !System.Text.RegularExpressions.Regex.IsMatch(sampleRows[4].Metadata, @" · \d\d-\d\d \d\d:\d\d$"))
                throw new InvalidOperationException($"History time format: {sampleRows[0].Metadata} | {sampleRows[4].Metadata}");
            // Config page samples use the production view-models; SelectedFolder/SelectedCollectionItem
            // are TwoWay in XAML, so the context needs settable properties (not an anonymous type).
            static CollectionFolder SampleFolder(string name, bool system = false) => new()
            {
                Id = Guid.NewGuid(), WorkspaceId = Guid.Empty, Name = name, SortKey = 1m, IsSystem = system,
                CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now
            };
            static CollectionItem SampleCollection(CollectionItemType type, string title, string? text = null, string? url = null) => new()
            {
                Id = Guid.NewGuid(), WorkspaceId = Guid.Empty, FolderId = Guid.Empty, ItemType = type, Title = title, TextContent = text, Url = url,
                SortKey = 1m, CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now
            };
            var sampleFolders = new[]
            {
                new ClipboardManager.Windows.FolderViewModel(SampleFolder("未分类", true), 3, "个人"),
                new ClipboardManager.Windows.FolderViewModel(SampleFolder("常用回复"), 5, "个人"),
                new ClipboardManager.Windows.FolderViewModel(SampleFolder("地址与账号备忘"), 2, "个人")
            };
            var sampleCollection = new[]
            {
                new ClipboardManager.Windows.CollectionItemViewModel(SampleCollection(CollectionItemType.Text, "周报开头", "本周完成了剪切板主题与布局的统一检查。")),
                new ClipboardManager.Windows.CollectionItemViewModel(SampleCollection(CollectionItemType.Link, "设计规范", url: "https://clipboard.netkaize.com/design/v2")),
                new ClipboardManager.Windows.CollectionItemViewModel(SampleCollection(CollectionItemType.Text, "会议链接", "https://meeting.example.com/room/42"))
            };
            var previewContext = new PreviewContext
            {
                Items = sampleRows, Folders = sampleFolders, CollectionItems = sampleCollection,
                SelectedFolder = sampleFolders[1], SelectedCollectionItem = sampleCollection[0],
                // Oldest first; the worst case is an expression that cannot fit beside its actions and result.
                PublishedRows = new(SamplePublishedRows()),
                SubscriberRows = new(SampleSubscriberRows()),
                PublisherRows = new(SamplePublisherRows()),
                PairedPublisherRows = new(SamplePairedPublisherRows()),
                SubscriptionRows = new(SampleSubscriptionRows()),
                Calculations = new System.Collections.ObjectModel.ObservableCollection<CalculationResult>
                {
                    new("200+10%", 220m, "200 + 20"),
                    new("1,250×2", 2500m),
                    new("123456789×987654321+(1250+380)/3", 123456789m * 987654321m + 1630m / 3m),
                    new("(1250+380)/3", 1630m / 3m)
                }
            };
            window.DataContext = previewContext;
            var surface = (FrameworkElement)window.Content;
            var tabs = (FrameworkElement)window.FindName("MainTabs");
            var title = (FrameworkElement)window.FindName("TitleBar");
            var listening = (FrameworkElement)window.FindName("ListeningStatusText");
            var settings = (FrameworkElement)window.FindName("SettingsButton");
            var count = (FrameworkElement)window.FindName("CountText");
            var panels = new[] { "HistoryPanel", "TodoPanel", "ConfigPanel", "CalculatorPanel", "SharePanel" }
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
            // One pass per theme: standard renders the bare palette; jiajia and kami merge their override
            // layer last (same as ThemeService.Apply, old layer removed first) and run every layout assertion again.
            var themeLayers = new (string Name, ResourceDictionary? Layer)[] { ("standard", null), ("jiajia", jiajia), ("kami", kami) };
            ResourceDictionary? activeLayer = null;
            Color? historySelectedColor = null;
            foreach (var (theme, layer) in themeLayers)
            foreach (var size in new[] { new Size(340, 540), new Size(340, 450), new Size(520, 700) })
            foreach (var active in panels)
            {
                if (!ReferenceEquals(activeLayer, layer))
                {
                    if (activeLayer is not null) app.Resources.MergedDictionaries.Remove(activeLayer);
                    if (layer is not null) app.Resources.MergedDictionaries.Add(layer);
                    activeLayer = layer;
                }
                // Decor, hairline and empty-state pet differ only for jiajia; kami is flat like standard.
                var themed = theme == "jiajia";
                var renderDir = Path.Combine(output, theme);
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
                    // The shipped screenshot case (5 / 1 / 6 / 83.3%) must stay on one row with the export button.
                    var valueTexts = new[] { "TodoTodayCompletedText", "TodoPendingText", "TodoTodayCreatedText", "TodoCompletionRateText" }.Select(name => (TextBlock)window.FindName(name)).ToArray();
                    var oldValues = valueTexts.Select(text => text.Text).ToArray();
                    valueTexts[0].Text = "5"; valueTexts[1].Text = "1"; valueTexts[2].Text = "6"; valueTexts[3].Text = "83.3%";
                    AssertPills("typical", oneRow: true);
                    // Worst case: large counts may wrap the row, but never clip or overlap.
                    valueTexts[0].Text = "128"; valueTexts[1].Text = "128"; valueTexts[2].Text = "256"; valueTexts[3].Text = "83.3%";
                    AssertPills("worst-case", oneRow: false);
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
                    var chipRects = new[] { "AllFilterButton", "TextFilterButton", "LinkFilterButton", "ImageFilterButton", "FileFilterButton" }
                        .Select(name => Bounds((FrameworkElement)window.FindName(name), surface)).ToArray();
                    // The chip group is centred in the row (equal gutters left and right, within a pixel or two).
                    var chipLeft = chipRects.Min(r => r.Left);
                    var chipRight = chipRects.Max(r => r.Right);
                    var rowCentre = (searchRect.Left + searchRect.Right) / 2;
                    if (Math.Abs((chipLeft + chipRight) / 2 - rowCentre) > 2)
                        throw new InvalidOperationException($"History filter chips must be centred at {size}: chips {chipLeft}..{chipRight}, row centre {rowCentre}");
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
                            throw new InvalidOperationException($"Row separator must be a hairline in standard and kami, hidden in jiajia ({theme}, visible={hairline.IsVisible}).");
                    }

                    // Selection decor (shadow, clip, fold) belongs to the jiajia theme only.
                    var selectedRow = realized[0];
                    historySelectedColor = SelectedBackground(selectedRow);
                    foreach (var part in new[] { "Shadow", "Clip", "Fold" })
                    {
                        var element = (FrameworkElement)selectedRow.Template.FindName(part, selectedRow);
                        if (element.IsVisible != themed)
                            throw new InvalidOperationException($"Selection decor '{part}' must show in jiajia only ({theme}, visible={element.IsVisible}).");
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
                    // Selected folder + selected item share one selection rule with the history list:
                    // same fill, the left edge bar only where the theme asks for it, decor on card rows only.
                    var folderBox = (ListBox)window.FindName("FolderList");
                    var collectionBox = (ListBox)window.FindName("CollectionList");
                    var folderRow = folderBox.ItemContainerGenerator.ContainerFromItem(previewContext.SelectedFolder) as ListBoxItem;
                    var itemRow = collectionBox.ItemContainerGenerator.ContainerFromItem(previewContext.SelectedCollectionItem) as ListBoxItem;
                    var otherFolderRow = folderBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                    var otherItemRow = collectionBox.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem;
                    if (folderRow is not { IsSelected: true } || itemRow is not { IsSelected: true } || otherFolderRow is null || otherItemRow is null ||
                        otherFolderRow.IsSelected || otherItemRow.IsSelected)
                        throw new InvalidOperationException($"Config page must show one selected folder and one selected item at {size} ({theme}).");
                    var folderColor = SelectedBackground(folderRow);
                    var itemColor = SelectedBackground(itemRow);
                    var themeSelection = (SolidColorBrush)app.FindResource("SelectionBrush");
                    if (folderColor != itemColor || folderColor != historySelectedColor || folderColor != themeSelection.Color)
                        throw new InvalidOperationException($"Selected fill must match across folders, config items and history ({theme}, {size}): folder={folderColor} item={itemColor} history={historySelectedColor} token={themeSelection.Color}");
                    if (SelectedBackground(otherFolderRow) == folderColor || SelectedBackground(otherItemRow) == itemColor)
                        throw new InvalidOperationException($"Unselected config rows must not use the selected fill ({theme}).");
                    var edge = (Thickness)app.FindResource("SelectionEdgeThickness");
                    if (((Border)folderRow.Template.FindName("Root", folderRow)).BorderThickness != edge ||
                        ((Border)otherFolderRow.Template.FindName("Root", otherFolderRow)).BorderThickness != edge)
                        throw new InvalidOperationException($"Folder rows must take their left bar from SelectionEdgeThickness ({theme}).");
                    // Folder rows are plain list items: no decor parts at all. Card rows (config items) show decor in jiajia only.
                    foreach (var part in new[] { "Shadow", "Clip", "Fold" })
                    {
                        if (folderRow.Template.FindName(part, folderRow) is not null)
                            throw new InvalidOperationException($"Folder rows must not carry selection decor '{part}'.");
                        var decor = (FrameworkElement)itemRow.Template.FindName(part, itemRow);
                        if (decor.IsVisible != themed || ((FrameworkElement)otherItemRow.Template.FindName(part, otherItemRow)).IsVisible)
                            throw new InvalidOperationException($"Config item decor '{part}' must show on the selected card in jiajia only ({theme}, visible={decor.IsVisible}).");
                    }
                    var folderRowBounds = Bounds(folderRow, surface);
                    var itemRowBounds = Bounds(itemRow, surface);
                    if (folderRowBounds.Left < 0 || folderRowBounds.Right > size.Width || itemRowBounds.Right > size.Width + 0.5 || itemRowBounds.Width < 40)
                        throw new InvalidOperationException($"Config rows are clipped at {size} ({theme}): folder={folderRowBounds} item={itemRowBounds}");
                }
                if (active.Name == "CalculatorPanel")
                {
                    // Same text the page shows (MainWindow.UpdateCalculatorStatus); the footer must hold it untrimmed.
                    var footerCount = (TextBlock)count;
                    var footerHint = (TextBlock)window.FindName("ShortcutHintText");
                    footerCount.Text = "Enter 记录并继续 · Ctrl+Enter 复制结果";
                    footerHint.Text = "Esc 清空";
                    var tapeList = (ListBox)window.FindName("CalculatorTapeList");
                    var keypadGrid = (FrameworkElement)window.FindName("CalculatorKeypad");
                    var calcInput = (TextBox)window.FindName("CalculatorExpressionBox");
                    var calcResult = (TextBlock)window.FindName("CalculatorResultText");
                    var resultRun = (System.Windows.Documents.Run)window.FindName("CalculatorResultRun");
                    var copyResult = (Button)window.FindName("CopyCalculatorValueButton");
                    var copyUppercase = (Button)window.FindName("CopyCalculatorUppercaseButton");
                    var modeBox = (ComboBox)window.FindName("ClipboardCalculatorModeBox");
                    calcInput.Text = "(1250+380)/3";
                    resultRun.Text = "= 543.333333333333≈";
                    resultRun.FontWeight = FontWeights.SemiBold;
                    resultRun.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty, "FontSizeTitle");
                    copyResult.IsEnabled = true; copyUppercase.IsEnabled = true;
                    modeBox.SelectedIndex = 1;
                    ((TextBlock)window.FindName("CalculatorTapeCountText")).Text = "最近 4 条";
                    ((FrameworkElement)window.FindName("CalculationEmptyText")).Visibility = Visibility.Collapsed;
                    var allKeys = ((System.Windows.Controls.Primitives.UniformGrid)keypadGrid).Children.OfType<Button>().ToArray();
                    foreach (var keypadOpen in new[] { false, true })
                    {
                        keypadGrid.Visibility = keypadOpen ? Visibility.Visible : Visibility.Collapsed;
                        tapeList.SelectedIndex = tapeList.Items.Count - 1;
                        surface.UpdateLayout();
                        var state = $"{theme} {size} keypad {(keypadOpen ? "open" : "closed")}";
                        if (keypadOpen != keypadGrid.IsVisible)
                            throw new InvalidOperationException($"Keypad visibility did not follow the toggle ({state}).");
                        var checkedElements = new List<(string Name, FrameworkElement Element)>
                        {
                            ("mode", modeBox), ("tape", tapeList), ("input", calcInput), ("result", calcResult), ("uppercase", copyUppercase), ("copy", copyResult)
                        };
                        if (keypadOpen) checkedElements.AddRange(allKeys.Select((key, i) => ($"key{i}", (FrameworkElement)key)));
                        foreach (var (name, element) in checkedElements)
                        {
                            var rect = Bounds(element, surface);
                            if (rect.Left < -0.5 || rect.Right > size.Width + 0.5 || rect.Top < contentBounds.Top - 1 || rect.Bottom > contentBounds.Bottom + 1)
                                throw new InvalidOperationException($"Calculator {name} is clipped ({state}): {rect} in content {contentBounds}");
                        }
                        var inputRect = Bounds(calcInput, surface);
                        var resultRect = Bounds(calcResult, surface);
                        var copyRect = Bounds(copyResult, surface);
                        var uppercaseRect = Bounds(copyUppercase, surface);
                        if (inputRect.Bottom > resultRect.Top + 1 || copyRect.Left < resultRect.Right - 0.5 || uppercaseRect.Left < resultRect.Right - 0.5 || uppercaseRect.Right > copyRect.Left || uppercaseRect.Width < 36 || resultRect.Width < 150 || calcInput.ActualWidth < 150)
                            throw new InvalidOperationException($"Calculator input/result row overlaps or is too narrow ({state}): input={inputRect} result={resultRect} copy={copyRect}");
                        if (Bounds(tapeList, surface).Bottom > inputRect.Top + 8)
                            throw new InvalidOperationException($"Calculator tape runs into the input ({state}).");
                        if (tapeList.ActualHeight < 34)
                            throw new InvalidOperationException($"Calculator tape must keep at least one full row ({state}): {tapeList.ActualHeight} DIP");
                        var tapeRows = Enumerable.Range(0, tapeList.Items.Count)
                            .Select(index => tapeList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)
                            .Where(row => row is not null).Cast<ListBoxItem>().ToArray();
                        if (tapeRows.Length == 0)
                            throw new InvalidOperationException($"Calculator tape realized no rows ({state}).");
                        foreach (var row in tapeRows)
                        {
                            var data = (CalculationResult)row.DataContext;
                            var rowRect = Bounds(row, surface);
                            var rowTexts = Descendants(row).OfType<TextBlock>().ToArray();
                            var expressionBlock = rowTexts.First(text => text.Text == data.Expression);
                            var valueBlock = rowTexts.First(text => text.Text == data.DisplayText);
                            var expressionRect = Bounds(expressionBlock, surface);
                            var valueRect = Bounds(valueBlock, surface);
                            if (valueRect.Right > rowRect.Right + 0.5 || expressionRect.Right > valueRect.Left + 0.5 || valueBlock.ActualHeight > 30 ||
                                expressionBlock.ActualHeight > 22 || valueRect.Right > size.Width + 0.5)
                                throw new InvalidOperationException($"Calculator tape row overlaps ({state}) '{data.Expression}': expression={expressionRect} result={valueRect}");
                            // Actions appear on hover only (never kept invisible-but-focusable). Force the hover state to
                            // measure the worst case: the buttons overlay the expression and must not touch the result.
                            var presenter = Descendants(row).OfType<ContentPresenter>().First();
                            var actions = (FrameworkElement)presenter.ContentTemplate.FindName("Actions", presenter);
                            var rowButtons = Descendants(row).OfType<Button>().ToArray();
                            if (actions.IsVisible || rowButtons.Any(button => button.IsVisible))
                                throw new InvalidOperationException($"Tape actions must stay hidden until the row is hovered ({state}).");
                            actions.Visibility = Visibility.Visible;
                            surface.UpdateLayout();
                            var actionsRect = Bounds(actions, surface);
                            valueRect = Bounds(valueBlock, surface);
                            if (rowButtons.Length != 3 || rowButtons.Any(button => !button.IsVisible) ||
                                actionsRect.Right > valueRect.Left + 0.5 || actionsRect.Left < rowRect.Left || actionsRect.Height > 30 ||
                                valueRect.Left - rowRect.Left < 100)
                                throw new InvalidOperationException($"Hovered tape row must show copy/reuse/delete beside an unclipped result ({state}): actions={actionsRect} result={valueRect}");
                            actions.Visibility = Visibility.Collapsed;
                            surface.UpdateLayout();
                        }
                        if (keypadOpen)
                        {
                            foreach (var key in allKeys)
                            {
                                if (key.ActualHeight < 28 || key.ActualWidth < 48)
                                    throw new InvalidOperationException($"Keypad key too small to hit ({state}): {key.Content} {key.ActualWidth}x{key.ActualHeight}");
                            }
                        }
                        SaveRender(surface, size, Path.Combine(renderDir, $"CalculatorPanel-keypad-{(keypadOpen ? "open" : "closed")}-{size.Width}x{size.Height}.png"));
                    }
                    keypadGrid.Visibility = Visibility.Collapsed;
                    tapeList.SelectedIndex = -1;
                    surface.UpdateLayout();
                    var footerCountRect = Bounds(footerCount, surface);
                    var footerHintRect = Bounds(footerHint, surface);
                    if (footerCount.ActualWidth + 0.5 < NaturalWidth(footerCount) || footerCountRect.Right > footerHintRect.Left)
                        throw new InvalidOperationException($"Calculator footer text is trimmed or overlaps at {size} ({theme}): count={footerCountRect} natural={NaturalWidth(footerCount):F0} hint={footerHintRect}");
                    // Empty tape: the placeholder is centred and the page keeps its layout.
                    var populatedTape = previewContext.Calculations.ToArray();
                    previewContext.Calculations.Clear();
                    ((FrameworkElement)window.FindName("CalculationEmptyText")).Visibility = Visibility.Visible;
                    calcInput.Text = string.Empty;
                    resultRun.Text = "输入算式，例如 (1250+380)/3";
                    resultRun.FontWeight = FontWeights.Normal;
                    resultRun.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty, "FontSizeCaption");
                    copyResult.IsEnabled = false; copyUppercase.IsEnabled = false;
                    surface.UpdateLayout();
                    var emptyTapeRect = Bounds((FrameworkElement)window.FindName("CalculationEmptyText"), surface);
                    if (emptyTapeRect.Width < 1 || emptyTapeRect.Bottom > Bounds(calcInput, surface).Top + 1 || emptyTapeRect.Top < contentBounds.Top - 1)
                        throw new InvalidOperationException($"Empty tape placeholder is clipped at {size} ({theme}): {emptyTapeRect}");
                    SaveRender(surface, size, Path.Combine(renderDir, $"CalculatorPanel-empty-{size.Width}x{size.Height}.png"));
                    foreach (var entry in populatedTape) previewContext.Calculations.Add(entry);
                    ((FrameworkElement)window.FindName("CalculationEmptyText")).Visibility = Visibility.Collapsed;
                    calcInput.Text = "(1250+380)/3";
                    resultRun.Text = "= 543.333333333333≈";
                    resultRun.FontWeight = FontWeights.SemiBold;
                    resultRun.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty, "FontSizeTitle");
                    copyResult.IsEnabled = true; copyUppercase.IsEnabled = true;
                    surface.UpdateLayout();
                }
                if (active.Name == "SharePanel")
                {
                    var footerCount = (TextBlock)count;
                    var footerHint = (TextBlock)window.FindName("ShortcutHintText");
                    var shareStatus = (TextBlock)window.FindName("ShareStatusText");
                    var publishChip = (RadioButton)window.FindName("SharePublishChip");
                    var subscribeChip = (RadioButton)window.FindName("ShareSubscribeChip");
                    var publishScroll = (ScrollViewer)window.FindName("SharePublishScroll");
                    var subscribeScroll = (ScrollViewer)window.FindName("ShareSubscribeScroll");
                    var emptyTexts = new[]
                    {
                        "SharePublishEmptyText", "ShareSubscriberEmptyText", "ShareSubscriptionEmptyText",
                        "SharePublisherEmptyText", "SharePairedEmptyText"
                    }.Select(name => (FrameworkElement)window.FindName(name)).ToArray();
                    shareStatus.Text = "监听中 · 端口 53318";
                    ((TextBlock)window.FindName("SharePairingCodeText")).Text = "482916";
                    ((TextBlock)window.FindName("ShareLocalAddressText")).Text = "本机地址：192.168.100.123:53318、10.0.0.5:53318";
                    footerHint.Text = "仅限局域网";
                    foreach (var segment in new[] { "publish", "subscribe" })
                    foreach (var populated in new[] { true, false })
                    {
                        var publish = segment == "publish";
                        publishChip.IsChecked = publish;
                        subscribeChip.IsChecked = !publish;
                        publishScroll.Visibility = publish ? Visibility.Visible : Visibility.Collapsed;
                        subscribeScroll.Visibility = publish ? Visibility.Collapsed : Visibility.Visible;
                        foreach (var rows in new System.Collections.IList[]
                                 { previewContext.PublishedRows, previewContext.SubscriberRows, previewContext.PublisherRows,
                                   previewContext.PairedPublisherRows, previewContext.SubscriptionRows })
                            rows.Clear();
                        if (populated)
                        {
                            foreach (var row in SamplePublishedRows()) previewContext.PublishedRows.Add(row);
                            foreach (var row in SampleSubscriberRows()) previewContext.SubscriberRows.Add(row);
                            foreach (var row in SamplePublisherRows()) previewContext.PublisherRows.Add(row);
                            foreach (var row in SamplePairedPublisherRows()) previewContext.PairedPublisherRows.Add(row);
                            foreach (var row in SampleSubscriptionRows()) previewContext.SubscriptionRows.Add(row);
                        }
                        foreach (var empty in emptyTexts) empty.Visibility = populated ? Visibility.Collapsed : Visibility.Visible;
                        footerCount.Text = publish ? $"{previewContext.PublishedRows.Count} 项发布" : $"{previewContext.SubscriptionRows.Count} 个订阅";
                        surface.UpdateLayout();
                        var state = $"{theme} {size} {segment} {(populated ? "populated" : "empty")}";
                        var scroll = publish ? publishScroll : subscribeScroll;
                        var viewport = Bounds(scroll, surface);
                        if (!scroll.IsVisible || viewport.Left < -0.5 || viewport.Right > size.Width + 0.5 ||
                            viewport.Top < contentBounds.Top - 1 || viewport.Bottom > contentBounds.Bottom + 1 || viewport.Height < 80)
                            throw new InvalidOperationException($"Share {segment} scroll area is clipped ({state}): {viewport} in content {contentBounds}");
                        // Segment switch row: chips and the status line must not collide.
                        var chipRight = Math.Max(Bounds(publishChip, surface).Right, Bounds(subscribeChip, surface).Right);
                        var statusRect = Bounds(shareStatus, surface);
                        if (statusRect.Left < chipRight - 0.5 || statusRect.Right > size.Width + 0.5 ||
                            shareStatus.ActualWidth + 0.5 < DisplayWidth(shareStatus))
                            throw new InvalidOperationException($"Share status text is trimmed or overlaps the segment chips ({state}): chips<={chipRight:F0} status={statusRect} natural={DisplayWidth(shareStatus):F1} actual={shareStatus.ActualWidth:F1}");
                        var shown = Descendants(scroll).OfType<FrameworkElement>().Where(element => element.IsVisible).ToArray();
                        var buttonsInScroll = shown.OfType<Button>().ToArray();
                        var textsOutsideButtons = shown.OfType<TextBlock>()
                            .Where(block => !buttonsInScroll.Any(button => IsInside(block, button)) && !shown.OfType<TextBox>().Any(box => IsInside(block, box)) && block.ActualWidth > 0).ToArray();
                        foreach (var element in shown.Where(element => element is TextBlock or Button or CheckBox or TextBox))
                        {
                            var rect = Bounds(element, surface);
                            if (rect.Width < 1) continue;
                            if (rect.Left < viewport.Left - 0.5 || rect.Right > viewport.Right + 0.5)
                                throw new InvalidOperationException($"Share element {element.GetType().Name} overflows horizontally ({state}): {rect} in {viewport}");
                        }
                        foreach (var block in textsOutsideButtons.Where(block => block.TextWrapping == TextWrapping.NoWrap && block.TextTrimming == TextTrimming.None))
                            if (block.ActualWidth + 0.5 < DisplayWidth(block))
                                throw new InvalidOperationException($"Share text '{block.Text}' is trimmed without an ellipsis ({state}): {block.ActualWidth:F0} < {DisplayWidth(block):F0}");
                        foreach (var button in buttonsInScroll)
                        {
                            if (button.ActualHeight < 22 || button.ActualWidth < 36)
                                throw new InvalidOperationException($"Share button '{button.Content}' is too small to hit ({state}): {button.ActualWidth:F0}x{button.ActualHeight:F0}");
                            var label = Descendants(button).OfType<TextBlock>().FirstOrDefault();
                            if (label is not null && label.ActualWidth + 0.5 < DisplayWidth(label))
                                throw new InvalidOperationException($"Share button '{button.Content}' label is trimmed ({state}).");
                        }
                        for (var i = 0; i < buttonsInScroll.Length; i++)
                        {
                            var first = Bounds(buttonsInScroll[i], surface);
                            for (var j = i + 1; j < buttonsInScroll.Length; j++)
                            {
                                var overlap = Rect.Intersect(first, Bounds(buttonsInScroll[j], surface));
                                if (!overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5)
                                    throw new InvalidOperationException($"Share buttons overlap ({state}): '{buttonsInScroll[i].Content}' / '{buttonsInScroll[j].Content}'");
                            }
                            foreach (var block in textsOutsideButtons)
                            {
                                var overlap = Rect.Intersect(first, Bounds(block, surface));
                                if (!overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5)
                                    throw new InvalidOperationException($"Share button '{buttonsInScroll[i].Content}' covers text '{block.Text}' ({state}).");
                            }
                        }
                        if (populated)
                        {
                            // Cards stay inside the viewport; a long name is ellipsised, never allowed to widen the card.
                            var cards = shown.OfType<Border>().Where(border => ReferenceEquals(border.Style, app.FindResource("ShareCardStyle"))).ToArray();
                            if (cards.Length < (publish ? 3 : 4))
                                throw new InvalidOperationException($"Share page realized too few cards ({state}): {cards.Length}");
                            foreach (var card in cards)
                            {
                                var cardRect = Bounds(card, surface);
                                if (cardRect.Left < viewport.Left - 0.5 || cardRect.Right > viewport.Right + 0.5)
                                    throw new InvalidOperationException($"Share card is wider than its viewport ({state}): {cardRect} in {viewport}");
                            }
                            if (!publish)
                            {
                                // Status line colour comes from tokens: warning for a problem or pending update, secondary otherwise.
                                var warning = ((SolidColorBrush)app.FindResource("WarningBrush")).Color;
                                var secondary = ((SolidColorBrush)app.FindResource("LabelSecondaryBrush")).Color;
                                var surfaceColor = ((SolidColorBrush)app.FindResource("SurfaceBrush")).Color;
                                foreach (var row in previewContext.SubscriptionRows)
                                {
                                    var statusBlock = textsOutsideButtons.Single(block => block.Text == row.StatusText);
                                    var expected = row.IsProblem ? warning : secondary;
                                    if (((SolidColorBrush)statusBlock.Foreground).Color != expected)
                                        throw new InvalidOperationException($"Subscription status '{row.StatusText}' must use the {(row.IsProblem ? "warning" : "secondary")} token ({state}).");
                                    if (Contrast(expected, surfaceColor) < 3.0)
                                        throw new InvalidOperationException($"Subscription status colour is hard to read on the card ({state}): contrast {Contrast(expected, surfaceColor):F2}");
                                }
                                var updateButtons = buttonsInScroll.Where(button => Equals(button.Content, "更新")).ToArray();
                                if (updateButtons.Length != previewContext.SubscriptionRows.Count(row => row.HasNewer))
                                    throw new InvalidOperationException($"The update button must show only on subscriptions with a newer version ({state}): {updateButtons.Length}");
                                var rollbackButtons = buttonsInScroll.Where(button => Equals(button.Content, "回退")).ToArray();
                                if (rollbackButtons.Count(button => button.IsEnabled) != previewContext.SubscriptionRows.Count(row => row.CanRollback))
                                    throw new InvalidOperationException($"Rollback must be enabled only where a snapshot exists ({state}).");
                            }
                        }
                        else
                        {
                            var visibleEmpty = emptyTexts.Where(empty => scroll.IsAncestorOf(empty)).ToArray();
                            if (visibleEmpty.Length == 0 || visibleEmpty.Any(empty => !empty.IsVisible || Bounds(empty, surface).Right > viewport.Right + 0.5))
                                throw new InvalidOperationException($"Share empty-state hints must be visible and unclipped ({state}).");
                        }
                        if (segment == "publish")
                        {
                            var code = (TextBlock)window.FindName("SharePairingCodeText");
                            var issue = (Button)window.FindName("ShareIssueCodeButton");
                            var firewall = (Button)window.FindName("ShareCopyFirewallButton");
                            if (Bounds(code, surface).Right > Bounds(issue, surface).Left + 0.5 || code.ActualWidth + 0.5 < DisplayWidth(code))
                                throw new InvalidOperationException($"Pairing code collides with its button ({state}).");
                            if (!firewall.IsVisible || Bounds(firewall, surface).Right > viewport.Right + 0.5)
                                throw new InvalidOperationException($"The firewall hint button must stay reachable ({state}).");
                        }
                        else
                        {
                            var address = (TextBox)window.FindName("ShareManualAddressBox");
                            var connect = (Button)window.FindName("ShareManualConnectButton");
                            if (address.ActualWidth < 120 || Bounds(address, surface).Right > Bounds(connect, surface).Left + 0.5)
                                throw new InvalidOperationException($"Manual address box is too narrow or overlaps its button ({state}): {address.ActualWidth:F0}");
                            // The placeholder (the box's Tag) is drawn by the template and must fit inside the box.
                            var hint = Descendants(address).OfType<TextBlock>().FirstOrDefault(block => block.Text == (string)address.Tag);
                            if (hint is null || Bounds(hint, surface).Right > Bounds(address, surface).Right - 4 || hint.ActualWidth + 0.5 < DisplayWidth(hint))
                                throw new InvalidOperationException($"Manual address placeholder does not fit its box ({state}): {(hint is null ? "missing" : $"{hint.ActualWidth:F0} of {address.ActualWidth:F0}")}");
                        }
                        surface.UpdateLayout();
                        var footerCountRect = Bounds(footerCount, surface);
                        var footerHintRect = Bounds(footerHint, surface);
                        if (footerCount.ActualWidth + 0.5 < DisplayWidth(footerCount) || footerCountRect.Right > footerHintRect.Left ||
                            footerHint.ActualWidth + 0.5 < DisplayWidth(footerHint))
                            throw new InvalidOperationException($"Share footer text is trimmed or overlaps ({state}): count={footerCountRect} hint={footerHintRect}");
                        SaveRender(surface, size, Path.Combine(renderDir, $"SharePanel-{segment}-{(populated ? "populated" : "empty")}-{size.Width}x{size.Height}.png"));
                    }
                    // Leave the default state (publish segment, populated) for the shared render below.
                    publishChip.IsChecked = true;
                    subscribeChip.IsChecked = false;
                    publishScroll.Visibility = Visibility.Visible;
                    subscribeScroll.Visibility = Visibility.Collapsed;
                    foreach (var row in SamplePublishedRows()) previewContext.PublishedRows.Add(row);
                    foreach (var row in SampleSubscriberRows()) previewContext.SubscriberRows.Add(row);
                    foreach (var row in SamplePublisherRows()) previewContext.PublisherRows.Add(row);
                    foreach (var row in SamplePairedPublisherRows()) previewContext.PairedPublisherRows.Add(row);
                    foreach (var row in SampleSubscriptionRows()) previewContext.SubscriptionRows.Add(row);
                    foreach (var empty in emptyTexts) empty.Visibility = Visibility.Collapsed;
                    footerCount.Text = $"{previewContext.PublishedRows.Count} 项发布";
                    surface.UpdateLayout();
                }
                Console.WriteLine($"PASS {theme} {active.Name} {size}: content={contentBounds}, footerY={footerBounds.Top:F0}");
                navigation.SelectedItem = active.Name switch
                {
                    "TodoPanel" => window.FindName("TodoTab"),
                    "ConfigPanel" => window.FindName("PersonalSpaceTab"),
                    "CalculatorPanel" => window.FindName("CalculatorTab"),
                    "SharePanel" => window.FindName("ShareTab"),
                    _ => window.FindName("HistoryTab")
                };
                count.SetCurrentValue(TextBlock.TextProperty, active.Name switch
                {
                    "TodoPanel" => "0 项待办",
                    "ConfigPanel" => "0 项配置",
                    "CalculatorPanel" => "Enter 记录并继续 · Ctrl+Enter 复制结果",
                    "SharePanel" => $"{previewContext.PublishedRows.Count} 项发布",
                    _ => "5 条历史"
                });
                ((TextBlock)window.FindName("ShortcutHintText")).Text = active.Name switch
                {
                    "TodoPanel" => "本机待办",
                    "CalculatorPanel" => "Esc 清空",
                    "SharePanel" => "仅限局域网",
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
                    window.DataContext = new PreviewContext { Items = Array.Empty<ClipboardManager.Windows.HistoryItemViewModel>() };
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
                    // Hint mode: a short single-line expression carries an "= result" pill at the row end.
                    // Worst cases: a very long result and a long expression must not push the row past 56 DIP,
                    // wrap, or clip; plain text rows must not show a pill.
                    var hintAt = DateTime.Today.AddHours(15);
                    var hintRows = new[]
                    {
                        new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "(1250+380)/3", "记事本", hintAt), true),
                        new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "本周产品检查清单", "记事本", hintAt.AddMinutes(-1)), true),
                        new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "123456789×987654321+(1250+380)/3-1000000×3", "Excel", hintAt.AddMinutes(-2)), true),
                        new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "200+10%", "微信", hintAt.AddMinutes(-3)), true),
                        new ClipboardManager.Windows.HistoryItemViewModel(SampleItem(ClipboardItemType.Text, "1,250×2", "微信", hintAt.AddMinutes(-4)), true)
                    };
                    if (hintRows.Select(row => row.Calculation is not null).ToArray() is not [true, false, true, true, true])
                        throw new InvalidOperationException("Hint rows must carry a calculation only for calculable single-line expressions.");
                    if (hintRows[0].CalculationHintText != "= 543.33")
                        throw new InvalidOperationException($"History hint must read '= 543.33' for (1250+380)/3, got '{hintRows[0].CalculationHintText}'.");
                    window.DataContext = new PreviewContext { Items = hintRows };
                    var hintList = (ListBox)window.FindName("HistoryList");
                    hintList.SelectedIndex = 0;
                    ((TextBlock)window.FindName("HistoryCountText")).Text = $"{hintRows.Length} 条";
                    surface.UpdateLayout();
                    var hintRealized = Enumerable.Range(0, hintList.Items.Count)
                        .Select(index => hintList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)
                        .Where(container => container is not null).Cast<ListBoxItem>().ToArray();
                    if (hintRealized.Length < 2)
                        throw new InvalidOperationException($"Hint rows must be realized at {size}: {hintRealized.Length}");
                    foreach (var container in hintRealized)
                    {
                        var data = (ClipboardManager.Windows.HistoryItemViewModel)container.DataContext;
                        var rowRect = Bounds(container, surface);
                        var texts = Descendants(container).OfType<TextBlock>().ToArray();
                        var preview = texts.First(text => text.Text == data.PreviewText);
                        var pill = texts.FirstOrDefault(text => text.Text == data.CalculationHintText && text.Text.StartsWith("= "));
                        if (Math.Abs(container.ActualHeight - 56) > 0.5 || preview.ActualHeight > 22)
                            throw new InvalidOperationException($"Hint row must keep a 56 DIP pitch and a one-line preview at {size} ({theme}): {container.ActualHeight}/{preview.ActualHeight}");
                        if (data.Calculation is null)
                        {
                            if (pill is not null && pill.IsVisible)
                                throw new InvalidOperationException($"Plain text row must not show a result pill ({theme}, {size}).");
                            continue;
                        }
                        if (pill is null || !pill.IsVisible)
                            throw new InvalidOperationException($"Calculable row '{data.PreviewText}' must show its result pill ({theme}, {size}).");
                        var pillRect = Bounds(pill, surface);
                        var previewRect = Bounds(preview, surface);
                        if (pill.ActualHeight > 20 || pillRect.Right > rowRect.Right - 4 || pillRect.Left < previewRect.Left ||
                            previewRect.Right > pillRect.Left + 0.5 || previewRect.Width < 48)
                            throw new InvalidOperationException($"Result pill is clipped or collides with the preview at {size} ({theme}): row={rowRect} preview={previewRect} pill={pillRect}");
                    }
                    SaveRender(surface, size, Path.Combine(renderDir, $"HistoryPanel-hint-{size.Width}x{size.Height}.png"));
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

    /// <summary>Fill of the container's template root once selected (the colour a row paints, not the token).</summary>
    private static Color SelectedBackground(ListBoxItem row) =>
        ((SolidColorBrush)((Border)row.Template.FindName("Root", row)).Background).Color;

    private static Rect Bounds(FrameworkElement element, FrameworkElement surface) =>
        element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));

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

    private sealed class PreviewContext
    {
        public Thickness RowMargin { get; set; } = new(10, 9, 10, 9);
        public double ThumbnailSize { get; set; } = 34d;
        public double HistoryRowHeight { get; set; } = 56d;
        public double HistoryIconSize { get; set; } = 30d;
        public Thickness HistoryRowPadding { get; set; } = new(10, 0, 10, 0);
        public IReadOnlyList<ClipboardManager.Windows.HistoryItemViewModel> Items { get; set; } = [];
        public IReadOnlyList<ClipboardManager.Windows.FolderViewModel> Folders { get; set; } = [];
        public IReadOnlyList<ClipboardManager.Windows.CollectionItemViewModel> CollectionItems { get; set; } = [];
        public ClipboardManager.Windows.FolderViewModel? SelectedFolder { get; set; }
        public ClipboardManager.Windows.CollectionItemViewModel? SelectedCollectionItem { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<CalculationResult> Calculations { get; set; } = [];
        public System.Collections.ObjectModel.ObservableCollection<ClipboardManager.Windows.PublishedRowViewModel> PublishedRows { get; set; } = [];
        public System.Collections.ObjectModel.ObservableCollection<ClipboardManager.Windows.SubscriberRowViewModel> SubscriberRows { get; set; } = [];
        public System.Collections.ObjectModel.ObservableCollection<ClipboardManager.Windows.PublisherRowViewModel> PublisherRows { get; set; } = [];
        public System.Collections.ObjectModel.ObservableCollection<ClipboardManager.Windows.PairedPublisherRowViewModel> PairedPublisherRows { get; set; } = [];
        public System.Collections.ObjectModel.ObservableCollection<ClipboardManager.Windows.SubscriptionRowViewModel> SubscriptionRows { get; set; } = [];
    }

    private const string SampleFingerprint = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    private static PairedPeer SamplePeer(PeerRole role, string name, string? address) =>
        new(Guid.NewGuid(), role, name, SampleFingerprint, address, DateTimeOffset.Now.AddDays(-2), PeerStatus.Active);

    private static ClipboardManager.Windows.PublishedRowViewModel[] SamplePublishedRows() =>
    [
        new(SamplePublished("日常工作配置", 3, false, allFolders: false), "常用话术", 12),
        new(SamplePublished("一个名字特别特别长的发布配置名称用来验证省略号不会撑开行宽", 128, true, allFolders: true), "全部配置 · 同样很长很长的文件夹名称示例等 128 个文件夹", 1200)
    ];

    private static PublishedConfig SamplePublished(string name, int version, bool auto, bool allFolders) =>
        new(Guid.NewGuid(), Guid.NewGuid(), allFolders ? [] : [Guid.NewGuid()], allFolders, name, version, "hash", "pkg", 1024, "{}", auto,
            DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddHours(-3));

    private static ClipboardManager.Windows.SubscriberRowViewModel[] SampleSubscriberRows() =>
    [
        new(SamplePeer(PeerRole.Subscriber, "同事的笔记本电脑", null)),
        new(SamplePeer(PeerRole.Subscriber, "一台设备名称非常非常长的办公室台式机用来验证省略号", null))
    ];

    private static ClipboardManager.Windows.PublisherRowViewModel[] SamplePublisherRows() =>
    [
        new(new DiscoveredPublisher("办公室电脑", "192.168.100.123:53318", "A1B2C3D4E5F6", 1,
            [new PublicationAnnouncement("日常工作配置", 3), new PublicationAnnouncement("客服话术", 12)], DateTimeOffset.Now),
            SamplePeer(PeerRole.Publisher, "办公室电脑", "192.168.100.123:53318")),
        new(new DiscoveredPublisher("一台设备名称非常非常长的家用台式机用来验证省略号", "192.168.100.231:53318", "0F9E8D7C6B5A", 1,
            [new PublicationAnnouncement("一个名字特别特别长的发布配置名称用来验证省略号不会撑开行宽", 128)], DateTimeOffset.Now), null)
    ];

    private static ClipboardManager.Windows.PairedPublisherRowViewModel[] SamplePairedPublisherRows() =>
    [
        new(SamplePeer(PeerRole.Publisher, "办公室电脑", "192.168.100.123:53318")),
        new(SamplePeer(PeerRole.Publisher, "一台设备名称非常非常长的家用台式机用来验证省略号", null))
    ];

    private static ClipboardManager.Windows.SubscriptionRowViewModel[] SampleSubscriptionRows() =>
    [
        new(SampleSubscription("日常工作配置", 3, 3, null, false, false), "办公室电脑", true),
        new(SampleSubscription("一个名字特别特别长的发布配置名称用来验证省略号不会撑开行宽", 2, 4, null, true, false), "一台设备名称非常非常长的家用台式机", true),
        new(SampleSubscription("客服话术", 1, 1, "无法连接发布者：连接超时。请确认对方在线，并在对方电脑的 Windows 防火墙中放行 TCP/UDP 53318。", false, false), "办公室电脑", false),
        new(SampleSubscription("会议模板", 5, 6, null, false, true), "办公室电脑", true)
    ];

    private static ConfigSubscription SampleSubscription(string name, int local, int latest, string? error, bool auto, bool stay) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, local, Guid.NewGuid(), auto, stay, null,
            DateTimeOffset.Now.AddMinutes(-12), latest, error, "{}", "hash", DateTimeOffset.Now.AddDays(-3), DateTimeOffset.Now.AddMinutes(-12));

    private static bool IsInside(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    /// <summary>WCAG contrast ratio between two opaque colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Unwrapped width in WPF's Display text mode (what the page renders with), to detect trimming.</summary>
    private static double DisplayWidth(TextBlock block) => new FormattedText(block.Text, System.Globalization.CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize,
        Brushes.Black, null, TextFormattingMode.Display, 1.0).WidthIncludingTrailingWhitespace;

    /// <summary>Unwrapped width of the text on one line, to detect trimming.</summary>
    private static double NaturalWidth(TextBlock block) => new FormattedText(block.Text, System.Globalization.CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize,
        Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

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
