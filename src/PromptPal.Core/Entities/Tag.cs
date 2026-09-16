using System.ComponentModel.DataAnnotations;

namespace PromptPal.Core.Entities;

public class Tag
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    // 导航属性
    public ICollection<Prompt> Prompts { get; set; } = [];
}
