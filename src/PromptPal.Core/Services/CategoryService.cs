using Microsoft.EntityFrameworkCore;
using PromptPal.Core.Data;
using PromptPal.Core.Entities;

namespace PromptPal.Core.Services;

public interface ICategoryService
{
    Task<IReadOnlyList<Category>> GetAllAsync();
    Task<Category?> GetByIdAsync(int id);
    Task<Category> CreateAsync(Category category);
    Task<Category> UpdateAsync(Category category);

    /// <summary>重命名分类（trim、非空、大小写不敏感重名校验）。</summary>
    Task<Category> RenameAsync(int id, string newName);

    /// <summary>调整分类排序：direction=-1 上移一位，+1 下移一位；移动后 SortOrder 整体归一化为 1..N。</summary>
    Task MoveAsync(int id, int direction);

    /// <summary>
    /// 按给定 Id 顺序整体重排（拖拽排序用）：orderedIds 必须与库内全部分类 Id 集合完全一致
    /// （数量相同、无重复、无缺失），随后 SortOrder 归一化为 1..N，单事务提交。
    /// </summary>
    Task ReorderAsync(IReadOnlyList<int> orderedIds);

    Task DeleteAsync(int id);
}

public sealed class CategoryService : ICategoryService
{
    private readonly AppDbContext _db;
    public CategoryService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Category>> GetAllAsync()
        // SortOrder 相同时（历史数据可能存在 0）按 Id 兜底，保证顺序确定、可预测
        => await _db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync();

    public async Task<Category?> GetByIdAsync(int id)
        => await _db.Categories.FindAsync(id);

    public async Task<Category> CreateAsync(Category category)
    {
        var name = category.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new ArgumentException("分类名称不能为空");
        if (name.Length > 64)
            throw new ArgumentException("分类名称最长 64 个字符");

        // 大小写不敏感重名校验（SQLite 默认 BINARY 排序大小写敏感，不能依赖唯一索引兜底）
        if (await _db.Categories.AnyAsync(c => c.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException($"已存在同名分类「{name}」");

        category.Name = name;
        category.CreatedAt = DateTimeOffset.UtcNow;
        // 未显式指定排序时追加到末尾（历史库可能有 SortOrder=0 的旧分类，max+1 仍在其后）
        if (category.SortOrder <= 0)
            category.SortOrder = await _db.Categories.MaxAsync(c => (int?)c.SortOrder) is int max ? max + 1 : 1;
        await _db.Categories.AddAsync(category);
        await _db.SaveChangesAsync();
        return category;
    }

    public async Task<Category> RenameAsync(int id, string newName)
    {
        var name = newName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new ArgumentException("分类名称不能为空");
        if (name.Length > 64)
            throw new ArgumentException("分类名称最长 64 个字符");

        var tracked = await _db.Categories.FindAsync(id)
            ?? throw new InvalidOperationException($"Category Id={id} 不存在");

        if (!string.Equals(tracked.Name, name, StringComparison.OrdinalIgnoreCase)
            && await _db.Categories.AnyAsync(c => c.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException($"已存在同名分类「{name}」");

        tracked.Name = name;
        await _db.SaveChangesAsync();
        return tracked;
    }

    public async Task MoveAsync(int id, int direction)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction), "direction 只能为 -1（上移）或 1（下移）");

        // 取与界面一致的有序列表，移动一位后整体重排为 1..N。
        // 不能只交换两个 SortOrder：历史数据可能存在同值（多个 0），交换同值不会改变顺序。
        var ordered = await _db.Categories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync();
        var index = ordered.FindIndex(c => c.Id == id);
        if (index < 0)
            throw new InvalidOperationException($"Category Id={id} 不存在");

        var target = index + direction;
        if (target < 0 || target >= ordered.Count)
            return; // 已在端点：静默无操作

        var moving = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(target, moving);

        var tracked = await _db.Categories.ToListAsync();
        var byId = tracked.ToDictionary(c => c.Id);
        for (var i = 0; i < ordered.Count; i++)
            byId[ordered[i].Id].SortOrder = i + 1;
        await _db.SaveChangesAsync();
    }

    public async Task ReorderAsync(IReadOnlyList<int> orderedIds)
    {
        if (orderedIds is null || orderedIds.Count == 0)
            throw new ArgumentException("排序 Id 列表不能为空", nameof(orderedIds));
        if (orderedIds.Distinct().Count() != orderedIds.Count)
            throw new ArgumentException("排序 Id 列表存在重复", nameof(orderedIds));

        var tracked = await _db.Categories.ToListAsync();
        var byId = tracked.ToDictionary(c => c.Id);

        // 必须与库内集合完全一致：防止调用方传入部分/陈旧 Id 列表，导致部分分类失去排序
        if (orderedIds.Count != tracked.Count || orderedIds.Any(id => !byId.ContainsKey(id)))
            throw new ArgumentException("排序 Id 列表与当前分类不一致（数量不符或存在不存在的 Id）", nameof(orderedIds));

        // 直接按新顺序赋值；EF 变更跟踪只对真正改变的实体发 UPDATE，无需手动短路。
        // （不能只比较 SortOrder 数值判断"无变化"：历史数据可能存在 SortOrder 重复，
        //   界面显示顺序按 SortOrder→Id 兜底，此时即使数值不变也可能需要落库归一化。）
        for (var i = 0; i < orderedIds.Count; i++)
            byId[orderedIds[i]].SortOrder = i + 1;
        await _db.SaveChangesAsync();
    }

    public async Task<Category> UpdateAsync(Category category)
    {
        // tracked 局部更新：_db.Categories.Update(detached 实体) 会把全部字段标为 Modified，
        // 传入对象上未加载的字段（如 CreatedAt 默认值）会被一起覆盖
        var tracked = await _db.Categories.FindAsync(category.Id)
            ?? throw new InvalidOperationException($"Category Id={category.Id} 不存在");
        tracked.Name = category.Name;
        tracked.IconEmoji = category.IconEmoji;
        tracked.ColorHex = category.ColorHex;
        tracked.SortOrder = category.SortOrder;
        await _db.SaveChangesAsync();
        return tracked;
    }

    public async Task DeleteAsync(int id)
    {
        var c = await _db.Categories.FindAsync(id);
        if (c is null) return;

        // 先解除该分类下所有提示词的关联，避免 FK 约束导致删除失败或产生悬空引用
        var prompts = await _db.Prompts.Where(p => p.CategoryId == id).ToListAsync();
        foreach (var p in prompts) p.CategoryId = null;

        _db.Categories.Remove(c);
        await _db.SaveChangesAsync();
    }
}

public interface ITagService
{
    Task<IReadOnlyList<Tag>> GetAllAsync();
}

public sealed class TagService : ITagService
{
    private readonly AppDbContext _db;
    public TagService(AppDbContext db) => _db = db;
    public async Task<IReadOnlyList<Tag>> GetAllAsync()
        => await _db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
}
