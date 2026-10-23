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
            var separator = historyList.Descendants(presentation + "Border")
                .Single(element => element.Attribute("Grid.Row")?.Value == "2" &&
                                   element.Attribute("Height")?.Value == "1");
            var separatorRow = separator.Parent!
                .Element(presentation + "Grid.RowDefinitions")!
                .Elements(presentation + "RowDefinition")
                .ElementAt(2);
            if (separatorRow.Attribute("Height")?.Value != "Auto")
                throw new InvalidOperationException("History separator must use an auto-sized row so its top margin cannot consume the 1px line.");
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
            var pet = new ClipboardManager.Windows.Services.PetCompanion { Width = 48, Height = 52 };
            pet.SetListening(true);
            var petHost = new Border { Width = 48, Height = 52, Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xF9, 0xF5)), Child = pet };
            // Unrooted host: manual measure/arrange, UpdateLayout alone renders nothing.
            petHost.Measure(new Size(48, 52));
            petHost.Arrange(new Rect(0, 0, 48, 52));
            petHost.UpdateLayout();
            var petCheck = new RenderTargetBitmap(48, 52, 96, 96, PixelFormats.Pbgra32);
            petCheck.Render(petHost);
            Directory.CreateDirectory(Path.Combine(root, "artifacts", "review", "0.8.2", "layout"));
            SavePng(petCheck, Path.Combine(root, "artifacts", "review", "0.8.2", "layout", "PanelPet-probe.png"));
            var petBytes = new byte[48 * 52 * 4];
            petCheck.CopyPixels(petBytes, 48 * 4, 0);
            if (ClipboardManager.Windows.Services.PetCompanion.LoadError is not null)
                throw new InvalidOperationException($"Pet frame load failed: {ClipboardManager.Windows.Services.PetCompanion.LoadError.GetType().Name}: {ClipboardManager.Windows.Services.PetCompanion.LoadError.Message} | {ClipboardManager.Windows.Services.PetCompanion.LoadError}");
            if (petBytes.All(b => b == 0))
                throw new InvalidOperationException("The panel pet rendered an empty frame; pack URI resource resolution failed in this host.");
            if (pet.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The panel pet must stay visible when its frames load.");
            var output = Path.Combine(root, "artifacts", "review", "0.8.2", "layout");
            Directory.CreateDirectory(output);
            window.DataContext = new
            {
                RowMargin = new Thickness(10, 9, 10, 9),
                ThumbnailSize = 34d,
                PreviewMaxHeight = 36d,
                HistorySeparatorMargin = new Thickness(0, 8, 0, 0),
                Items = new[]
                {
                    new PreviewRow("本周产品检查清单", "文本 · 记事本 · 今天 14:32"),
                    new PreviewRow("本地优先，让每一次复制都安心。", "文本 · 微信 · 今天 14:29"),
                    new PreviewRow("发布检查记录.xlsx", "文件 · Excel · 今天 14:04"),
                    new PreviewRow("https://clipboard.netkaize.com", "文本 · Edge · 今天 13:32"),
                    new PreviewRow("长文本示例：核对搜索、配置、待办、计算与划词的完整操作路径。", "文本 · 记事本 · 今天 13:10")
                }
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
            ((TextBlock)window.FindName("EmptyStateText")).Visibility = Visibility.Collapsed;
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
            foreach (var size in new[] { new Size(340, 540), new Size(340, 450), new Size(520, 700) })
            foreach (var active in panels)
            {
                foreach (var panel in panels)
                    panel.Visibility = panel == active ? Visibility.Visible : Visibility.Collapsed;
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
                var footerBounds = Bounds(count, surface);
                var hintBounds = Bounds((FrameworkElement)window.FindName("ShortcutHintText"), surface);
                if (footerBounds.Left < 0 || footerBounds.Right > hintBounds.Left ||
                    hintBounds.Right > size.Width || hintBounds.Width < 1)
                    throw new InvalidOperationException($"Footer count and shortcut overlap: {footerBounds}, {hintBounds}");
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
                    var lines = Descendants(active).OfType<System.Windows.Shapes.Line>().ToArray();
                    if (lines.Length != 5 || lines.Any(line => line.ActualWidth < 1 || line.ActualHeight < 1))
                        throw new InvalidOperationException("Todo summary dashed separators have invalid rendered dimensions.");
                }
                if (tabBounds.Top < titleBounds.Bottom - 1 ||
                    contentBounds.Top < tabBounds.Bottom - 1 ||
                    contentBounds.Bottom > footerBounds.Top + 1 ||
                    contentBounds.Height < 100 || footerBounds.Top < size.Height - 85 ||
                    listeningBounds.Right > size.Width || settingsBounds.Right > size.Width)
                    throw new InvalidOperationException(
                        $"{active.Name} {size}: title={titleBounds}, listening={listeningBounds}, settings={settingsBounds}, tabs={tabBounds}, content={contentBounds}, footer={footerBounds}");
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
                Console.WriteLine($"PASS {active.Name} {size}: content={contentBounds}, footerY={footerBounds.Top:F0}");
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
                SaveRender(surface, size, Path.Combine(output, $"{active.Name}-{size.Width}x{size.Height}.png"));
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

    public sealed record PreviewRow(string PreviewText, string Metadata)
    {
        public Visibility PreviewVisibility => Visibility.Collapsed;
        public ImageSource? Preview => null;
        public Stretch PreviewStretch => Stretch.Uniform;
        public Thickness PreviewPadding => default;
        public TextWrapping PreviewWrapping => TextWrapping.Wrap;
        public string FavoriteMark => "";
        public string PinnedMark => "";
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
