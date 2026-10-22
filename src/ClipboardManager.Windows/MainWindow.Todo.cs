using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace ClipboardManager.Windows;

internal enum TodoSummaryDrilldown
{
    None,
    Completed,
    Pending,
    Created,
    Rate
}

public partial class MainWindow
{
    private TodoListFilter _todoFilter = TodoListFilter.All;
    private TodoStatsPeriod _todoStatsPeriod = TodoStatsPeriod.All;
    private TodoSummaryDrilldown _todoSummaryDrilldown = TodoSummaryDrilldown.None;
    private Guid? _todoTagFilterId;
    private string _todoSearchText = string.Empty;
    private readonly HashSet<Guid> _todoDraftTagIds = [];
    private bool _todoUiBusy;

    public ObservableCollection<TodoItemViewModel> TodoItems { get; } = [];
    public ObservableCollection<TodoTagChoiceViewModel> TodoTagChoices { get; } = [];
    public ObservableCollection<TodoTagStatViewModel> TodoTagStats { get; } = [];
    public TodoItemViewModel? SelectedTodoItem
    {
        get => _selectedTodoItem;
        set { _selectedTodoItem = value; Changed(nameof(SelectedTodoItem)); }
    }

    private TodoItemViewModel? _selectedTodoItem;

    private async Task RefreshTodosAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _todoService.GetSnapshotAsync(_todoFilter, _todoTagFilterId, cancellationToken);
        var statsSnapshot = _todoFilter == TodoListFilter.All && _todoTagFilterId is null
            ? snapshot
            : await _todoService.GetSnapshotAsync(TodoListFilter.All, null, cancellationToken);
        if (_todoTagFilterId is { } filter && snapshot.Tags.All(tag => tag.Id != filter))
            _todoTagFilterId = null;

        var selectedId = SelectedTodoItem?.Item.Id;
        TodoItems.Clear();
        var visibleItems = ApplyTodoSearch(snapshot.Items);
        if (_todoTagFilterId is { } selectedTag)
            visibleItems = visibleItems.Where(item => item.TagIds.Contains(selectedTag));
        foreach (var item in visibleItems)
            TodoItems.Add(new TodoItemViewModel(item, snapshot.Tags));
        SelectedTodoItem = TodoItems.FirstOrDefault(item => item.Item.Id == selectedId) ?? TodoItems.FirstOrDefault();

        TodoTagChoices.Clear();
        foreach (var tag in snapshot.Tags)
            TodoTagChoices.Add(new TodoTagChoiceViewModel(tag, _todoDraftTagIds.Contains(tag.Id)));

        TodoTagStats.Clear();
        foreach (var stat in snapshot.TagStats)
            TodoTagStats.Add(new TodoTagStatViewModel(stat, _todoTagFilterId == stat.Tag.Id));

        var now = DateTimeOffset.Now;
        TodoDateText.Text = now.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        var periodStart = StatsPeriodStart(now, _todoStatsPeriod);
        var allItems = statsSnapshot.Items;
        var completedItems = allItems.Where(item => item.IsCompleted &&
            (periodStart is null || item.CompletedAt >= periodStart)).ToList();
        var createdItems = allItems.Where(item => periodStart is null || item.CreatedAt >= periodStart).ToList();
        var pendingItems = createdItems.Where(item => !item.IsCompleted).ToList();
        var completedCreatedItems = createdItems.Where(item => item.IsCompleted).ToList();
        if (_todoSummaryDrilldown != TodoSummaryDrilldown.None)
        {
            IEnumerable<TodoItem> drilled = _todoSummaryDrilldown switch
            {
                TodoSummaryDrilldown.Completed => completedItems,
                TodoSummaryDrilldown.Pending => pendingItems,
                TodoSummaryDrilldown.Created => createdItems,
                TodoSummaryDrilldown.Rate => createdItems,
                _ => createdItems
            };
            TodoItems.Clear();
            foreach (var item in ApplyTodoSearch(drilled).OrderBy(item => item.IsCompleted).ThenByDescending(item => item.CreatedAt))
                TodoItems.Add(new TodoItemViewModel(item, statsSnapshot.Tags));
            SelectedTodoItem = TodoItems.FirstOrDefault();
        }
        TodoCompletedSummaryButton.IsChecked = _todoSummaryDrilldown == TodoSummaryDrilldown.Completed;
        TodoPendingSummaryButton.IsChecked = _todoSummaryDrilldown == TodoSummaryDrilldown.Pending;
        TodoCreatedSummaryButton.IsChecked = _todoSummaryDrilldown == TodoSummaryDrilldown.Created;
        TodoRateSummaryButton.IsChecked = _todoSummaryDrilldown == TodoSummaryDrilldown.Rate;
        TodoCompletedLabel.Text = _todoStatsPeriod switch { TodoStatsPeriod.Week => "本周完成", TodoStatsPeriod.Month => "本月完成", TodoStatsPeriod.Year => "今年完成", _ => "完成" };
        TodoPendingLabel.Text = "待完成";
        TodoCreatedLabel.Text = _todoStatsPeriod switch { TodoStatsPeriod.Week => "本周新增", TodoStatsPeriod.Month => "本月新增", TodoStatsPeriod.Year => "今年新增", _ => "新增" };
        TodoRateLabel.Text = "完成率";
        TodoTodayCompletedText.Text = completedItems.Count.ToString(CultureInfo.InvariantCulture);
        TodoPendingText.Text = pendingItems.Count.ToString(CultureInfo.InvariantCulture);
        TodoTodayCreatedText.Text = createdItems.Count.ToString(CultureInfo.InvariantCulture);
        TodoCompletionRateText.Text = $"{(createdItems.Count == 0 ? 0 : Math.Round(completedCreatedItems.Count * 100d / createdItems.Count, 1)).ToString("0.#", CultureInfo.InvariantCulture)}%";
        UpdateTodoEmptyState();
        if (IsTodoTabActive) CountText.Text = $"{snapshot.Summary.Pending} 项待完成 · 共 {snapshot.Summary.Total} 项";

    }

    private void UpdateTodoEmptyState()
    {
        TodoEmptyStateText.Text = _todoFilter == TodoListFilter.All && _todoTagFilterId is null && string.IsNullOrWhiteSpace(_todoSearchText)
            ? "还没有待办，请在下方添加一条"
            : "没有符合当前筛选的待办";
        TodoEmptyStateText.Visibility = TodoItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTodoLayout()
    {
        TodoSideGapColumn.Width = new GridLength(0);
        TodoSideColumn.Width = new GridLength(0);
    }

    private async void AddTodoButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("创建待办", CreateTodoFromInputAsync);

    private async void TodoTitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunActionAsync("创建待办", CreateTodoFromInputAsync);
    }

    private async Task CreateTodoFromInputAsync()
    {
        var title = TodoTitleBox.Text;
        if (string.IsNullOrWhiteSpace(title)) return;
        await _todoService.CreateAsync(title, _todoDraftTagIds.ToArray());
        TodoTitleBox.Clear();
        await RefreshTodosAsync();
        TodoTitleBox.Focus();
    }

    private void TodoItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid { DataContext: TodoItemViewModel item } grid) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = grid };
        var copy = new MenuItem { Header = "复制待办", InputGestureText = "Ctrl+C" };
        copy.Click += async (_, _) => await CopyTodoItemAsync(item);
        var edit = new MenuItem { Header = "编辑待办" };
        edit.Click += async (_, _) => await EditTodoItemAsync(item);
        var delete = new MenuItem { Header = "删除待办" };
        delete.Click += async (_, _) => await DeleteTodoItemAsync(item);
        menu.Items.Add(copy);
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private Task CopyTodoItemAsync(TodoItemViewModel item) =>
        RunActionAsync("复制待办", async () =>
        {
            await _restoreService.CopyTextAsync(item.Item.Title);
            CountText.Text = "待办内容已复制";
        });

    private async Task EditTodoItemAsync(TodoItemViewModel item)
    {
        var snapshot = await _todoService.GetSnapshotAsync(TodoListFilter.All, null);
        var dialog = new TodoEditDialog(this, item.Item, snapshot.Tags);
        if (WithModal(() => dialog.ShowDialog()) != true) return;
        await RunActionAsync("编辑待办", async () =>
        {
            await _todoService.UpdateAsync(item.Item.Id, dialog.TodoTitle, dialog.SelectedTagIds);
            await RefreshTodosAsync();
        });
    }

    private async Task DeleteTodoItemAsync(TodoItemViewModel item)
    {
        if (MessageBox.Show(this, $"确定删除待办「{item.Title}」吗？", "删除待办",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunActionAsync("删除待办", async () =>
        {
            await _todoService.DeleteAsync(item.Item.Id);
            await RefreshTodosAsync();
        });
    }

    private async void TodoCompletedCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TodoItemViewModel item }) return;
        e.Handled = true;
        await RunActionAsync("更新待办", async () =>
        {
            await _todoService.SetCompletedAsync(item.Item.Id, !item.IsCompleted);
            await RefreshTodosAsync();
        });
    }

    private async void TodoCompletedSummaryButton_Click(object sender, RoutedEventArgs e) =>
        await SetTodoSummaryDrilldownAsync(TodoSummaryDrilldown.Completed);

    private async void TodoPendingSummaryButton_Click(object sender, RoutedEventArgs e) =>
        await SetTodoSummaryDrilldownAsync(TodoSummaryDrilldown.Pending);

    private async void TodoCreatedSummaryButton_Click(object sender, RoutedEventArgs e) =>
        await SetTodoSummaryDrilldownAsync(TodoSummaryDrilldown.Created);

    private async void TodoRateSummaryButton_Click(object sender, RoutedEventArgs e) =>
        await SetTodoSummaryDrilldownAsync(TodoSummaryDrilldown.Rate);

    private async Task SetTodoSummaryDrilldownAsync(TodoSummaryDrilldown drilldown)
    {
        if (_todoUiBusy) return;
        _todoSummaryDrilldown = _todoSummaryDrilldown == drilldown ? TodoSummaryDrilldown.None : drilldown;
        await RunActionAsync("筛选统计待办", () => RefreshTodosAsync());
    }

    private async void TodoMonthStatsButton_Click(object sender, RoutedEventArgs e) =>
        await SetTodoStatsPeriodAsync(TodoStatsPeriod.Month);

    private async void TodoYearStatsButton_Click(object sender, RoutedEventArgs e) =>
        await SetTodoStatsPeriodAsync(TodoStatsPeriod.Year);

    private async void TodoAllStatsButton_Click(object sender, RoutedEventArgs e)
    {
        _todoFilter = TodoListFilter.All;
        await SetTodoStatsPeriodAsync(TodoStatsPeriod.All);
    }

    private async Task SetTodoStatsPeriodAsync(TodoStatsPeriod period)
    {
        if (_todoUiBusy) return;
        _todoStatsPeriod = period;
        _todoFilter = TodoListFilter.All;
        _todoSummaryDrilldown = TodoSummaryDrilldown.None;
        await RunActionAsync("切换待办统计周期", () => RefreshTodosAsync());
    }

    private async void ExportTodoDetailsButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("导出待办明细", async () =>
        {
            var snapshot = await _todoService.GetSnapshotAsync(TodoListFilter.All, null);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出待办明细",
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"待办明细-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            var tagMap = snapshot.Tags.ToDictionary(tag => tag.Id, tag => tag.Name);
            var csv = new StringBuilder("状态,待办,标签,创建时间,完成时间\r\n");
            foreach (var item in snapshot.Items.OrderBy(item => item.CreatedAt))
            {
                var tags = string.Join(";", item.TagIds.Where(tagMap.ContainsKey).Select(id => tagMap[id]));
                csv.Append(Csv(item.IsCompleted ? "已完成" : "进行中")).Append(',')
                    .Append(Csv(item.Title)).Append(',').Append(Csv(tags)).Append(',')
                    .Append(Csv(item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                    .Append(Csv(item.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty))
                    .Append("\r\n");
            }
            await File.WriteAllTextAsync(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            MessageBox.Show(this, $"已导出 {snapshot.Items.Count} 条待办。", "导出待办明细",
                MessageBoxButton.OK, MessageBoxImage.Information);
        });

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static DateTimeOffset? StatsPeriodStart(DateTimeOffset now, TodoStatsPeriod period)
    {
        if (period == TodoStatsPeriod.All) return null;
        var local = now.ToLocalTime();
        if (period == TodoStatsPeriod.Year)
            return new DateTimeOffset(local.Year, 1, 1, 0, 0, 0, local.Offset);
        if (period == TodoStatsPeriod.Month)
            return new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, local.Offset);

        var daysFromMonday = ((int)local.DayOfWeek + 6) % 7;
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset)
            .AddDays(-daysFromMonday);
    }

    private async void TodoSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_todoUiBusy) return;
        _todoSearchText = TodoSearchBox.Text.Trim();
        await RunActionAsync("搜索待办", () => RefreshTodosAsync());
    }

    private IEnumerable<TodoItem> ApplyTodoSearch(IEnumerable<TodoItem> items) =>
        string.IsNullOrWhiteSpace(_todoSearchText)
            ? items
            : items.Where(item => item.Title.Contains(_todoSearchText, StringComparison.CurrentCultureIgnoreCase));

    private void TodoList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<CheckBox>(source) is not null ||
            e.OriginalSource is DependencyObject buttonSource && FindAncestor<Button>(buttonSource) is not null) return;
        var item = e.OriginalSource is DependencyObject origin ? FindAncestor<ListBoxItem>(origin) : null;
        if (item?.DataContext is not TodoItemViewModel selected) return;
        var created = selected.Item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var completed = selected.Item.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未完成";
        MessageBox.Show(this, $"创建时间：{created}\n完成时间：{completed}", selected.Title,
            MessageBoxButton.OK, MessageBoxImage.Information);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private async void TodoActiveFilterButton_Click(object sender, RoutedEventArgs e) =>
        await SetTodoFilterAsync(TodoListFilter.Active);

    private async void TodoCompletedFilterButton_Click(object sender, RoutedEventArgs e) =>
        await SetTodoFilterAsync(TodoListFilter.Completed);

    private async Task SetTodoFilterAsync(TodoListFilter filter)
    {
        if (_todoUiBusy) return;
        _todoFilter = filter;
        _todoStatsPeriod = TodoStatsPeriod.All;
        _todoSummaryDrilldown = TodoSummaryDrilldown.None;
        await RunActionAsync("筛选待办", () => RefreshTodosAsync());
    }

    private async void TodoTagStatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TodoTagStatViewModel stat }) return;
        _todoTagFilterId = _todoTagFilterId == stat.Tag.Id ? null : stat.Tag.Id;
        await RunActionAsync("筛选标签", () => RefreshTodosAsync());
    }

    private async void TodoTagChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: TodoTagChoiceViewModel choice }) return;
        if (choice.IsSelected) _todoDraftTagIds.Add(choice.Tag.Id);
        else _todoDraftTagIds.Remove(choice.Tag.Id);
        _todoTagFilterId = choice.IsSelected ? choice.Tag.Id : null;
        _todoSummaryDrilldown = TodoSummaryDrilldown.None;
        await RunActionAsync("按标签筛选待办", () => RefreshTodosAsync());
    }

    private void TodoTagChoice_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: TodoTagChoiceViewModel choice } button) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = button };
        var edit = new MenuItem { Header = "编辑名称和颜色" };
        edit.Click += async (_, _) => await EditTodoTagAsync(choice);
        var delete = new MenuItem { Header = "删除标签" };
        delete.Click += async (_, _) => await DeleteTodoTagAsync(choice);
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private async Task EditTodoTagAsync(TodoTagChoiceViewModel choice)
    {
        var dialog = new TodoTagDialog(this, choice.Tag);
        if (WithModal(() => dialog.ShowDialog()) != true) return;
        await RunActionAsync("编辑标签", async () =>
        {
            await _todoService.UpdateTagAsync(choice.Tag.Id, dialog.TagName, dialog.SelectedColor);
            await RefreshTodosAsync();
        });
    }

    private async Task DeleteTodoTagAsync(TodoTagChoiceViewModel choice)
    {
        if (MessageBox.Show(this, $"删除标签「{choice.Name}」后，它会从相关待办中移除。确定删除吗？",
                "删除标签", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunActionAsync("删除标签", async () =>
        {
            await _todoService.DeleteTagAsync(choice.Tag.Id);
            _todoDraftTagIds.Remove(choice.Tag.Id);
            if (_todoTagFilterId == choice.Tag.Id) _todoTagFilterId = null;
            await RefreshTodosAsync();
        });
    }

    private async void AddTodoTagButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("创建标签", async () =>
        {
            var dialog = new TodoTagDialog(this);
            if (WithModal(() => dialog.ShowDialog()) != true) return;
            var created = await _todoService.CreateTagAsync(dialog.TagName, dialog.SelectedColor);
            _todoDraftTagIds.Add(created.Id);
            await RefreshTodosAsync();
        });
}

public sealed class TodoItemViewModel
{
    public TodoItemViewModel(TodoItem item, IReadOnlyList<TodoTag> tags)
    {
        Item = item;
        Title = item.Title;
        IsCompleted = item.IsCompleted;
        TitleDecorations = item.IsCompleted ? TextDecorations.Strikethrough : null;
        TitleBrush = TodoUiColor.Label(item.IsCompleted);
        var lookup = tags.ToDictionary(tag => tag.Id);
        Tags = item.TagIds.Where(lookup.ContainsKey).Select(id => new TodoTagBadgeViewModel(lookup[id])).ToList();
        TagsVisibility = Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckBrush = Tags.FirstOrDefault()?.Foreground ?? TodoUiColor.Solid("#15803D");
        var createdDate = item.CreatedAt.ToLocalTime().Date;
        var overdueDays = Math.Max(0, (DateTime.Now.Date - createdDate).Days);
        OverdueText = !item.IsCompleted && overdueDays > 0 ? $"延期 {overdueDays} 天" : string.Empty;
        OverdueVisibility = string.IsNullOrEmpty(OverdueText) ? Visibility.Collapsed : Visibility.Visible;
    }

    public TodoItem Item { get; }
    public string Title { get; }
    public bool IsCompleted { get; }
    public TextDecorationCollection? TitleDecorations { get; }
    public Brush TitleBrush { get; }
    public Brush CheckBrush { get; }
    public string OverdueText { get; }
    public Visibility OverdueVisibility { get; }
    public Visibility TagsVisibility { get; }
    public IReadOnlyList<TodoTagBadgeViewModel> Tags { get; }
}

public sealed class TodoTagChoiceViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

    public TodoTagChoiceViewModel(TodoTag tag, bool selected)
    {
        Tag = tag;
        Name = tag.Name;
        IsSelected = selected;
        ToolTipText = "左键勾选，右键编辑或删除标签";
        ChipForeground = TodoUiColor.Solid(tag.Color);
        ChipBackground = TodoUiColor.Soft(tag.Color, selected ? (byte)0x4D : (byte)0x24);
        ChipBorder = TodoUiColor.Solid(tag.Color);
    }

    public TodoTag Tag { get; }
    public string Name { get; }
    public string ToolTipText { get; }
    public Brush ChipForeground { get; }
    public Brush ChipBackground { get; }
    public Brush ChipBorder { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TodoTagStatViewModel
{
    public TodoTagStatViewModel(TodoTagStat stat, bool selected)
    {
        Tag = stat.Tag;
        Name = stat.Tag.Name;
        CompletionRate = stat.CompletionRate;
        RatioText = selected ? $"筛选中 {stat.Completed}/{stat.Total}" : $"{stat.Completed}/{stat.Total}";
        DetailText = $"已完成 {stat.Completed} · 待完成 {stat.Pending}";
        AccentBrush = TodoUiColor.Solid(stat.Tag.Color);
        IsSelected = selected;
    }

    public TodoTag Tag { get; }
    public string Name { get; }
    public double CompletionRate { get; }
    public string RatioText { get; }
    public string DetailText { get; }
    public Brush AccentBrush { get; }
    public bool IsSelected { get; }
}

public sealed class TodoTagBadgeViewModel(TodoTag tag)
{
    public Guid TagId { get; } = tag.Id;
    public string Name { get; } = tag.Name;
    public Brush Background { get; } = TodoUiColor.Soft(tag.Color);
    public Brush Foreground { get; } = TodoUiColor.Solid(tag.Color);
}

internal static class TodoUiColor
{
    public static SolidColorBrush Solid(string hex)
    {
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Soft(string hex, byte alpha = 0x2E)
    {
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        color.A = alpha;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static Brush Label(bool completed) => completed
        ? (Brush)System.Windows.Application.Current.FindResource("LabelTertiaryBrush")
        : (Brush)System.Windows.Application.Current.FindResource("LabelPrimaryBrush");
}
