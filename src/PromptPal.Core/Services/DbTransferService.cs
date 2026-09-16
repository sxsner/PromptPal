using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PromptPal.Core.Data;

namespace PromptPal.Core.Services;

/// <summary>
/// 数据库文件级导入导出（用户换机/备份的主链路）：
/// 导出 = VACUUM INTO 生成单文件一致性快照（含已提交事务、无 -wal 依赖，可直接当 promptpal.db 用）；
/// 导入 = ATTACH 源库后在单事务内整体替换 4 张业务表，保持 Id 与自增序列不变，导入前自动 .bak 备份。
/// </summary>
public interface IDbTransferService
{
    /// <summary>导入文件大小上限（字节）：UI 层在读文件前据此预检，避免误选超大文件。</summary>
    public const long MaxImportBytes = 100 * 1024 * 1024; // 100 MB

    /// <summary>把当前库完整导出为单个 .db 文件（目标已存在则覆盖）。</summary>
    Task ExportToFileAsync(string targetPath);

    /// <summary>
    /// 从另一个 PromptPal .db 文件整体导入（覆盖当前全部数据）。
    /// 文件非法/版本不兼容时抛异常且当前数据不变；成功导入前自动生成 .bak 备份。
    /// </summary>
    Task ImportFromFileAsync(string sourcePath);
}

public sealed class DbTransferService : IDbTransferService
{
    private readonly AppDbContext _db;
    public DbTransferService(AppDbContext db) => _db = db;

    // 删除/插入顺序必须遵守外键：Prompts→Categories，PromptTags→(Prompts, Tags)
    private static readonly string[] TablesInFkOrder = ["Categories", "Tags", "Prompts", "PromptTags"];
    private static readonly string[] DeleteOrder = ["PromptTags", "Prompts", "Tags", "Categories"];

    private const int BackupKeepCount = 5;

    public async Task ExportToFileAsync(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("导出路径不能为空", nameof(targetPath));

        var full = Path.GetFullPath(targetPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // VACUUM INTO 要求目标文件不存在；它在 SQLite 内部读一致性快照，
        // WAL 下也能拿到全部已提交事务，生成的库无 -wal/-shm 依赖，可直接拷贝换机。
        // 该语句不能在事务内执行。
        if (File.Exists(full)) File.Delete(full);

        var conn = await OpenMainConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO " + ToSqlLiteral(full) + ";";
            await cmd.ExecuteNonQueryAsync();
        }

        // 产物自检：必须能独立打开且四张业务表齐全，防止把半截文件交给用户
        var schema = await ReadSchemaAsync(full);
        EnsureRequiredTables(schema);
    }

    public async Task ImportFromFileAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("导入路径不能为空", nameof(sourcePath));

        var full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full))
            throw new FileNotFoundException("找不到要导入的数据库文件", full);

        var fi = new FileInfo(full);
        if (fi.Length == 0)
            throw new ArgumentException("导入文件为空，不是有效的数据库文件");
        if (fi.Length > IDbTransferService.MaxImportBytes)
            throw new ArgumentException($"导入文件过大（{fi.Length / 1024 / 1024}MB），最大允许 100MB");

        // 1) 先校验源文件（只读独立连接）：非法文件在动当前库之前就被拒绝
        var sourceSchema = await ReadSchemaAsync(full);
        EnsureRequiredTables(sourceSchema);

        var conn = await OpenMainConnectionAsync();

        // 2) 与源做列结构比对：不同版本库（缺列/多列）不允许导入，避免 SELECT * 错位写花
        var mainSchema = await ReadSchemaAsync(conn, mainConnection: true);
        EnsureRequiredTables(mainSchema);
        foreach (var table in TablesInFkOrder)
        {
            if (!sourceSchema[table].Select(c => c.Name).SequenceEqual(mainSchema[table].Select(c => c.Name)))
                throw new InvalidOperationException(
                    $"导入文件的表「{table}」结构与当前版本不一致，可能来自不兼容的程序版本，已取消导入");
        }

        // 3) 备份当前库（VACUUM INTO；须在事务外）
        BackupCurrentDatabase(conn);

        // 4) ATTACH 同样不能在事务内执行
        await using var attach = conn.CreateCommand();
        attach.CommandText = "ATTACH DATABASE " + ToSqlLiteral(full) + " AS src;";
        await attach.ExecuteNonQueryAsync();

        try
        {
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                // 先清空（外键顺序），再按源 Id 原样写入
                foreach (var table in DeleteOrder)
                    await ExecAsync(conn, $"DELETE FROM main.{table};");

                foreach (var table in TablesInFkOrder)
                {
                    var cols = mainSchema[table];
                    var colList = string.Join(", ", cols.Select(c => QuoteIdent(c.Name)));
                    await ExecAsync(conn,
                        $"INSERT INTO main.{table} ({colList}) SELECT {colList} FROM src.{table};");
                }

                // AUTOINCREMENT 序列也要一起搬，否则导入后新建行会从旧 seq 继续而可能与导入 Id 冲突
                if (await AttachedTableExistsAsync(conn, "src", "sqlite_sequence"))
                {
                    await ExecAsync(conn, "DELETE FROM main.sqlite_sequence;");
                    await ExecAsync(conn,
                        "INSERT INTO main.sqlite_sequence (name, seq) SELECT name, seq FROM src.sqlite_sequence;");
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        finally
        {
            await using var detach = conn.CreateCommand();
            detach.CommandText = "DETACH DATABASE src;";
            await detach.ExecuteNonQueryAsync();
        }
    }

    // ========== 内部工具 ==========

    private async Task<DbConnection> OpenMainConnectionAsync()
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>读库中四张业务表的列定义（顺序为准）；文件不是 SQLite 或损坏时直接抛错。</summary>
    private static async Task<IReadOnlyDictionary<string, List<ColumnInfo>>> ReadSchemaAsync(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(cs);
        try
        {
            await conn.OpenAsync();

            // 损坏/非 SQLite 文件会在此处或 integrity_check 暴露
            await using var integrity = conn.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = await integrity.ExecuteScalarAsync() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("导入文件已损坏（integrity_check 未通过），已取消导入");
        }
        catch (SqliteException ex)
        {
            // 非 SQLite 文件、损坏文件、只读无法打开等都归为"文件无效"，当前库不动
            throw new InvalidOperationException("导入文件不是有效的 SQLite/PromptPal 数据库（已取消导入）", ex);
        }

        return await ReadSchemaAsync(conn, mainConnection: false);
    }

    private static async Task<IReadOnlyDictionary<string, List<ColumnInfo>>> ReadSchemaAsync(
        DbConnection conn, bool mainConnection)
    {
        var schema = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in TablesInFkOrder)
        {
            var prefix = mainConnection ? "main." : string.Empty;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA {prefix}table_info({QuoteIdent(table)});";
            var cols = new List<ColumnInfo>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                cols.Add(new ColumnInfo(reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
            schema[table] = cols;
        }
        return schema;
    }

    private static void EnsureRequiredTables(IReadOnlyDictionary<string, List<ColumnInfo>> schema)
    {
        foreach (var table in TablesInFkOrder)
        {
            if (!schema.TryGetValue(table, out var cols) || cols.Count == 0)
                throw new InvalidOperationException(
                    $"导入文件中缺少表「{table}」，不是有效的 PromptPal 数据库，已取消导入");
        }
    }

    private static async Task<bool> AttachedTableExistsAsync(DbConnection conn, string schemaName, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM " + QuoteIdent(schemaName) + ".sqlite_master " +
                          "WHERE type='table' AND name=$n LIMIT 1;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$n";
        p.Value = table;
        cmd.Parameters.Add(p);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private void BackupCurrentDatabase(DbConnection conn)
    {
        try
        {
            var dbFile = ParseDataSource(_db.Database.GetConnectionString() ?? string.Empty);
            if (string.IsNullOrEmpty(dbFile) || !File.Exists(dbFile)) return;

            var backupPath = $"{dbFile}.bak_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
            if (File.Exists(backupPath)) File.Delete(backupPath);

            // WAL 下 File.Copy 主库会丢 -wal 事务；VACUUM INTO 拿一致性快照，且须在事务外执行
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO " + ToSqlLiteral(backupPath) + ";";
            cmd.ExecuteNonQuery();

            PruneBackups(dbFile, BackupKeepCount);
        }
        catch
        {
            // 备份失败不阻止导入，尽力而为（导入本身是单事务，失败会回滚）
        }
    }

    /// <summary>从 ADO.NET 连接串解析 Data Source（连接串可能含 ;Default Timeout=5 等后缀，不能直接截取）。</summary>
    private static string? ParseDataSource(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>轮转备份：仅保留最新 keepCount 个 {dbFile}.bak_yyyyMMdd_HHmmss_fff。</summary>
    public static void PruneBackups(string dbFile, int keepCount)
    {
        var dir = Path.GetDirectoryName(dbFile);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        var backups = Directory.GetFiles(dir, Path.GetFileName(dbFile) + ".bak_*");
        foreach (var old in backups.OrderDescending().Skip(keepCount))
        {
            try { File.Delete(old); }
            catch { /* 尽力而为 */ }
        }
    }

    private static string ToSqlLiteral(string path) => "'" + path.Replace("'", "''") + "'";
    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private sealed record ColumnInfo(string Name, string Type, bool NotNull);
}
