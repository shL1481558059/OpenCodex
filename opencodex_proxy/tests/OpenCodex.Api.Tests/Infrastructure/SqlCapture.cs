using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests.Infrastructure;

/// <summary>
/// 测试用 SQL 捕获器：收集 EF 下发的所有命令文本，供 SQL 层面的验收断言使用。
/// 同时覆盖 Reader（SELECT）、NonQuery（ExecuteDelete/ExecuteUpdate/INSERT/UPDATE/DELETE）
/// 与 Scalar（COUNT/聚合）三类命令，解决此前只抓 ReaderExecuting 漏掉 NonQuery 的问题。
/// </summary>
public sealed class SqlCapture : DbCommandInterceptor
{
    private readonly object _sync = new();
    private readonly List<string> _commands = [];

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_sync)
            {
                return _commands.ToArray();
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _commands.Clear();
        }
    }

    /// <summary>按关键词统计命令条数（不区分大小写）。</summary>
    public int CountMatching(string keyword)
    {
        return Commands.Count(command => command.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>SELECT 条数（按去掉空白后的首个关键字判定）。</summary>
    public int SelectCount => CountByKeyword("SELECT");

    /// <summary>DELETE 条数（含 ExecuteDelete 生成的 DELETE）。</summary>
    public int DeleteCount => CountByKeyword("DELETE");

    /// <summary>UPDATE 条数（含 ExecuteUpdate 生成的 UPDATE）。</summary>
    public int UpdateCount => CountByKeyword("UPDATE");

    public void AssertNoColumn(string column)
    {
        Assert.DoesNotContain(
            Commands,
            command => command.Contains(column, StringComparison.OrdinalIgnoreCase));
    }

    public void AssertContains(string fragment)
    {
        Assert.Contains(
            Commands,
            command => command.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 返回去掉空白后以指定关键字开头的所有命令原文（不区分大小写），
    /// 供只针对某类语句（如 UPDATE）的精确断言使用。
    /// </summary>
    public IReadOnlyList<string> StatementsStartingWith(string keyword)
    {
        return Commands
            .Where(command => StartsWithKeyword(command, keyword))
            .ToArray();
    }

    /// <summary>
    /// 按去掉空白后的首个关键字判语句类型，避免 EF 生成的换行/缩进/RETURNING 子句干扰。
    /// </summary>
    private int CountByKeyword(string keyword)
    {
        return StatementsStartingWith(keyword).Count;
    }

    private static bool StartsWithKeyword(string command, string keyword)
    {
        var compact = new string(
            command
                .Where(character => !char.IsWhiteSpace(character))
                .Take(keyword.Length)
                .ToArray());
        return string.Equals(compact, keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 造一个挂了 <see cref="SqlCapture"/> 的 SQLite context，供测试注入。
    /// </summary>
    public static OpenCodexSqliteDbContext CreateCapturingContext(
        string connectionString,
        SqlCapture capture)
    {
        var builder = new DbContextOptionsBuilder<OpenCodexSqliteDbContext>();
        OpenCodexDbContextFactory.ConfigureSqlite(builder, connectionString);
        builder.AddInterceptors(capture);
        return new OpenCodexSqliteDbContext(builder.Options);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Capture(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command.CommandText);
        return new ValueTask<InterceptionResult<DbDataReader>>(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Capture(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command.CommandText);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Capture(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command.CommandText);
        return new ValueTask<InterceptionResult<object>>(result);
    }

    private void Capture(string commandText)
    {
        lock (_sync)
        {
            _commands.Add(commandText);
        }
    }
}
