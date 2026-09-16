using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using PromptPal.Core.Data;
using PromptPal.Core.Entities;

namespace PromptPal.Core.Services;

public interface IPromptService
{
    IAsyncEnumerable<Prompt> GetAllAsync(int? categoryId = null, bool? favoritesOnly = null, CancellationToken cancellationToken = default);
    Task<Prompt?> GetByIdAsync(int id);
    Task<Prompt> CreateAsync(Prompt prompt, IEnumerable<string>? tagNames = null);
    Task<Prompt> UpdateAsync(Prompt prompt, IEnumerable<string>? tagNames = null);
    Task DeleteAsync(int id);
    Task ToggleFavoriteAsync(int id);
    Task IncrementUseAsync(int id);
    Task<IReadOnlyList<Prompt>> GetRecentAsync(int count = 10, CancellationToken cancellationToken = default);
}

public sealed class PromptService : IPromptService
{
    private readonly AppDbContext _db;

    public PromptService(AppDbContext db) => _db = db;

    public async IAsyncEnumerable<Prompt> GetAllAsync(
        int? categoryId = null,
        bool? favoritesOnly = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IQueryable<Prompt> query = _db.Prompts.AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Tags);

        if (categoryId.HasValue)
            query = query.Where(p => p.CategoryId == categoryId.Value);

        if (favoritesOnly == true)
            query = query.Where(p => p.IsFavorite);

        // SQLite 提供器无法把 DateTimeOffset 的 ORDER BY 翻译成 SQL，会抛 NotSupportedException。
        // 这里先按过滤条件取回数据，再在内存中排序（个人工具数据量小，开销可忽略）。
        var items = await query.ToListAsync(cancellationToken);
        var ordered = items
            .OrderByDescending(p => p.IsFavorite)
            .ThenByDescending(p => p.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(p => p.CreatedAt);

        foreach (var item in ordered)
            yield return item;
    }

    public async Task<Prompt?> GetByIdAsync(int id)
    {
        return await _db.Prompts
            .Include(p => p.Category)
            .Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<Prompt> CreateAsync(Prompt prompt, IEnumerable<string>? tagNames = null)
    {
        prompt.Title = (prompt.Title ?? string.Empty).Trim();
        prompt.Content ??= string.Empty;
        ValidateTitleAndContent(prompt.Title, prompt.Content);

        prompt.CreatedAt = DateTimeOffset.UtcNow;
        prompt.UpdatedAt = prompt.CreatedAt;

        await _db.Prompts.AddAsync(prompt);

        if (tagNames is not null)
        {
            await AssignTagsAsync(prompt, tagNames);
        }

        await _db.SaveChangesAsync();
        return prompt;
    }

    public async Task<Prompt> UpdateAsync(Prompt prompt, IEnumerable<string>? tagNames = null)
    {
        // 统一用 tracked 实体操作，避免 ChangeTracker 里同 Id 双实体冲突
        var tracked = await _db.Prompts.Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == prompt.Id)
            ?? throw new InvalidOperationException($"Prompt Id={prompt.Id} 不存在");

        var title = (prompt.Title ?? string.Empty).Trim();
        var content = prompt.Content ?? string.Empty;
        ValidateTitleAndContent(title, content);

        tracked.Title = title;
        tracked.Content = content;
        tracked.CategoryId = prompt.CategoryId;
        tracked.IsFavorite = prompt.IsFavorite;
        tracked.UpdatedAt = DateTimeOffset.UtcNow;

        if (tagNames is not null)
        {
            tracked.Tags.Clear();
            await AssignTagsAsync(tracked, tagNames);
        }

        await _db.SaveChangesAsync();

        // 标签被替换/清空后，旧标签可能已无任何引用：顺手清理，避免 Tags 表无限堆积孤儿
        if (tagNames is not null)
            await DeleteUnusedTagsAsync();
        return tracked;
    }

    public async Task DeleteAsync(int id)
    {
        var p = await _db.Prompts.FindAsync(id);
        if (p is null) return;
        _db.Prompts.Remove(p);
        await _db.SaveChangesAsync();
        await DeleteUnusedTagsAsync();
    }

    /// <summary>删除不再被任何提示词引用的孤儿标签。</summary>
    private async Task DeleteUnusedTagsAsync()
        => await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM Tags WHERE Id NOT IN (SELECT DISTINCT TagId FROM PromptTags);");

    private static void ValidateTitleAndContent(string title, string content)
    {
        if (title.Length == 0)
            throw new ArgumentException("标题不能为空");
        if (title.Length > 256)
            throw new ArgumentException("标题最长 256 个字符");
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("内容不能为空");
    }

    public async Task ToggleFavoriteAsync(int id)
    {
        var p = await _db.Prompts.FindAsync(id);
        if (p is null) return;
        p.IsFavorite = !p.IsFavorite;
        await _db.SaveChangesAsync();
    }

    public async Task IncrementUseAsync(int id)
    {
        // 原子自增：App 与 MCP sidecar 可能几乎同时取用同一提示词，
        // 读-改-写会在并发下丢计数；一条 UPDATE 在 SQLite 行锁内完成。
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Prompts SET UseCount = UseCount + 1, LastUsedAt = {DateTimeOffset.UtcNow} WHERE Id = {id};");
    }

    public async Task<IReadOnlyList<Prompt>> GetRecentAsync(int count = 10, CancellationToken cancellationToken = default)
    {
        // 同 GetAllAsync：DateTimeOffset 不能在 SQL 里排序，取回后内存排序再截取
        var list = await _db.Prompts.AsNoTracking()
            .Where(p => p.LastUsedAt != null)
            .Include(p => p.Category)
            .Include(p => p.Tags)
            .ToListAsync(cancellationToken);

        return list
            .OrderByDescending(p => p.LastUsedAt)
            .Take(count)
            .ToList();
    }

    private async Task AssignTagsAsync(Prompt prompt, IEnumerable<string> tagNames)
    {
        var distinctNames = tagNames
            .Select(n => n.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctNames.Count == 0) return;

        // 只取回可能命中的标签（一次过滤查询，避免全表加载），再按大小写不敏感匹配
        var lowerNames = distinctNames.Select(n => n.ToLowerInvariant()).ToList();
        var existingByName = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        var candidates = await _db.Tags
            .Where(t => lowerNames.Contains(t.Name.ToLower()))
            .ToListAsync();
        foreach (var t in candidates)
            existingByName.TryAdd(t.Name, t);

        foreach (var name in distinctNames)
        {
            if (!existingByName.TryGetValue(name, out var tag))
            {
                tag = new Tag { Name = name };
                await _db.Tags.AddAsync(tag);
                existingByName[name] = tag;
            }
            prompt.Tags.Add(tag);
        }
    }
}
