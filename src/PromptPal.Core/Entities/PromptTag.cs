namespace PromptPal.Core.Entities;

/// <summary>
/// Prompt ↔ Tag 多对多连接表（强类型实体）
/// </summary>
public sealed class PromptTag
{
    public int PromptId { get; set; }
    public int TagId { get; set; }
    public Prompt? Prompt { get; set; }
    public Tag? Tag { get; set; }
}
