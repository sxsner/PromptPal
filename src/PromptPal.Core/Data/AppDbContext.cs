using Microsoft.EntityFrameworkCore;
using PromptPal.Core.Entities;

namespace PromptPal.Core.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PromptTag> PromptTags => Set<PromptTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ========== Prompt ↔ Tag 多对多（强类型连接表）==========
        modelBuilder.Entity<Prompt>()
            .HasMany(p => p.Tags)
            .WithMany(t => t.Prompts)
            .UsingEntity<PromptTag>(
                // 必须显式绑定 PromptTag.Tag/Prompt 导航属性；否则 EF 约定会为这两个导航
                // 再建一套关系并生成 shadow 外键 TagId1/PromptId1（EF10625 警告 + 库内遗留空列）。
                pt => pt.HasOne(j => j.Tag).WithMany().HasForeignKey(j => j.TagId),
                pt => pt.HasOne(j => j.Prompt).WithMany().HasForeignKey(j => j.PromptId),
                pt =>
                {
                    pt.ToTable("PromptTags");
                    pt.HasKey(j => new { j.PromptId, j.TagId });
                });

        // ========== 种子数据（定义集中在 DatabaseInitializer.SeedCategories，重置流程共用）==========
        modelBuilder.Entity<Category>().HasData(DatabaseInitializer.SeedCategories.Cast<object>().ToArray());

        // ========== 索引 ==========
        // LastUsedAt 单列：「最近使用」按 LastUsedAt IS NOT NULL 过滤
        modelBuilder.Entity<Prompt>().HasIndex(p => p.LastUsedAt);
        // 复合索引（高频查询：按分类+收藏筛选）
        // 注意：不再单列建 CategoryId/IsFavorite 索引——
        // CategoryId 已是该复合索引的最左前缀；布尔列基数极低，单列索引基本无用。
        modelBuilder.Entity<Prompt>().HasIndex(p => new { p.CategoryId, p.IsFavorite, p.LastUsedAt });
    }
}
