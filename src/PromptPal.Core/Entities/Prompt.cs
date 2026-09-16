using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;

namespace PromptPal.Core.Entities;

public class Prompt
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string Content { get; set; } = string.Empty;

    public int? CategoryId { get; set; }

    [ForeignKey(nameof(CategoryId))]
    public Category? Category { get; set; }

    public bool IsFavorite { get; set; }

    public int UseCount { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public ICollection<Tag> Tags { get; set; } = [];

    /// <summary>列表预览用：换行/连续空白折叠为单个空格后取前 120 字符（不映射到 DB）</summary>
    [NotMapped]
    public string ContentPreview
    {
        get
        {
            // 多行正文直接截断会留下孤立换行，导致单行预览出现空白/错位
            var normalized = WhitespaceRegex.Replace(Content, " ");
            return normalized.Length > 120 ? normalized[..120] + "..." : normalized;
        }
    }

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
}
