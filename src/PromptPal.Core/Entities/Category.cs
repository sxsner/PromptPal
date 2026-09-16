using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PromptPal.Core.Entities;

public class Category
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(4)]
    public string IconEmoji { get; set; } = "📁";

    [MaxLength(16)]
    public string ColorHex { get; set; } = "#FF3B82F6";

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public ICollection<Prompt> Prompts { get; set; } = [];
}
