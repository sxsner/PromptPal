using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PromptPal.Core.Data;
using PromptPal.Core.Entities;
using PromptPal.Core.Services;

namespace PromptPal.Core.Tests;

public class DbTransferServiceTests
{
    private static AppDbContext CreateFileContext(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static string NewDbFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pp_dbtransfer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public async Task ExportToFile_CreatesStandaloneSnapshot_WithAllRows()
    {
        var mainFile = NewDbFile("main.db");
        using (var db = CreateFileContext(mainFile))
        {
            var svc = new PromptService(db);
            await svc.CreateAsync(new Prompt { Title = "导出测试", Content = "正文", CategoryId = 1 },
                ["标签A", "标签B"]);
            await svc.IncrementUseAsync((await db.Prompts.SingleAsync(p => p.Title == "导出测试")).Id);

            await new DbTransferService(db).ExportToFileAsync(
                Path.Combine(Path.GetDirectoryName(mainFile)!, "snapshot.db"));
        }

        var snap = Path.Combine(Path.GetDirectoryName(mainFile)!, "snapshot.db");
        Assert.True(File.Exists(snap));
        // 快照必须自包含：不产生 -wal/-shm 附属文件
        Assert.False(File.Exists(snap + "-wal"));
        Assert.False(File.Exists(snap + "-shm"));

        // 快照能独立打开并读到全部数据
        using (var raw = new SqliteConnection($"Data Source={snap};Mode=ReadOnly;Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText =
                "SELECT (SELECT COUNT(*) FROM Categories),(SELECT COUNT(*) FROM Prompts)," +
                "(SELECT COUNT(*) FROM Tags),(SELECT COUNT(*) FROM PromptTags)," +
                "(SELECT UseCount FROM Prompts WHERE Title='导出测试')";
            using var r = cmd.ExecuteReader();
            r.Read();
            Assert.Equal(3, r.GetInt32(0));
            Assert.Equal(1, r.GetInt32(1));
            Assert.Equal(2, r.GetInt32(2));
            Assert.Equal(2, r.GetInt32(3));
            Assert.Equal(1, r.GetInt32(4));
        }

        // 快照可作为"新机旧库"直接走启动流程：EnsureCreated 幂等、无重复种子
        using (var imported = CreateFileContext(snap))
            Assert.Equal(3, await imported.Categories.CountAsync());
    }

    [Fact]
    public async Task ImportFromFile_ReplacesAll_PreservesIdsAndAutoincrementSequence()
    {
        // 当前库：内置种子
        var targetFile = NewDbFile("target.db");
        using (var db = CreateFileContext(targetFile))
            Assert.Equal(3, await db.Categories.CountAsync());

        // 源库：清空后放一套完全不同的数据（显式 Id，含大 Id 以验证自增序列）
        var sourceFile = NewDbFile("source.db");
        using (var src = CreateFileContext(sourceFile))
        {
            src.PromptTags.RemoveRange(src.PromptTags);
            src.Prompts.RemoveRange(src.Prompts);
            src.Categories.RemoveRange(src.Categories);
            src.Tags.RemoveRange(src.Tags);
            await src.SaveChangesAsync();
            src.Categories.AddRange(
                new Category { Id = 10, Name = "源分类甲", SortOrder = 1, CreatedAt = DatabaseInitializer.SeedTime },
                new Category { Id = 11, Name = "源分类乙", SortOrder = 2, CreatedAt = DatabaseInitializer.SeedTime });
            src.Tags.Add(new Tag { Id = 20, Name = "源标签" });
            src.Prompts.Add(new Prompt
            {
                Id = 100, Title = "源提示词", Content = "c", CategoryId = 10,
                UseCount = 9, CreatedAt = DatabaseInitializer.SeedTime, UpdatedAt = DatabaseInitializer.SeedTime
            });
            src.PromptTags.Add(new PromptTag { PromptId = 100, TagId = 20 });
            await src.SaveChangesAsync();
        }

        using (var db = CreateFileContext(targetFile))
        {
            await new DbTransferService(db).ImportFromFileAsync(sourceFile);
        }

        // 目标库整体变成源库内容（用新上下文验证，避开 EF 变更跟踪缓存）
        using (var verify = CreateFileContext(targetFile))
        {
            var catNames = await verify.Categories.OrderBy(c => c.Id).Select(c => c.Name).ToListAsync();
            Assert.Equal(new[] { "源分类甲", "源分类乙" }, catNames);
            Assert.Equal(100, await verify.Prompts.Select(p => p.Id).SingleAsync());
            Assert.Equal("源提示词", await verify.Prompts.Select(p => p.Title).SingleAsync());
            Assert.Equal(20, await verify.Tags.Select(t => t.Id).SingleAsync());
            Assert.Single(verify.PromptTags);
        }

        // 自增序列已同步：新建提示词的 Id 必须接续源库（101），不会与导入行撞 Id
        using (var app = CreateFileContext(targetFile))
        {
            app.Prompts.Add(new Prompt { Title = "新行", Content = "c" });
            await app.SaveChangesAsync();
            Assert.Equal(101, await app.Prompts.Where(p => p.Title == "新行").Select(p => p.Id).SingleAsync());
        }

        // 导入前自动留了 .bak 备份
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(targetFile)!, "target.db.bak_*"));
    }

    [Fact]
    public async Task ImportFromFile_TextFile_Throws_And_LeavesDataUnchanged()
    {
        var targetFile = NewDbFile("target.db");
        var bad = NewDbFile("fake.db");
        await File.WriteAllTextAsync(bad, "这根本不是数据库");

        using var db = CreateFileContext(targetFile);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DbTransferService(db).ImportFromFileAsync(bad));
        Assert.Equal(3, await db.Categories.CountAsync());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(targetFile)!, "target.db.bak_*"));
    }

    [Fact]
    public async Task ImportFromFile_MissingBusinessTable_Throws_And_LeavesDataUnchanged()
    {
        var targetFile = NewDbFile("target.db");
        var alien = NewDbFile("alien.db");
        using (var raw = new SqliteConnection($"Data Source={alien};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "CREATE TABLE x(y); INSERT INTO x VALUES(1);";
            cmd.ExecuteNonQuery();
        }

        using var db = CreateFileContext(targetFile);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DbTransferService(db).ImportFromFileAsync(alien));
        Assert.Contains("PromptPal", ex.Message);
        Assert.Equal(3, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task ExportToFile_OverwritesExistingFile()
    {
        var mainFile = NewDbFile("main.db");
        var outFile = Path.Combine(Path.GetDirectoryName(mainFile)!, "snapshot.db");
        await File.WriteAllTextAsync(outFile, "旧内容");

        using var db = CreateFileContext(mainFile);
        await new DbTransferService(db).ExportToFileAsync(outFile);

        using var raw = new SqliteConnection($"Data Source={outFile};Mode=ReadOnly;Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Categories";
        Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar()));
    }
}
