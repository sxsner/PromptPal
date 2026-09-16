using CommunityToolkit.Mvvm.ComponentModel;
using PromptPal.Core.Entities;

namespace PromptPal.App.ViewModels;

/// <summary>
/// 分类导航项包装：携带选中态以驱动左侧导航高亮
/// </summary>
public partial class CategoryNavItem : ObservableObject
{
    public Category Category { get; }

    /// <summary>当前是否为选中分类</summary>
    [ObservableProperty]
    private bool _isSelected;

    public CategoryNavItem(Category category) => Category = category;

    /// <summary>主题切换后重新触发 IsSelected 绑定，让转换器按新主题重取画刷。</summary>
    public void RefreshTheme() => OnPropertyChanged(nameof(IsSelected));
}