using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PromptPal.Core.Data;
using PromptPal.Core.Entities;
using PromptPal.Core.Services;

namespace PromptPal.Mcp;

/// <summary>
/// PromptPal 的全部 MCP 工具。工具类型按请求由 DI 激活；
/// 内部对每次调用建独立 DI scope，DbContext/服务随调用释放。
/// </summary>
[McpServerToolType]
public sealed class PromptPalTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IServiceScopeFactory _scopeFactory;

    public PromptPalTools(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    private IServiceScope NewScope() => _scopeFactory.CreateScope();

    private static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static McpException NotFound(string what) => new($"未找到{what}");

    private sealed record CategoryDto(int Id, string Name, string? IconEmoji, string? ColorHex, int SortOrder, int PromptCount);

    private static CategoryDto ToCategoryDto(Category c, int promptCount)
        => new(c.Id, c.Name, c.IconEmoji, c.ColorHex, c.SortOrder, promptCount);

    private sealed record PromptSummaryDto(
        int Id,
        string Title,
        string Preview,
        string? Category,
        IReadOnlyList<string> Tags,
        bool IsFavorite,
        int UseCount,
        DateTimeOffset? LastUsedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record PromptDetailDto(
        int Id,
        string Title,
        string Content,
        string? Category,
        IReadOnlyList<string> Tags,
        bool IsFavorite,
        int UseCount,
        DateTimeOffset? LastUsedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record CopyResultDto(int Id, string Title, string Content, int UseCount, DateTimeOffset? LastUsedAt);

    private static PromptSummaryDto ToSummary(Prompt p) => new(
        p.Id,
        p.Title,
        p.ContentPreview,
        p.Category?.Name,
        p.Tags.Select(t => t.Name).OrderBy(n => n).ToList(),
        p.IsFavorite,
        p.UseCount,
        p.LastUsedAt,
        p.CreatedAt,
        p.UpdatedAt);

    private static PromptDetailDto ToDetail(Prompt p) => new(
        p.Id,
        p.Title,
        p.Content,
        p.Category?.Name,
        p.Tags.Select(t => t.Name).OrderBy(n => n).ToList(),
        p.IsFavorite,
        p.UseCount,
        p.LastUsedAt,
        p.CreatedAt,
        p.UpdatedAt);

    /// <summary>按名称（大小写不敏感）解析分类 Id；null 表示未分类。名称不存在时抛出带候选列表的错误。</summary>
    private static async Task<int?> ResolveCategoryIdAsync(AppDbContext db, string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return null;

        var name = categoryName.Trim();
        if (name is "未分类" or "无分类")
            return null;

        var id = await db.Categories
            .Where(c => c.Name.ToLower() == name.ToLower())
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync();
        if (id is null)
        {
            var valid = string.Join("、", await db.Categories.OrderBy(c => c.SortOrder).Select(c => c.Name).ToListAsync());
            throw new McpException($"分类「{name}」不存在。可用分类：{valid}（或留空 / 传\"未分类\"表示不分类）");
        }
        return id;
    }

    private sealed record IdResult(int Id);

    private sealed record TagDto(string Name, int PromptCount);

    /// <summary>分类业务校验异常（重名/不存在等）统一转 McpException，向客户端返回 isError 文本而非内部错误。</summary>
    private static McpException ToolError(Exception ex) => new(ex.Message);

    private static async Task<List<CategoryDto>> LoadCategoryDtosAsync(AppDbContext db)
    {
        var categories = await db.Categories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync();
        var counts = await db.Prompts.AsNoTracking()
            .GroupBy(p => p.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToListAsync();
        var countMap = counts
            .Where(x => x.CategoryId.HasValue)
            .ToDictionary(x => x.CategoryId!.Value, x => x.Count);

        return categories
            .Select(c => ToCategoryDto(c, countMap.TryGetValue(c.Id, out var n) ? n : 0))
            .ToList();
    }

    [McpServerTool(Name = "list_categories")]
    [Description("列出 PromptPal 全部分类（按手动排序），含每个分类下的提示词数量。")]
    public async Task<string> ListCategories()
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return ToJson(await LoadCategoryDtosAsync(db));
    }

    [McpServerTool(Name = "list_tags")]
    [Description("列出全部标签（按名称排序）及每个标签关联的提示词数量，便于用标签过滤前发现可用标签。")]
    public async Task<string> ListTags()
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        var counts = await db.PromptTags.AsNoTracking()
            .GroupBy(pt => pt.TagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToListAsync();
        var countMap = counts.ToDictionary(x => x.TagId, x => x.Count);

        var result = tags.Select(t => new TagDto(t.Name, countMap.TryGetValue(t.Id, out var n) ? n : 0)).ToList();
        return ToJson(result);
    }

    [McpServerTool(Name = "create_category")]
    [Description("新建分类（自动追加到排序末尾），返回更新后的全部分类列表。")]
    public async Task<string> CreateCategory([Description("分类名称（必填，1-64 字，不可与现有分类重名）")] string name)
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await scope.ServiceProvider.GetRequiredService<ICategoryService>()
                .CreateAsync(new Category { Name = name });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw ToolError(ex);
        }
        return ToJson(await LoadCategoryDtosAsync(db));
    }

    [McpServerTool(Name = "rename_category")]
    [Description("重命名分类（大小写不敏感重名检查），返回更新后的全部分类列表。")]
    public async Task<string> RenameCategory(
        [Description("分类 Id")] int id,
        [Description("新分类名称（1-64 字，不可与现有分类重名）")] string newName)
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await scope.ServiceProvider.GetRequiredService<ICategoryService>().RenameAsync(id, newName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw ToolError(ex);
        }
        return ToJson(await LoadCategoryDtosAsync(db));
    }

    [McpServerTool(Name = "move_category")]
    [Description("手动调整分类排序：direction 传 \"up\" 上移一位或 \"down\" 下移一位，返回更新后的全部分类列表。")]
    public async Task<string> MoveCategory(
        [Description("分类 Id")] int id,
        [Description("移动方向：up=上移，down=下移")] string direction)
    {
        var delta = direction?.Trim().ToLowerInvariant() switch
        {
            "up" => -1,
            "down" => 1,
            _ => throw new McpException("direction 只能是 \"up\" 或 \"down\""),
        };

        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await scope.ServiceProvider.GetRequiredService<ICategoryService>().MoveAsync(id, delta);
        }
        catch (InvalidOperationException ex)
        {
            throw ToolError(ex);
        }
        return ToJson(await LoadCategoryDtosAsync(db));
    }

    [McpServerTool(Name = "delete_category")]
    [Description("删除分类；该分类下的提示词会变为「未分类」而不会被删除。返回更新后的全部分类列表。")]
    public async Task<string> DeleteCategory([Description("分类 Id")] int id)
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exists = await db.Categories.AsNoTracking().AnyAsync(c => c.Id == id);
        if (!exists)
            throw NotFound($"分类 Id={id}");
        await scope.ServiceProvider.GetRequiredService<ICategoryService>().DeleteAsync(id);
        return ToJson(await LoadCategoryDtosAsync(db));
    }

    [McpServerTool(Name = "search_prompts")]
    [Description("搜索提示词：可按关键字（匹配标题或正文）、分类名、标签名、是否收藏过滤，返回摘要列表。")]
    public async Task<string> SearchPrompts(
        [Description("关键字，同时匹配标题与正文（可空）")] string? keyword = null,
        [Description("分类名称，大小写不敏感（可空）")] string? category = null,
        [Description("标签名称，大小写不敏感（可空）")] string? tag = null,
        [Description("仅返回收藏项；省略表示不过滤")] bool? favorite = null,
        [Description("返回条数上限，1-100，默认 20")] int? limit = null)
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var take = Math.Clamp(limit ?? 20, 1, 100);

        var query = db.Prompts.AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Tags)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            query = query.Where(p => p.Title.Contains(kw) || p.Content.Contains(kw));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = category.Trim().ToLower();
            query = query.Where(p => p.Category != null && p.Category.Name.ToLower() == cat);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var t = tag.Trim().ToLower();
            query = query.Where(p => p.Tags.Any(x => x.Name.ToLower() == t));
        }

        if (favorite == true)
            query = query.Where(p => p.IsFavorite);
        else if (favorite == false)
            query = query.Where(p => !p.IsFavorite);

        // DateTimeOffset 无法在 SQLite 端排序，取回后内存排序（与 PromptService 保持一致）。
        var items = await query.ToListAsync();
        var ordered = items
            .OrderByDescending(p => p.IsFavorite)
            .ThenByDescending(p => p.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(p => p.CreatedAt)
            .Take(take)
            .Select(ToSummary)
            .ToList();

        return ToJson(ordered);
    }

    [McpServerTool(Name = "get_prompt")]
    [Description("按 Id 获取单条提示词的完整正文与全部元数据。")]
    public async Task<string> GetPrompt([Description("提示词 Id")] int id)
    {
        using var scope = NewScope();
        var prompts = scope.ServiceProvider.GetRequiredService<IPromptService>();
        var p = await prompts.GetByIdAsync(id) ?? throw NotFound($"提示词 Id={id}");
        return ToJson(ToDetail(p));
    }

    [McpServerTool(Name = "create_prompt")]
    [Description("新建提示词。category 传分类名（大小写不敏感）或留空；tags 为标签名数组。")]
    public async Task<string> CreatePrompt(
        [Description("标题（必填，1-256 字）")] string title,
        [Description("正文（必填）")] string content,
        [Description("分类名称，留空为未分类")] string? category = null,
        [Description("标签名列表，可空")] string[]? tags = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new McpException("标题不能为空");
        if (title.Trim().Length > 256)
            throw new McpException("标题最长 256 个字符");
        if (string.IsNullOrWhiteSpace(content))
            throw new McpException("正文不能为空（纯空白不允许）");

        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prompts = scope.ServiceProvider.GetRequiredService<IPromptService>();

        var categoryId = await ResolveCategoryIdAsync(db, category);

        try
        {
            var created = await prompts.CreateAsync(new Prompt
            {
                Title = title.Trim(),
                Content = content,
                CategoryId = categoryId,
            }, tags);
            return ToJson(new IdResult(created.Id));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw ToolError(ex);
        }
    }

    [McpServerTool(Name = "update_prompt")]
    [Description(
        "局部更新提示词，仅修改传入的字段。title/content/is_favorite/category/tags 省略则不变；" +
        "tags 传空数组则清空；category 传\"未分类\"则移出分类。")]
    public async Task<string> UpdatePrompt(
        [Description("提示词 Id")] int id,
        [Description("新标题（省略=不变）")] string? title = null,
        [Description("新正文（省略=不变）")] string? content = null,
        [Description("分类名称（省略=不变，\"未分类\"=移出分类）")] string? category = null,
        [Description("是否收藏（省略=不变）")] bool? isFavorite = null,
        [Description("标签名列表（省略=不变，空数组=清空）")] string[]? tags = null)
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prompts = scope.ServiceProvider.GetRequiredService<IPromptService>();

        var existing = await prompts.GetByIdAsync(id) ?? throw NotFound($"提示词 Id={id}");

        if (title is not null)
        {
            if (string.IsNullOrWhiteSpace(title))
                throw new McpException("标题不能为空");
            existing.Title = title.Trim();
        }
        if (content is not null)
            existing.Content = content;
        if (category is not null)
            existing.CategoryId = await ResolveCategoryIdAsync(db, category);
        if (isFavorite is not null)
            existing.IsFavorite = isFavorite.Value;

        var merged = new Prompt
        {
            Id = existing.Id,
            Title = existing.Title,
            Content = existing.Content,
            CategoryId = existing.CategoryId,
            IsFavorite = existing.IsFavorite,
        };

        try
        {
            // tags 为 null（省略）时 PromptService 不动标签；空数组表示显式清空。
            await prompts.UpdateAsync(merged, tags);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw ToolError(ex);
        }
        // UpdateAsync 返回的 tracked 实体未 Include Category 导航，直接序列化会得到 null，
        // 这里按 Id 重取（Include Category/Tags），保证返回与库内状态一致。
        var updated = await prompts.GetByIdAsync(id);
        return ToJson(ToDetail(updated!));
    }

    [McpServerTool(Name = "toggle_favorite")]
    [Description("切换提示词的收藏状态，返回切换后的完整信息。")]
    public async Task<string> ToggleFavorite([Description("提示词 Id")] int id)
    {
        using var scope = NewScope();
        var prompts = scope.ServiceProvider.GetRequiredService<IPromptService>();
        var exists = await prompts.GetByIdAsync(id) ?? throw NotFound($"提示词 Id={id}");
        await prompts.ToggleFavoriteAsync(id);
        var updated = await prompts.GetByIdAsync(id);
        return ToJson(ToDetail(updated!));
    }

    [McpServerTool(Name = "delete_prompt")]
    [Description("按 Id 删除提示词（不可恢复）。")]
    public async Task<string> DeletePrompt([Description("提示词 Id")] int id)
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prompts = scope.ServiceProvider.GetRequiredService<IPromptService>();
        var exists = await db.Prompts.AsNoTracking().AnyAsync(p => p.Id == id);
        if (!exists)
            throw NotFound($"提示词 Id={id}");
        await prompts.DeleteAsync(id);
        return ToJson(new { ok = true, id });
    }

    [McpServerTool(Name = "copy_prompt")]
    [Description(
        "取用提示词：返回完整正文供对话使用，同时把该提示词使用次数 +1 并记录最近使用时间。" +
        "注意：本工具不操作任何系统剪贴板。")]
    public async Task<string> CopyPrompt([Description("提示词 Id")] int id)
    {
        using var scope = NewScope();
        var prompts = scope.ServiceProvider.GetRequiredService<IPromptService>();
        var p = await prompts.GetByIdAsync(id) ?? throw NotFound($"提示词 Id={id}");
        await prompts.IncrementUseAsync(id);
        var after = await prompts.GetByIdAsync(id);
        return ToJson(new CopyResultDto(after!.Id, after.Title, after.Content, after.UseCount, after.LastUsedAt));
    }
}
