using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptPal.App.ViewModels;
using PromptPal_App;
using PromptPal.Core.Entities;
using PromptPal.Core.Services;

namespace PromptPal.App.ViewModels;

/// <summary>
/// 主页面 ViewModel（最小化：分类/筛选 + 提示词列表 + 复制，无变量、无搜索）
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ICategoryService _categorySvc;
    private readonly IPromptService _promptSvc;
    private readonly IClipboardService _clipboard;

    // 竞态取消：分类/筛选快速切换时丢弃过期刷新
    private CancellationTokenSource? _refreshCts;

    public MainViewModel(
        ICategoryService categorySvc,
        IPromptService promptSvc,
        IClipboardService clipboard)
    {
        _categorySvc = categorySvc;
        _promptSvc = promptSvc;
        _clipboard = clipboard;
    }

    // ========== 左侧分类 ==========
    [ObservableProperty] private ObservableCollection<CategoryNavItem> _categories = [];
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private bool _showFavoritesOnly;
    [ObservableProperty] private bool _showRecentOnly;

    // ========== 列表排序（频率=默认：收藏优先→最近使用→创建时间） ==========
    public const string SortFrequency = "frequency";
    public const string SortUpdated = "updated";
    public const string SortName = "name";

    [ObservableProperty] private string _sortMode = SortFrequency;
    public bool IsSortFrequency => SortMode == SortFrequency;
    public bool IsSortUpdated => SortMode == SortUpdated;
    public bool IsSortName => SortMode == SortName;

    /// <summary>「全部」是否处于选中态（无分类/收藏/最近筛选）</summary>
    public bool IsAllSelected => SelectedCategory is null && !ShowFavoritesOnly && !ShowRecentOnly;

    // ========== 提示词列表 ==========
    [ObservableProperty] private ObservableCollection<Prompt> _prompts = [];
    [ObservableProperty] private Prompt? _selectedPrompt;
    [ObservableProperty] private bool _hasPrompts;

    // ========== 命令 ==========
    public ICommand SelectCategoryCommand => new RelayCommand<Category>(SelectCategory);
    public ICommand ToggleFavoritesFilterCommand => new RelayCommand(ToggleFavoritesFilter);
    public ICommand ToggleRecentFilterCommand => new RelayCommand(ToggleRecentFilter);
    public ICommand SetSortCommand => new RelayCommand<string>(SetSort);
    public ICommand CopyCommand => new AsyncRelayCommand(CopyAsync);
    public ICommand ToggleFavoriteCommand => new AsyncRelayCommand(ToggleFavoriteAsync);
    public ICommand LoadDataCommand => new AsyncRelayCommand(LoadAllAsync);

    // ========== 底部状态条瞬时反馈（不再用 InfoBar，避免顶走主区域造成 UI 跳动） ==========
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _statusIsError;

    // ========== 设置驱动的外观 ==========
    /// <summary>复制后请求隐藏到托盘（由页面订阅）。</summary>
    public event Action? RequestAutoHide;

    public double BodyFont => AppSettings.Current.FontSize switch
    {
        "small" => 13, "large" => 18, _ => 15,
    };
    public double ListTitleFont => AppSettings.Current.FontSize switch
    {
        "small" => 13, "large" => 16, _ => 14,
    };
    public double ListBodyFont => AppSettings.Current.FontSize switch
    {
        "small" => 11, "large" => 14, _ => 12,
    };
    public double DetailTitleFont => AppSettings.Current.FontSize switch
    {
        "small" => 16, "large" => 21, _ => 18,
    };
    public bool ShowUseCount => AppSettings.Current.ShowUseCount;

    /// <summary>设置变更后刷新绑定（字号/次数靠 INPC 实时生效，不依赖 ThemeResource 热更新）。</summary>
    public void RefreshSettings()
    {
        OnPropertyChanged(nameof(BodyFont));
        OnPropertyChanged(nameof(ListTitleFont));
        OnPropertyChanged(nameof(ListBodyFont));
        OnPropertyChanged(nameof(DetailTitleFont));
        OnPropertyChanged(nameof(ShowUseCount));
    }

    /// <summary>
    /// 浅/深主题切换后刷新所有“经转换器取色”的绑定：转换器不会因 ActualTheme 变化自动重算，
    /// 必须重新抛出这些布尔属性，chip/胶囊/排序/状态文字才能拿到新主题的画刷。
    /// </summary>
    public void RefreshThemeColors()
    {
        foreach (var n in Categories) n.RefreshTheme();
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(ShowFavoritesOnly));
        OnPropertyChanged(nameof(ShowRecentOnly));
        OnPropertyChanged(nameof(IsSortFrequency));
        OnPropertyChanged(nameof(IsSortUpdated));
        OnPropertyChanged(nameof(IsSortName));
        OnPropertyChanged(nameof(StatusIsError));
    }

    /// <summary>编辑对话框用的分类实体列表（供 ComboBox ItemsSource）</summary>
    public IEnumerable<Category> CategoryChoices => Categories.Select(n => n.Category);

    // ========== 方法 ==========

    public async Task LoadAllAsync()
    {
        Categories = new ObservableCollection<CategoryNavItem>(
            (await _categorySvc.GetAllAsync()).Select(c => new CategoryNavItem(c)));
        // 当前选中分类可能刚在别处（右键菜单/设置页）被删除：清掉悬空选中，避免列表按不存在的 Id 过滤出空页
        if (SelectedCategory is not null && Categories.All(n => n.Category.Id != SelectedCategory.Id))
            SelectedCategory = null;
        SyncNavSelection();
        await RefreshPromptsAsync();
    }

    private async Task RefreshPromptsAsync(CancellationToken ct = default)
    {
        var keepSelectedId = SelectedPrompt?.Id;
        var list = new List<Prompt>();
        try
        {
            if (ShowRecentOnly)
            {
                list.AddRange(await _promptSvc.GetRecentAsync(50, ct));
            }
            else
            {
                // CT 下传到 EF：快速切筛选时旧查询在数据库执行阶段即被中止，而不是取回全表后才丢弃
                await foreach (var p in _promptSvc.GetAllAsync(
                    SelectedCategory?.Id,
                    ShowFavoritesOnly ? true : null,
                    ct))
                {
                    list.Add(p);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 被更新一次刷新取代：静默丢弃，避免未观察异常噪声（TriggerRefresh 会立刻发起新查询）
            return;
        }
        if (ct.IsCancellationRequested) return;

        var ordered = ApplySort(list).ToList();
        Prompts = new ObservableCollection<Prompt>(ordered);
        HasPrompts = ordered.Count > 0;

        // 复制会改变 LastUsedAt 导致重排、收藏切换改变置顶位：重建列表后按 Id 保持选中，
        // 否则详情面板会在最常用的「复制」动作后突然消失
        if (keepSelectedId.HasValue)
            SelectedPrompt = Prompts.FirstOrDefault(p => p.Id == keepSelectedId.Value);
    }

    private IEnumerable<Prompt> ApplySort(IEnumerable<Prompt> items) => SortMode switch
    {
        // 更新时间：新→旧
        SortUpdated => items.OrderByDescending(p => p.UpdatedAt),
        // 名称：不区分大小写的本地化排序
        SortName => items.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase),
        // 默认频率：收藏置顶 → 最近使用 → 创建时间
        _ => items
            .OrderByDescending(p => p.IsFavorite)
            .ThenByDescending(p => p.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(p => p.CreatedAt),
    };

    private void SetSort(string? mode)
    {
        if (mode is not (SortFrequency or SortUpdated or SortName) || mode == SortMode) return;
        SortMode = mode;
        OnPropertyChanged(nameof(IsSortFrequency));
        OnPropertyChanged(nameof(IsSortUpdated));
        OnPropertyChanged(nameof(IsSortName));
        TriggerRefresh();
    }

    private void SelectCategory(Category? c)
    {
        if (ShowRecentOnly) ShowRecentOnly = false;
        ShowFavoritesOnly = false;
        SelectedCategory = c;
        SyncNavSelection();
        TriggerRefresh();
    }

    private void ToggleFavoritesFilter()
    {
        ShowRecentOnly = false;
        SelectedCategory = null;
        ShowFavoritesOnly = !ShowFavoritesOnly;
        SyncNavSelection();
        TriggerRefresh();
    }

    private void ToggleRecentFilter()
    {
        ShowFavoritesOnly = false;
        SelectedCategory = null;
        ShowRecentOnly = !ShowRecentOnly;
        SyncNavSelection();
        TriggerRefresh();
    }

    /// <summary>同步左侧导航选中高亮（分类/全部/收藏/最近四者互斥）</summary>
    private void SyncNavSelection()
    {
        var highlightId = (!ShowFavoritesOnly && !ShowRecentOnly) ? SelectedCategory?.Id : null;
        foreach (var n in Categories)
            n.IsSelected = highlightId.HasValue && n.Category.Id == highlightId.Value;
        OnPropertyChanged(nameof(IsAllSelected));
    }

    // 分类/筛选切换立即执行，保留取消能力
    private void TriggerRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        _ = RefreshPromptsAsync(_refreshCts.Token);
    }

    // ========== 复制 ==========

    public async Task CopyAsync()
    {
        if (SelectedPrompt is null) return;

        // 剪贴板被占用导致写入失败时：不累加使用次数、不隐藏窗口，明确提示用户重试
        if (!await _clipboard.CopyTextAsync(SelectedPrompt.Content))
        {
            _ = ShowStatusAsync("复制失败：剪贴板被其他程序占用，请稍后重试", true);
            return;
        }

        await _promptSvc.IncrementUseAsync(SelectedPrompt.Id);
        _ = ShowStatusAsync("已复制到剪贴板", false);
        await RefreshPromptsAsync();
        if (AppSettings.Current.AutoHideAfterCopy)
            RequestAutoHide?.Invoke();
    }

    /// <summary>在底部状态条显示瞬时反馈，2 秒后自动清空（错误停留 4 秒）。</summary>
    private async Task ShowStatusAsync(string message, bool isError)
    {
        StatusIsError = isError;
        StatusText = message;
        await Task.Delay(isError ? 4000 : 2000);
        if (StatusText == message) StatusText = string.Empty;
    }

    /// <summary>供页面在其他操作（如保存、删除）后复用状态条。</summary>
    public void NotifyStatus(string message, bool isError = false)
        => _ = ShowStatusAsync(message, isError);

    // ========== 提示词增删改 ==========

    public async Task CreatePromptAsync(string title, string content, int? categoryId, IEnumerable<string> tags)
    {
        var saved = await _promptSvc.CreateAsync(
            new Prompt { Title = title, Content = content, CategoryId = categoryId }, tags);
        await RefreshPromptsAsync();
        SelectedPrompt = Prompts.FirstOrDefault(p => p.Id == saved.Id);
    }

    public async Task UpdatePromptAsync(Prompt target, string title, string content, int? categoryId, IEnumerable<string> tags)
    {
        target.Title = title;
        target.Content = content;
        target.CategoryId = categoryId;
        await _promptSvc.UpdateAsync(target, tags);
        await RefreshPromptsAsync();
        SelectedPrompt = Prompts.FirstOrDefault(p => p.Id == target.Id);
    }

    public async Task DeletePromptAsync(int id)
    {
        await _promptSvc.DeleteAsync(id);
        SelectedPrompt = null;
        await RefreshPromptsAsync();
    }

    public async Task ToggleFavoriteAsync()
    {
        if (SelectedPrompt is null) return;
        await _promptSvc.ToggleFavoriteAsync(SelectedPrompt.Id);
        await RefreshPromptsAsync();
    }

    // ========== 右键菜单小功能 ==========

    /// <summary>复制当前选中提示词的标题（正文复制走 CopyAsync）。</summary>
    public async Task CopyTitleAsync()
    {
        if (SelectedPrompt is null) return;
        if (!await _clipboard.CopyTextAsync(SelectedPrompt.Title))
        {
            _ = ShowStatusAsync("复制失败：剪贴板被其他程序占用，请稍后重试", true);
            return;
        }
        NotifyStatus("标题已复制到剪贴板");
    }

    /// <summary>把提示词移动到指定分类（null = 未分类），保留正文、标签、收藏与计数。</summary>
    public async Task MovePromptToCategoryAsync(Prompt p, int? categoryId)
    {
        p.CategoryId = categoryId;
        // 显式传现有标签：UpdateAsync 传 null 时不动标签，这里为统一路径仍按原标签集合重赋一遍
        var tagNames = p.Tags?.Select(t => t.Name) ?? Enumerable.Empty<string>();
        await _promptSvc.UpdateAsync(p, tagNames);
        NotifyStatus(categoryId is null ? "已移到「未分类」" : "已移动分类");
        await RefreshPromptsAsync();
    }

    /// <summary>复制一份（标题加「副本」后缀、同正文/分类/标签、不复制收藏），并选中新条目。</summary>
    public async Task DuplicatePromptAsync(Prompt source)
    {
        // 服务端标题上限 256：给「 副本」预留字符，避免长标题复制时被校验拒绝
        var baseTitle = source.Title.Length > 253 ? source.Title[..253] : source.Title;
        var saved = await _promptSvc.CreateAsync(
            new Prompt
            {
                Title = baseTitle + " 副本",
                Content = source.Content,
                CategoryId = source.CategoryId,
            },
            source.Tags?.Select(t => t.Name));
        await RefreshPromptsAsync();
        SelectedPrompt = Prompts.FirstOrDefault(p => p.Id == saved.Id);
        NotifyStatus("已复制一份");
    }
}
