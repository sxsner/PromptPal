using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PromptPal.Core.Entities;

namespace PromptPal_App;

/// <summary>
/// 新建/编辑提示词表单（宿主于独立 DialogWindow，尺寸不受主窗口限制）。
/// </summary>
public sealed partial class PromptEditorDialog : UserControl
{
    public string ResultTitle { get; private set; } = string.Empty;
    public string ResultContent { get; private set; } = string.Empty;
    public int? ResultCategoryId { get; private set; }
    public IReadOnlyList<string> ResultTags { get; private set; } = Array.Empty<string>();

    /// <summary>由宿主窗口注入：true=保存，false=取消。</summary>
    public Action<bool>? CloseRequested { get; set; }

    public PromptEditorDialog(
        IEnumerable<Category> categories,
        Prompt? existing,
        int? presetCategoryId = null,
        string? presetTitle = null,
        string? presetContent = null)
    {
        InitializeComponent();

        CategoryBox.ItemsSource = categories;

        if (existing is not null)
        {
            HeaderText.Text = "编辑提示词";
            TitleBox.Text = existing.Title;
            ContentBox.Text = existing.Content;
            TagsBox.Text = string.Join(", ", existing.Tags.Select(t => t.Name));
            if (existing.CategoryId.HasValue)
                CategoryBox.SelectedItem = categories.FirstOrDefault(c => c.Id == existing.CategoryId.Value);
        }
        else
        {
            // 右键菜单入口：「在此分类新建」预分类、「粘贴为新提示词」预填正文
            TitleBox.Text = presetTitle ?? string.Empty;
            ContentBox.Text = presetContent ?? string.Empty;
            if (presetCategoryId.HasValue)
                CategoryBox.SelectedItem = categories.FirstOrDefault(c => c.Id == presetCategoryId.Value);
        }

        Loaded += (_, _) => TitleBox.Focus(FocusState.Programmatic);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text) || string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            ErrorText.Text = "标题与内容不能为空";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        ResultTitle = TitleBox.Text.Trim();
        ResultContent = ContentBox.Text;
        ResultCategoryId = (CategoryBox.SelectedItem as Category)?.Id;
        ResultTags = TagsBox.Text
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        CloseRequested?.Invoke(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(false);
}
