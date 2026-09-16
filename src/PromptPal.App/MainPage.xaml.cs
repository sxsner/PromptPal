using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PromptPal.App.ViewModels;
using PromptPal.Core.Entities;
using PromptPal.Core.Services;

namespace PromptPal_App;

/// <summary>
/// 主页面
/// </summary>
public sealed partial class MainPage : Page
{
    public MainViewModel VM { get; }

    // 页面级 DI scope：VM 及其 scoped DbContext 的生命周期跟随页面，
    // 不再从根容器解析（根容器解析 scoped 服务会得到进程级单例，且页面销毁后无法释放）
    private readonly IServiceScope _pageScope;

    public MainPage()
    {
        InitializeComponent();
        _pageScope = App.Services.CreateScope();
        VM = _pageScope.ServiceProvider.GetRequiredService<MainViewModel>();
        DataContext = VM;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await VM.LoadAllAsync();
        VM.RequestAutoHide += OnAutoHide;
        if (App.MainWindowInstance is { } w)
        {
            w.NewRequested += OnTrayNew;
            w.TraySettingsRequested += OnTraySettings;
            w.DataChanged += OnDataChanged;
            // 设置页改字号/显示项后立即刷新本页绑定（ThemeResource 替换不实时，必须走 INPC）
            w.SettingsApplied += VM.RefreshSettings;
            // 置顶图标是代码后置着色：切换浅/深主题时也要重取对应主题的画刷
            RootPage.ActualThemeChanged += OnRootThemeChanged;
            RefreshPinGlyph();

            // 启动即最小化到托盘时弹对话框无人可见，改为仅日志（托盘菜单仍可进设置）
            if (w.HotkeyStartupFailed && !AppSettings.Current.MinimizeOnStartup)
                _ = ShowHotkeyWarningAsync();
        }
    }

    private async Task ShowHotkeyWarningAsync()
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = "全局热键不可用",
                Content = "Ctrl+Shift+P 注册失败，可能已被其他程序占用。可在「设置」中关闭热键或更换占用程序。",
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
                // ContentDialog 弹层只按应用级主题解析资源（系统深色+应用内浅色时会错成深底白字），
                // 必须显式跟随宿主元素的 ActualTheme
                RequestedTheme = ActualTheme
            };
            await dlg.ShowAsync();
        }
        catch { /* 对话框竞态时忽略，日志已记录 */ }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 必须退订：窗口生命周期长于 Frame 导航时，残留订阅会让已销毁页面继续响应事件
        VM.RequestAutoHide -= OnAutoHide;
        if (App.MainWindowInstance is { } w)
        {
            w.NewRequested -= OnTrayNew;
            w.TraySettingsRequested -= OnTraySettings;
            w.DataChanged -= OnDataChanged;
            w.SettingsApplied -= VM.RefreshSettings;
            RootPage.ActualThemeChanged -= OnRootThemeChanged;
        }
        _pageScope.Dispose();
    }

    private void OnRootThemeChanged(FrameworkElement sender, object args)
    {
        RefreshPinGlyph();
        VM.RefreshThemeColors();
    }

    private void OnTrayNew() => _ = ShowEditorAsync(null);
    private void OnTraySettings() => SettingsWindow.ShowOrCreate();
    private void OnDataChanged() => _ = VM.LoadAllAsync();
    private void OnAutoHide() => App.MainWindowInstance?.MinimizeToTrayNow();

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        // Tag 为 Category 实例，或 "__all__"（全部）
        VM.SelectCategoryCommand.Execute(btn.Tag as Category);
    }

    private void FavoritesButton_Click(object sender, RoutedEventArgs e)
        => VM.ToggleFavoritesFilterCommand.Execute(null);

    private void RecentButton_Click(object sender, RoutedEventArgs e)
        => VM.ToggleRecentFilterCommand.Execute(null);

    // ========== 提示词增删改 ==========

    private async void NewPrompt_Click(object sender, RoutedEventArgs e)
        => await ShowEditorAsync(null);

    private async void EditPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedPrompt is null) return;
        await ShowEditorAsync(VM.SelectedPrompt);
    }

    private async Task ShowEditorAsync(
        Prompt? existing,
        int? presetCategoryId = null,
        string? presetTitle = null,
        string? presetContent = null)
    {
        var view = new PromptEditorDialog(VM.CategoryChoices, existing, presetCategoryId, presetTitle, presetContent);
        var win = new DialogWindow(existing is null ? "新建提示词" : "编辑提示词", 480, 600);
        view.CloseRequested = ok => win.Complete(ok);
        win.SetContent(view);

        if (!await win.ShowDialogAsync()) return;

        if (existing is null)
            await VM.CreatePromptAsync(view.ResultTitle, view.ResultContent, view.ResultCategoryId, view.ResultTags);
        else
            await VM.UpdatePromptAsync(existing, view.ResultTitle, view.ResultContent, view.ResultCategoryId, view.ResultTags);
    }

    private async void DeletePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedPrompt is { } p) await ConfirmDeleteAsync(p);
    }

    // WinUI 同一时刻只允许一个 ContentDialog：菜单/按钮/快捷键快速连点会并发 ShowAsync 抛异常
    private bool _deleteConfirmOpen;

    private async Task ConfirmDeleteAsync(Prompt p)
    {
        if (_deleteConfirmOpen) return;
        _deleteConfirmOpen = true;
        try
        {
            var confirm = new ContentDialog
            {
                Title = "删除提示词",
                Content = $"确定删除「{p.Title}」吗？此操作不可恢复。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                await VM.DeletePromptAsync(p.Id);
        }
        finally
        {
            _deleteConfirmOpen = false;
        }
    }

    // ========== 分区右键菜单 ==========
    // 左栏四个区域各有专属菜单：筛选行 / 分类胶囊 / 排序行 / 列表空白区；
    // 提示词条目与右栏详情区共用条目菜单。各区域处理器都标记 Handled，
    // 阻止事件继续冒泡到 rail 兜底菜单，保证"右键哪里就出哪里的功能"。

    private void PromptItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Prompt p)
        {
            VM.SelectedPrompt = p;
            // 必须在鼠标位置弹出：ShowAt(fe) 默认对齐 fe 左上角，列表项里会错位
            BuildItemMenu(p).ShowAt(fe, e.GetPosition(fe));
            e.Handled = true;
        }
    }

    private void PromptItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 双击列表项 = 直接复制（快捷工具的高频路径，省去先选中再点按钮）
        if (sender is FrameworkElement fe && fe.DataContext is Prompt p)
        {
            VM.SelectedPrompt = p;
            VM.CopyCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Detail_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (VM.SelectedPrompt is { } p && sender is FrameworkElement fe)
        {
            BuildItemMenu(p).ShowAt(fe, e.GetPosition(fe));
            e.Handled = true;
        }
    }

    // 筛选行（全部/收藏/最近）右键：直接切筛选 + 新建/刷新
    private void FilterRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        BuildFilterMenu().ShowAt(fe, e.GetPosition(fe));
        e.Handled = true;
    }

    // 具体分类胶囊右键：看该分类、在此分类新建、重命名/移动/删除
    private void CategoryPill_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not CategoryNavItem nav) return;
        var index = -1;
        for (var i = 0; i < VM.Categories.Count; i++)
            if (ReferenceEquals(VM.Categories[i], nav)) { index = i; break; }
        BuildCategoryMenu(nav, index, VM.Categories.Count).ShowAt(fe, e.GetPosition(fe));
        e.Handled = true;
    }

    // 排序行右键：三种排序方式单选
    private void SortRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        BuildSortMenu().ShowAt(fe, e.GetPosition(fe));
        e.Handled = true;
    }

    // 列表空白区右键。条目模板的 StackPanel 没有铺背景，点在条目内空白缝隙时命中测试
    // 会直接落到 ListView：按原始命中元素的 DataContext 兜底，是 Prompt 仍出条目菜单。
    private void ListArea_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Prompt p)
        {
            VM.SelectedPrompt = p;
            BuildItemMenu(p).ShowAt(fe, e.GetPosition(fe));
            e.Handled = true;
            return;
        }
        BuildListEmptyMenu().ShowAt(fe, e.GetPosition(fe));
        e.Handled = true;
    }

    // rail 内边距等空白处的兜底菜单（四个分区都没命中时才会冒泡到这里）
    private void Rail_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        // 在鼠标点弹出：ShowAt(fe) 会对齐整个 rail 左上角，菜单首项被系统标题栏遮住
        BuildRailMenu().ShowAt(fe, e.GetPosition(fe));
        e.Handled = true;
    }

    // 选中态单选勾：当前项显示勾，未选项保留同字形但透明，图标列对齐不跳动
    private const string CheckGlyph = "\uE73E";

    // ---------- 菜单构建小工具 ----------

    private MenuFlyoutItem Menu(
        string text,
        string? glyph,
        RoutedEventHandler onClick,
        string? fg = null,
        bool enabled = true,
        string? accelHint = null)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = glyph is null ? null : new FontIcon { Glyph = glyph },
            IsEnabled = enabled,
            KeyboardAcceleratorTextOverride = accelHint ?? string.Empty,
        };
        item.Click += onClick;
        if (fg is not null && AppPrefs.ThemeBrush(fg, RootPage.ActualTheme) is { } themed)
            item.Foreground = themed;
        return item;
    }

    // 选中态单选勾：当前项显示勾，未选项保留同字形但透明，图标列对齐不跳动
    private MenuFlyoutItem CheckMenu(string text, bool isChecked, RoutedEventHandler onClick)
    {
        var item = Menu(text, CheckGlyph, onClick);
        if (!isChecked && item.Icon is FontIcon fi)
            fi.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        return item;
    }

    // ---------- 提示词条目菜单（操作对象固定为右键点中的 p，不依赖当前选中项） ----------

    private MenuFlyout BuildItemMenu(Prompt p)
    {
        var m = new MenuFlyout();
        m.Items.Add(Menu("复制提示词", "\uE8C8", (_, _) =>
        {
            VM.SelectedPrompt = p;
            VM.CopyCommand.Execute(null);
        }, accelHint: "双击"));
        m.Items.Add(Menu("复制标题", null, (_, _) =>
        {
            VM.SelectedPrompt = p;
            _ = VM.CopyTitleAsync();
        }));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Menu("编辑", "\uE70F", (_, _) => _ = ShowEditorAsync(p)));
        m.Items.Add(Menu(p.IsFavorite ? "取消收藏" : "收藏",
            p.IsFavorite ? "\uE735" : "\uE734",
            (_, _) =>
            {
                VM.SelectedPrompt = p;
                VM.ToggleFavoriteCommand.Execute(null);
            }));
        m.Items.Add(Menu("复制一份", "\uE8C8", (_, _) => _ = VM.DuplicatePromptAsync(p)));
        m.Items.Add(BuildMoveCategorySubMenu(p));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Menu("删除", "\uE74D", (_, _) => _ = ConfirmDeleteAsync(p), "DangerText", accelHint: "Del"));
        return m;
    }

    private MenuFlyoutSubItem BuildMoveCategorySubMenu(Prompt p)
    {
        var sub = new MenuFlyoutSubItem
        {
            Text = "移动到分类",
            Icon = new FontIcon { Glyph = "\uE8B7" },
        };
        sub.Items.Add(CheckMenu("未分类", p.CategoryId is null,
            (_, _) => _ = VM.MovePromptToCategoryAsync(p, null)));
        foreach (var nav in VM.Categories)
        {
            var cat = nav.Category;
            sub.Items.Add(CheckMenu(cat.Name, p.CategoryId == cat.Id,
                (_, _) => _ = VM.MovePromptToCategoryAsync(p, cat.Id)));
        }
        return sub;
    }

    // ---------- 筛选行菜单 ----------

    private MenuFlyout BuildFilterMenu()
    {
        var m = new MenuFlyout();
        m.Items.Add(CheckMenu("显示全部", VM.IsAllSelected,
            (_, _) => VM.SelectCategoryCommand.Execute(null)));
        m.Items.Add(CheckMenu("仅看收藏", VM.ShowFavoritesOnly,
            (_, _) => VM.ToggleFavoritesFilterCommand.Execute(null)));
        m.Items.Add(CheckMenu("仅看最近", VM.ShowRecentOnly,
            (_, _) => VM.ToggleRecentFilterCommand.Execute(null)));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Menu("新建提示词", "\uE710", (_, _) => _ = ShowEditorAsync(null), accelHint: "Ctrl+N"));
        m.Items.Add(Menu("刷新", "\uE72C", (_, _) => _ = VM.LoadAllAsync()));
        return m;
    }

    // ---------- 排序行菜单 ----------

    private MenuFlyout BuildSortMenu()
    {
        var m = new MenuFlyout();
        m.Items.Add(CheckMenu("按使用频率", VM.IsSortFrequency,
            (_, _) => VM.SetSortCommand.Execute(MainViewModel.SortFrequency)));
        m.Items.Add(CheckMenu("按更新时间", VM.IsSortUpdated,
            (_, _) => VM.SetSortCommand.Execute(MainViewModel.SortUpdated)));
        m.Items.Add(CheckMenu("按名称", VM.IsSortName,
            (_, _) => VM.SetSortCommand.Execute(MainViewModel.SortName)));
        return m;
    }

    // ---------- 分类胶囊菜单 ----------

    private MenuFlyout BuildCategoryMenu(CategoryNavItem nav, int index, int count)
    {
        var cat = nav.Category;
        var m = new MenuFlyout();
        m.Items.Add(Menu("只看此分类", "\uE72E", (_, _) => VM.SelectCategoryCommand.Execute(cat),
            enabled: !nav.IsSelected));
        m.Items.Add(Menu("在此分类新建提示词", "\uE710",
            (_, _) => _ = ShowEditorAsync(null, presetCategoryId: cat.Id), accelHint: "Ctrl+N"));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Menu("重命名分类…", "\uE8D2", (_, _) => _ = RenameCategoryFromMenuAsync(cat)));
        m.Items.Add(Menu("上移", "\uE70E", (_, _) => _ = MoveCategoryFromMenuAsync(cat, -1),
            enabled: index > 0));
        m.Items.Add(Menu("下移", "\uE70D", (_, _) => _ = MoveCategoryFromMenuAsync(cat, 1),
            enabled: index >= 0 && index < count - 1));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Menu("删除分类", "\uE74D", (_, _) => _ = DeleteCategoryFromMenuAsync(cat), "DangerText"));
        return m;
    }

    // 分类重命名/删除共用一个重入标志（ContentDialog 同一时刻只允许一个）
    private bool _categoryMenuDialogOpen;

    private async Task RenameCategoryFromMenuAsync(Category cat)
    {
        if (_categoryMenuDialogOpen) return;
        _categoryMenuDialogOpen = true;
        try
        {
            var box = new TextBox { Text = cat.Name, MaxLength = 64, Width = 320 };
            var dialog = new ContentDialog
            {
                Title = "重命名分类",
                Content = box,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
            };
            box.Loaded += (_, _) =>
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            using var scope = App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICategoryService>().RenameAsync(cat.Id, box.Text);
            App.MainWindowInstance?.NotifyDataChanged();
            VM.NotifyStatus($"分类已重命名为「{box.Text.Trim()}」");
        }
        catch (Exception ex)
        {
            Logger.Error("右键菜单重命名分类失败", ex);
            VM.NotifyStatus($"重命名失败：{ex.Message}", true);
        }
        finally
        {
            _categoryMenuDialogOpen = false;
        }
    }

    private async Task MoveCategoryFromMenuAsync(Category cat, int direction)
    {
        try
        {
            using var scope = App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICategoryService>().MoveAsync(cat.Id, direction);
            App.MainWindowInstance?.NotifyDataChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("右键菜单移动分类失败", ex);
            VM.NotifyStatus("移动分类失败，请稍后重试", true);
        }
    }

    private async Task DeleteCategoryFromMenuAsync(Category cat)
    {
        if (_categoryMenuDialogOpen) return;
        _categoryMenuDialogOpen = true;
        try
        {
            var confirm = new ContentDialog
            {
                Title = "删除分类",
                Content = $"确定删除分类「{cat.Name}」吗？该分类下的提示词将变为「未分类」，不会被删除。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            using var scope = App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICategoryService>().DeleteAsync(cat.Id);
            App.MainWindowInstance?.NotifyDataChanged();
            VM.NotifyStatus($"已删除分类「{cat.Name}」");
        }
        catch (Exception ex)
        {
            Logger.Error("右键菜单删除分类失败", ex);
            VM.NotifyStatus($"删除失败：{ex.Message}", true);
        }
        finally
        {
            _categoryMenuDialogOpen = false;
        }
    }

    // ---------- 列表空白区 / rail 兜底菜单 ----------

    private MenuFlyout BuildListEmptyMenu()
    {
        var m = new MenuFlyout();
        m.Items.Add(Menu("新建提示词", "\uE710", (_, _) => _ = ShowEditorAsync(null), accelHint: "Ctrl+N"));
        m.Items.Add(Menu("粘贴为新提示词…", "\uE77F", (_, _) => _ = PasteNewPromptAsync()));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Menu("刷新", "\uE72C", (_, _) => _ = VM.LoadAllAsync()));
        m.Items.Add(Menu("清除选择", "\uE711", (_, _) => VM.SelectedPrompt = null,
            enabled: VM.SelectedPrompt is not null, accelHint: "Esc"));
        return m;
    }

    private MenuFlyout BuildRailMenu()
    {
        var m = new MenuFlyout();
        m.Items.Add(Menu("新建提示词", "\uE710", (_, _) => _ = ShowEditorAsync(null), accelHint: "Ctrl+N"));
        m.Items.Add(Menu("刷新", "\uE72C", (_, _) => _ = VM.LoadAllAsync()));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Menu("设置", "\uE713", (_, _) => SettingsWindow.ShowOrCreate()));
        return m;
    }

    /// <summary>读剪贴板文本直接带进新建对话框的正文（标题留空让用户填）。</summary>
    private async Task PasteNewPromptAsync()
    {
        string? text = null;
        try
        {
            var data = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                text = await data.GetTextAsync();
        }
        catch (Exception ex)
        {
            // 剪贴板被其他进程独占时 GetContent/GetTextAsync 可能抛异常：不阻断，提示即可
            Logger.Error("读取剪贴板新建提示词失败", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            VM.NotifyStatus("剪贴板中没有文本内容", true);
            return;
        }
        await ShowEditorAsync(null, presetContent: text.TrimEnd());
    }

    // ========== 键盘快捷键 ==========

    private void NewAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = ShowEditorAsync(null);
        args.Handled = true;
    }

    private void DeleteAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (VM.SelectedPrompt is { } p) _ = ConfirmDeleteAsync(p);
        args.Handled = true;
    }

    private void EscapeAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        VM.SelectedPrompt = null;
        args.Handled = true;
    }

    // ========== 固定到最前（置顶） ==========

    private void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance is not { } wnd) return;
        wnd.ToggleTopmost();
        RefreshPinGlyph();
        ToolTipService.SetToolTip(PinButton, wnd.IsTopmost ? "取消置顶" : "固定到最前");
        AutomationProperties.SetName(PinButton, wnd.IsTopmost ? "取消置顶" : "固定到最前");
    }

    // 未置顶 = 斜向图钉描边（E840 Pinned），置顶中 = 实心图钉（E841 PinnedFill）+ 强调色。
    // 旧版用的 E718 是横置细描边，在 14px 下几乎看不清；E840/E841 辨识度高得多。
    private const string PinGlyphOff = "\uE840";
    private const string PinGlyphOn = "\uE841";

    private void RefreshPinGlyph()
    {
        if (App.MainWindowInstance is not { } wnd) return;
        PinGlyph.Glyph = wnd.IsTopmost ? PinGlyphOn : PinGlyphOff;
        var key = wnd.IsTopmost ? "Accent" : "TextSecondary";
        if (AppPrefs.ThemeBrush(key, RootPage.ActualTheme) is { } b)
            PinGlyph.Foreground = b;
    }

    // ========== 设置 ==========

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsWindow.ShowOrCreate();
}