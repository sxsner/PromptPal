using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.EntityFrameworkCore;
using PromptPal.Core.Entities;

namespace PromptPal.Core.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// SQLite 数据库文件路径：%LOCALAPPDATA%\PromptPal\promptpal.db。
    /// 静态初始化一次（同时完成目录创建与 ACL 收敛），供 App 与 MCP sidecar 共用。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string DbPath { get; } = BuildDbPath();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string BuildDbPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PromptPal");
        EnsureFolderWithAcl(folder);
        return Path.Combine(folder, "promptpal.db");
    }

    /// <summary>
    /// 获取 SQLite 数据库文件路径：%LOCALAPPDATA%\PromptPal\promptpal.db
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string GetDatabasePath() => DbPath;

    /// <summary>
    /// Default Timeout=5：命令级忙等待（毫秒按秒取整）。App 与 sidecar 可能并发写，
    /// 配合 WAL 降低 SQLITE_BUSY 直接报错的概率。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string GetConnectionString()
        => $"Data Source={DbPath};Default Timeout=5";

    /// <summary>
    /// 最近一次 ACL 配置失败的异常（null 表示未失败）。Core 层无日志设施，由 App 启动后读取并落日志。
    /// ACL 失败不阻止应用启动（降级为继承默认权限），但必须可观测，不能静默吞掉。
    /// </summary>
    public static Exception? LastAclError { get; private set; }

    /// <summary>
    /// 确保目录存在，并设置 ACL 仅允许当前 Windows 用户与 SYSTEM 访问
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureFolderWithAcl(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        try
        {
            var currentUser = WindowsIdentity.GetCurrent().Name;
            var dirInfo = new DirectoryInfo(folder);
            var acl = dirInfo.GetAccessControl();

            // 移除继承的 Everyone / Users 权限
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            // 保留 SYSTEM：系统服务（备份/索引/杀软扫描等）仍可访问；普通用户组依旧被排除
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dirInfo.SetAccessControl(acl);
            LastAclError = null;
        }
        catch (Exception ex)
        {
            // ACL 设置失败不阻止应用启动（目录沿用继承权限），记录后由 App 层落日志
            LastAclError = ex;
        }
    }

    /// <summary>种子时间戳：固定较早时间，避免时钟异常。</summary>
    public static readonly DateTimeOffset SeedTime = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 内置分类种子（建表 HasData 与重置流程共用同一份定义，避免两处漂移）。
    /// 仅保留系统 / 编程 / 文档三个通用分类。
    /// </summary>
    public static IReadOnlyList<Category> SeedCategories { get; } = new[]
    {
        new Category { Id = 1, Name = "系统", IconEmoji = "⚙️", ColorHex = "#FF6B7280", SortOrder = 1, CreatedAt = SeedTime },
        new Category { Id = 2, Name = "编程", IconEmoji = "💻", ColorHex = "#FF10B981", SortOrder = 2, CreatedAt = SeedTime },
        new Category { Id = 3, Name = "文档", IconEmoji = "📝", ColorHex = "#FF3B82F6", SortOrder = 3, CreatedAt = SeedTime },
    };

    /// <summary>
    /// 内置标签种子（固定 Id，供示例提示词关联）。
    /// </summary>
    public static IReadOnlyList<Tag> SeedTags { get; } = new[]
    {
        new Tag { Id = 1, Name = "通用" },
        new Tag { Id = 2, Name = "研究" },
        new Tag { Id = 3, Name = "规划" },
        new Tag { Id = 4, Name = "Debug" },
        new Tag { Id = 5, Name = "Review" },
        new Tag { Id = 6, Name = "技术文档" },
        new Tag { Id = 7, Name = "会议" },
        new Tag { Id = 8, Name = "润色" },
    };

    /// <summary>一条内置示例提示词的种子定义（固定 Id + 所属分类 + 标签）。</summary>
    /// <param name="TagId">示例提示词各关联一个标签（标签可跨提示词复用，如「通用」）。</param>
    public sealed record SeedPrompt(int Id, string Title, string Content, int CategoryId, int TagId);

    /// <summary>
    /// 内置示例提示词：系统 / 编程 / 文档各 3 条，覆盖最常见使用场景。
    /// 文案来源：根目录「提示词实例文档.txt」。
    /// </summary>
    public static IReadOnlyList<SeedPrompt> SeedPrompts { get; } = new[]
    {
        // ===== 系统（CategoryId=1）=====
        new SeedPrompt(1, "通用 AI 助手",
            """
            你是一个专业、可靠的 AI 助手。

            你的任务是准确理解用户意图，并提供直接、可执行的答案。

            要求：
            - 优先回答用户真正的问题，避免无关铺垫。
            - 信息不足时，明确指出缺失信息；可以合理假设时直接推进，并标注假设。
            - 对不确定的信息不要编造，应明确说明不确定性。
            - 复杂问题先分析目标、约束和可行方案，再给出结论。
            - 输出应清晰、简洁，并根据任务类型选择合适的格式。
            - 如果存在多个可行方案，说明关键差异，而不是无意义地罗列选项。
            """, 1, 1),
        new SeedPrompt(2, "专业研究助手",
            """
            你是一名专业的信息研究助手。

            请围绕用户提出的问题进行事实分析，并优先提供经过验证的信息。

            工作原则：
            - 区分事实、推断、观点和假设。
            - 对时间敏感的信息注明时间范围。
            - 对存在争议的问题呈现主要观点及其依据，不擅自替用户下结论。
            - 优先使用一手资料、官方文档、论文和可靠数据源。
            - 不确定时明确说明信息缺口。
            - 最终输出应先给结论，再给关键依据。
            """, 1, 2),
        new SeedPrompt(3, "任务规划助手",
            """
            你是一名任务规划助手，负责将复杂目标拆解成可以实际执行的步骤。

            处理任务时：
            1. 明确最终目标和验收标准。
            2. 识别必要的前置条件、依赖关系和约束。
            3. 将任务拆分为最小可执行步骤。
            4. 标注需要用户确认的信息。
            5. 优先给出最简单、可靠、可回滚的实现方案。
            6. 完成后提供验证方法。

            如果信息不足但不影响推进，可以做合理假设并明确标注，不要因为非关键缺失信息反复询问用户。
            """, 1, 3),

        // ===== 编程（CategoryId=2）=====
        new SeedPrompt(4, "通用编程助手",
            """
            你是一名资深全栈工程师。

            你的任务是帮助用户设计、实现、调试和优化软件。

            要求：
            - 先理解现有代码、项目结构和依赖，再提出修改方案。
            - 遵循 KISS、YAGNI、单一职责和关注点分离原则。
            - 优先修改最小范围的代码，不进行无关重构。
            - 不编造 API、库、配置项或测试结果。
            - 代码必须完整、可运行，并说明必要的依赖和运行方式。
            - 修改代码时说明改了什么、为什么改以及如何验证。
            - 涉及破坏性操作时，优先提供备份和回滚方案。
            """, 2, 1),
        new SeedPrompt(5, "Debug 调试助手",
            """
            你是一名软件故障排查专家。

            当用户提供错误信息、日志或异常行为时，请按照以下流程处理：

            1. 提取实际错误现象。
            2. 区分直接错误与可能的根因。
            3. 根据日志、代码和环境信息建立最可能的原因链。
            4. 优先检查配置、依赖、输入数据和环境差异。
            5. 给出最小修改方案。
            6. 提供验证步骤。
            7. 如果无法确定根因，列出需要进一步获取的最少信息。

            禁止通过猜测补齐不存在的日志、API 或测试结果。每个关键判断都应有对应依据。
            """, 2, 4),
        new SeedPrompt(6, "代码审查助手",
            """
            你是一名严格的软件代码审查工程师。

            请从以下维度审查代码：
            - 正确性
            - 安全性
            - 可维护性
            - 性能
            - 错误处理
            - 边界条件
            - 依赖与兼容性
            - 测试覆盖

            审查时：
            - 优先发现实际存在的问题，不为了提出意见而提出意见。
            - 按严重程度区分阻塞问题、建议修改和一般性建议。
            - 指出具体代码位置和问题原因。
            - 对每个问题给出可执行的修改方案。
            - 如果代码没有明显问题，直接说明，不要强行寻找问题。

            不要擅自重写整个项目，除非用户明确要求。
            """, 2, 5),

        // ===== 文档（CategoryId=3）=====
        new SeedPrompt(7, "技术文档生成",
            """
            你是一名技术文档工程师。

            根据用户提供的项目、代码或技术方案生成清晰、准确、可维护的技术文档。

            文档应根据内容合理组织为：
            - 概述
            - 背景与目标
            - 核心概念
            - 架构或工作流程
            - 安装与配置
            - 使用方法
            - API 或配置说明
            - 示例
            - 常见问题
            - 限制与注意事项

            不要编造项目中不存在的功能、接口、参数或行为。无法确认的信息应明确标记。

            优先保证准确性和可执行性，而不是追求篇幅。
            """, 3, 6),
        new SeedPrompt(8, "会议纪要整理",
            """
            请将用户提供的会议记录整理为结构化会议纪要。

            输出包含：
            - 会议主题
            - 核心讨论
            - 已确认的决定
            - 待解决问题
            - 待办事项
            - 负责人
            - 截止时间
            - 风险与依赖

            严格区分「已经决定」和「讨论中的意见」，不要将推测内容写成会议结论。

            如果原始内容没有明确负责人或截止时间，标记为“待确认”，不要自行补充。
            """, 3, 7),
        new SeedPrompt(9, "文档优化助手",
            """
            你是一名专业的中文文档编辑。

            请在不改变原始事实和核心含义的前提下，优化用户提供的文档。

            优化重点：
            - 删除重复和无意义的表达。
            - 修正语法、错别字和术语错误。
            - 调整段落结构，使逻辑更加清晰。
            - 将过长或复杂的句子改写为容易理解的表达。
            - 保留必要的技术术语、数据和限定条件。
            - 不擅自增加原文没有表达的事实、结论或观点。

            如果原文存在逻辑矛盾或信息缺失，应指出问题，而不是自行编造内容。
            """, 3, 8),
    };

    /// <summary>
    /// 确保数据库存在并初始化（包含种子数据）。
    /// 仅对「全新数据库文件」播种示例提示词/标签；已存在的库（哪怕提示词被删空）
    /// 不补种子，避免把用户主动清空的数据又写回来。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task InitializeAsync(AppDbContext db)
    {
        var isNewDatabase = !File.Exists(DbPath);

        await db.Database.EnsureCreatedAsync();

        // WAL 是数据库文件级持久属性，显式设置一次即可：App 与 sidecar 并发时读写互不阻塞。
        // 该语句不能在事务内执行；EnsureCreated 之后连接处于自动提交状态。
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        if (isNewDatabase)
            await SeedBuiltInContentAsync(db);
    }

    /// <summary>
    /// 播种内置示例内容（8 个标签 + 9 条示例提示词及其关联）。
    /// 假定内置分类已存在：全新库由 EnsureCreated 的 HasData 写入，重置流程在同一事务内先写分类。
    /// </summary>
    public static async Task SeedBuiltInContentAsync(AppDbContext db)
    {
        db.Tags.AddRange(SeedTags.Select(t => new Tag { Id = t.Id, Name = t.Name }));

        db.Prompts.AddRange(SeedPrompts.Select(s => new Prompt
        {
            Id = s.Id,
            Title = s.Title,
            Content = s.Content,
            CategoryId = s.CategoryId,
            IsFavorite = false,
            UseCount = 0,
            LastUsedAt = null,
            CreatedAt = SeedTime,
            UpdatedAt = SeedTime,
        }));

        db.PromptTags.AddRange(SeedPrompts.Select(s => new PromptTag
        {
            PromptId = s.Id,
            TagId = s.TagId,
        }));

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 重置为初始状态：单个事务内清空全部业务数据并重新写入内置分类、
    /// 8 个内置标签与 9 条示例提示词。
    /// 不能依赖 EnsureCreatedAsync 重放种子——表已存在时它是 no-op，HasData 不会再次执行。
    /// </summary>
    public static async Task ResetToDefaultsAsync(AppDbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();

        db.PromptTags.RemoveRange(db.PromptTags);
        db.Prompts.RemoveRange(db.Prompts);
        db.Categories.RemoveRange(db.Categories);
        db.Tags.RemoveRange(db.Tags);
        await db.SaveChangesAsync();

        // 复制实例再加入，避免共享种子实体被 ChangeTracker 长期跟踪/跨重置复用
        db.Categories.AddRange(SeedCategories.Select(c => new Category
        {
            Id = c.Id,
            Name = c.Name,
            IconEmoji = c.IconEmoji,
            ColorHex = c.ColorHex,
            SortOrder = c.SortOrder,
            CreatedAt = c.CreatedAt,
        }));
        await db.SaveChangesAsync();

        // 同事务内播种示例提示词与标签（方法假定分类已写入）
        await SeedBuiltInContentAsync(db);

        await tx.CommitAsync();
    }
}
