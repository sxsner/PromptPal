using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PromptPal.Core.Data;
using PromptPal.Core.Entities;
using PromptPal.Core.Services;

namespace PromptPal.Core.Tests;

public class CategoryAndPromptServiceTests
{
    private static AppDbContext CreateContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task DeleteCategory_UnlinksPrompts_LeavesNoFkErrorOrDanglingRef()
    {
        using var db = CreateContext();

        var cat = new Category { Name = "待删" };
        db.Categories.Add(cat);
        await db.SaveChangesAsync();
        db.Prompts.Add(new Prompt { Title = "p", Content = "c", CategoryId = cat.Id });
        await db.SaveChangesAsync();

        var catId = cat.Id;

        await new CategoryService(db).DeleteAsync(catId);

        // 分类已删除，且其下提示词转为未分类而非抛 FK 异常
        Assert.Equal(0, await db.Categories.CountAsync(c => c.Name == "待删"));
        var p = await db.Prompts.SingleAsync();
        Assert.Null(p.CategoryId);
    }

    [Fact]
    public async Task CreateWithSameTag_DifferentCase_ReusesTag_NoDuplicate()
    {
        using var db = CreateContext();

        db.Tags.Add(new Tag { Name = "Foo" }); // 库里已有 “Foo”
        await db.SaveChangesAsync();

        var svc = new PromptService(db);
        var prompt = await svc.CreateAsync(
            new Prompt { Title = "t", Content = "c" },
            new[] { "foo", "FOO" });

        // 大小写不敏感匹配：复用已有 “Foo”，不新建 “foo”
        Assert.Single(prompt.Tags);
        Assert.Equal(1, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task CreateCategory_DuplicateName_CaseInsensitive_Throws()
    {
        using var db = CreateContext();
        var svc = new CategoryService(db);

        // EnsureCreated 已播种 3 个内置分类（系统/编程/文档），这里用种子之外的名称
        await svc.CreateAsync(new Category { Name = "自定义甲" });
        await svc.CreateAsync(new Category { Name = "work" });

        // 完全同名与大小写不同都视为重名
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(new Category { Name = "自定义甲" }));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(new Category { Name = "WORK" }));
        Assert.Equal(5, await db.Categories.CountAsync()); // 3 种子 + 2 新建
    }

    [Fact]
    public async Task CreateCategory_WithoutSortOrder_AppendsToEnd()
    {
        using var db = CreateContext();
        var svc = new CategoryService(db);

        var first = await svc.CreateAsync(new Category { Name = "自定义甲" });
        var second = await svc.CreateAsync(new Category { Name = "自定义乙" });

        // 种子 1..3 之后依次追加为 4、5，顺序与创建顺序一致
        Assert.Equal(4, first.SortOrder);
        Assert.Equal(5, second.SortOrder);
        var names = (await svc.GetAllAsync()).Select(c => c.Name).ToList();
        Assert.Equal("自定义乙", names[^1]);
    }

    [Fact]
    public async Task RenameCategory_Trims_And_RejectsDuplicateOrEmpty()
    {
        using var db = CreateContext();
        var svc = new CategoryService(db);
        var cat = await svc.CreateAsync(new Category { Name = "待改名" });

        var renamed = await svc.RenameAsync(cat.Id, "  新名字  ");
        Assert.Equal("新名字", renamed.Name);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RenameAsync(cat.Id, "系统")); // 与种子重名
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RenameAsync(cat.Id, "   "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RenameAsync(9999, "x"));
    }

    [Fact]
    public async Task MoveCategory_SwapsPosition_And_NormalizesSortOrder()
    {
        using var db = CreateContext();
        var svc = new CategoryService(db);

        // 起点为种子顺序：系统、编程、文档
        var before = (await svc.GetAllAsync()).Select(c => c.Name).ToList();
        Assert.Equal(new[] { "系统", "编程", "文档" }, before);

        // 「文档」(末位) 上移一位 → 与「编程」交换
        var last = (await svc.GetAllAsync())[^1];
        await svc.MoveAsync(last.Id, -1);

        var afterUp = (await svc.GetAllAsync()).Select(c => c.Name).ToList();
        Assert.Equal(new[] { "系统", "文档", "编程" }, afterUp);
        Assert.Equal(new[] { 1, 2, 3 }, (await svc.GetAllAsync()).Select(c => c.SortOrder));

        // 首项继续上移 = 端点无操作，不抛异常、顺序不变
        var first = (await svc.GetAllAsync())[0];
        await svc.MoveAsync(first.Id, -1);
        Assert.Equal(afterUp, (await svc.GetAllAsync()).Select(c => c.Name).ToList());

        // 非法方向抛异常；不存在的 Id 抛异常
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.MoveAsync(first.Id, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MoveAsync(9999, -1));
    }

    [Fact]
    public async Task ReorderCategory_AppliesNewOrder_And_NormalizesSortOrder()
    {
        using var db = CreateContext();
        var svc = new CategoryService(db);

        var ordered = (await svc.GetAllAsync()).Select(c => c.Id).ToList();
        // 模拟拖拽：首项移到末尾 → 2,3,1
        var dragged = ordered[0];
        var reversed = ordered.Skip(1).Append(dragged).ToList();

        await svc.ReorderAsync(reversed);

        var after = await svc.GetAllAsync();
        Assert.Equal(reversed, after.Select(c => c.Id).ToList());
        Assert.Equal(new[] { 1, 2, 3 }, after.Select(c => c.SortOrder));

        // 原顺序再拖回去，能完整还原
        await svc.ReorderAsync(ordered);
        Assert.Equal(ordered, (await svc.GetAllAsync()).Select(c => c.Id).ToList());
    }

    [Fact]
    public async Task ReorderCategory_InvalidIdLists_Throw_And_LeaveDataUnchanged()
    {
        using var db = CreateContext();
        var svc = new CategoryService(db);
        var ordered = (await svc.GetAllAsync()).Select(c => c.Id).ToList();
        var snapshot = (await svc.GetAllAsync()).Select(c => (c.Id, c.SortOrder)).ToList();

        // 空列表 / 数量不符 / 含不存在 Id / 重复 Id 都拒绝
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ReorderAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ReorderAsync(ordered.Skip(1).ToList()));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ReorderAsync(ordered.Append(9999).ToList()));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ReorderAsync(ordered.Skip(1).Append(ordered[1]).ToList()));

        // 抛异常后排序数据原样不动
        Assert.Equal(snapshot, (await svc.GetAllAsync()).Select(c => (c.Id, c.SortOrder)).ToList());
    }

    [Fact]
    public async Task GetAllAsync_And_GetRecent_WithFavoritesAndDates_DoNotThrow()
    {
        using var db = CreateContext();
        var svc = new PromptService(db);

        var a = await svc.CreateAsync(new Prompt { Title = "a", Content = "c" });
        await svc.CreateAsync(new Prompt { Title = "b", Content = "c", IsFavorite = true });
        await svc.CreateAsync(new Prompt { Title = "cc", Content = "c" });
        await svc.IncrementUseAsync(a.Id); // 给一条设置 LastUsedAt，覆盖“最近使用”路径

        // 修复前：这里在 SQL 端对 DateTimeOffset 排序 → NotSupportedException（表现为“添加提示词失败”）
        var all = new List<Prompt>();
        await foreach (var p in svc.GetAllAsync()) all.Add(p);

        Assert.Equal(3, all.Count);
        Assert.Equal("b", all[0].Title); // 收藏项置顶

        var recent = await svc.GetRecentAsync(10);
        Assert.Single(recent);
        Assert.Equal("a", recent[0].Title);
    }

    [Fact]
    public async Task Sqlite_DoesNotSupport_DateTimeOffset_OrderBy_RootCauseGuard()
    {
        // 固化根因：确认该 EF/SQLite 组合确实拒绝在 SQL 端按 DateTimeOffset 排序。
        // 若哪天它不再抛异常，说明数据层可回到 SQL 排序，届时可移除非必要提示。
        using var db = CreateContext();
        db.Prompts.Add(new Prompt { Title = "x", Content = "c", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            var _ = await db.Prompts
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();
        });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DeletePrompt_RemovesOrphanTags_ButKeepsSharedTags()
    {
        using var db = CreateContext();
        var svc = new PromptService(db);

        var a = await svc.CreateAsync(new Prompt { Title = "a", Content = "c" }, new[] { "独占", "共享" });
        await svc.CreateAsync(new Prompt { Title = "b", Content = "c" }, new[] { "共享", "其他" });

        await svc.DeleteAsync(a.Id);

        var names = await db.Tags.Select(t => t.Name).ToListAsync();
        Assert.DoesNotContain("独占", names);
        Assert.Contains("共享", names);
        Assert.Contains("其他", names);
    }

    [Fact]
    public async Task UpdatePrompt_ReplacingTags_KeepsTagsStillReferencedByOthers()
    {
        using var db = CreateContext();
        var svc = new PromptService(db);

        var a = await svc.CreateAsync(new Prompt { Title = "a", Content = "c" }, new[] { "旧标签" });
        // 另一提示词也引用「旧标签」时，a 移走后该标签必须保留
        await svc.CreateAsync(new Prompt { Title = "b", Content = "c" }, new[] { "旧标签", "保留" });

        var detached = await db.Prompts.AsNoTracking().FirstAsync(p => p.Id == a.Id);
        await svc.UpdateAsync(detached, new[] { "新标签" });

        var names = await db.Tags.Select(t => t.Name).ToListAsync();
        Assert.Contains("旧标签", names);
        Assert.Contains("保留", names);
        Assert.Contains("新标签", names);
    }

    [Fact]
    public async Task UpdatePrompt_ReplacingLastReference_RemovesOrphanTag()
    {
        using var db = CreateContext();
        var svc = new PromptService(db);

        var a = await svc.CreateAsync(new Prompt { Title = "a", Content = "c" }, new[] { "旧标签" });

        var detached = await db.Prompts.AsNoTracking().FirstAsync(p => p.Id == a.Id);
        await svc.UpdateAsync(detached, new[] { "新标签" });

        var names = await db.Tags.Select(t => t.Name).ToListAsync();
        Assert.DoesNotContain("旧标签", names);
        Assert.Contains("新标签", names);
    }

    [Fact]
    public async Task CreateOrUpdate_ValidatesTitleContentAndCategoryLength()
    {
        using var db = CreateContext();
        var prompts = new PromptService(db);
        var cats = new CategoryService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            prompts.CreateAsync(new Prompt { Title = "  ", Content = "c" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            prompts.CreateAsync(new Prompt { Title = "t", Content = "   " }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            prompts.CreateAsync(new Prompt { Title = new string('x', 257), Content = "c" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            cats.CreateAsync(new Category { Name = new string('长', 65) }));

        var created = await cats.CreateAsync(new Category { Name = "合法分类" });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            cats.RenameAsync(created.Id, new string('a', 65)));
    }

    [Fact]
    public async Task IncrementUse_AtomicUpdate_BumpsCountAndSetsLastUsedAt()
    {
        using var db = CreateContext();
        var svc = new PromptService(db);
        var a = await svc.CreateAsync(new Prompt { Title = "a", Content = "c" });

        await svc.IncrementUseAsync(a.Id);
        await svc.IncrementUseAsync(a.Id);

        var reloaded = await db.Prompts.AsNoTracking().SingleAsync(p => p.Id == a.Id);
        Assert.Equal(2, reloaded.UseCount);
        Assert.NotNull(reloaded.LastUsedAt);
    }

    [Fact]
    public async Task ResetToDefaults_ClearsAllData_And_RestoresSeedContent()
    {
        using var db = CreateContext();

        // 先制造一批业务数据：自定义分类 + 提示词 + 标签
        var customCat = new Category { Name = "自定义分类" };
        db.Categories.Add(customCat);
        await db.SaveChangesAsync();
        var prompt = new Prompt { Title = "p", Content = "c", CategoryId = customCat.Id };
        var promptSvc = new PromptService(db);
        await promptSvc.CreateAsync(prompt, new[] { "标签A" });

        // 执行重置
        await DatabaseInitializer.ResetToDefaultsAsync(db);

        // 自定义业务数据已清空（被内置示例取代，不再残留）
        Assert.Null(await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Title == "p"));
        Assert.Null(await db.Tags.AsNoTracking().FirstOrDefaultAsync(t => t.Name == "标签A"));
        Assert.Null(await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name == "自定义分类"));

        // 3 个内置分类按固定 Id/名称恢复（回归修复前的 bug：EnsureCreated 不重放种子导致分类全丢）
        var cats = await db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(3, cats.Count);
        Assert.Equal(DatabaseInitializer.SeedCategories.Select(s => s.Name), cats.Select(c => c.Name));
        Assert.Equal(new[] { 1, 2, 3 }, cats.Select(c => c.Id));

        // 9 条示例提示词 + 8 个内置标签 + 9 条关联一并恢复，且分类分布为 3/3/3
        Assert.Equal(9, await db.Prompts.CountAsync());
        Assert.Equal(8, await db.Tags.CountAsync());
        Assert.Equal(9, await db.PromptTags.CountAsync());
        Assert.Equal(
            new[] { 3, 3, 3 },
            (await db.Prompts.AsNoTracking().GroupBy(p => p.CategoryId)
                .Select(g => g.Count()).ToListAsync()).OrderBy(x => x).ToArray());

        // 自增序列未坏：重置后新建分类不与种子 Id=1..3 冲突即可。
        // 注意 SQLite AUTOINCREMENT 序列单调不回退（重置前已用过 4，这里会拿到 5），属正常语义。
        var created = await new CategoryService(db).CreateAsync(new Category { Name = "新分类" });
        var allCats = await db.Categories.AsNoTracking().OrderBy(c => c.Id).Select(c => $"{c.Id}:{c.Name}").ToListAsync();
        Assert.Equal(4, allCats.Count); // 3 个种子 + 1 个新建
        Assert.True(created.Id > 3, $"新分类 Id={created.Id} 与种子冲突；表内现有：{string.Join(",", allCats)}");
    }
}