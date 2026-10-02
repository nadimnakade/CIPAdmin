using System.Data;
using Microsoft.Data.SqlClient;

namespace CIPAdmin.Backend.Services;

/// <summary>Thin ADO.NET wrapper around the shared connection string.</summary>
public interface IDbContext
{
    string ConnectionString { get; }
    DataTable Query(string sql, Action<SqlParameterCollection>? bind = null);
    Task<DataTable> QueryAsync(string sql, Action<SqlParameterCollection>? bind = null, CancellationToken ct = default);
    DataSet QuerySet(string sql, Action<SqlParameterCollection>? bind = null);
    Task<DataSet> QuerySetAsync(string sql, Action<SqlParameterCollection>? bind = null, CancellationToken ct = default, int commandTimeout = 60);
    int Execute(string sql, Action<SqlParameterCollection>? bind = null);
    Task<int> ExecuteAsync(string sql, Action<SqlParameterCollection>? bind = null, CancellationToken ct = default);
    object? Scalar(string sql, Action<SqlParameterCollection>? bind = null);
    Task<object?> ScalarAsync(string sql, Action<SqlParameterCollection>? bind = null, CancellationToken ct = default);
}

public class DbContext : IDbContext
{
    private readonly IConfiguration _config;

    public DbContext(IConfiguration config) => _config = config;

    public string ConnectionString => _config.GetConnectionString("Default")!;

    private SqlConnection Open()
    {
        var conn = new SqlConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    public DataTable Query(string sql, Action<SqlParameterCollection>? bind = null)
    {
        var ds = QuerySet(sql, bind);
        return ds.Tables.Count > 0 ? ds.Tables[0] : new DataTable();
    }

    public async Task<DataTable> QueryAsync(string sql, Action<SqlParameterCollection>? bind = null, CancellationToken ct = default)
    {
        var ds = await QuerySetAsync(sql, bind, ct).ConfigureAwait(false);
        return ds.Tables.Count > 0 ? ds.Tables[0] : new DataTable();
    }

    public DataSet QuerySet(string sql, Action<SqlParameterCollection>? bind = null)
        => QuerySetAsync(sql, bind).GetAwaiter().GetResult();

    public async Task<DataSet> QuerySetAsync(string sql, Action<SqlParameterCollection>? bind = null, CancellationToken ct = default, int commandTimeout = 60)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeout };
        bind?.Invoke(cmd.Parameters);
        var ds = new DataSet();
        using var da = new SqlDataAdapter(cmd);
        da.Fill(ds);
        await Task.CompletedTask;
        return ds;
    }

    public int Execute(string sql, Action<SqlParameterCollection>? bind = null)
        => ExecuteAsync(sql, bind).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(string sql, Action<SqlParameterCollection>? bind = null, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        bind?.Invoke(cmd.Parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public object? Scalar(string sql, Action<SqlParameterCollection>? bind = null)
        => ScalarAsync(sql, bind).GetAwaiter().GetResult();

    public async Task<object?> ScalarAsync(string sql, Action<SqlParameterCollection>? bind = null, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        bind?.Invoke(cmd.Parameters);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
