using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace LicenseServer;

public record UserInfo(int Id, string Login, string? FullName);
public record BlacklistEntry(int Id, string? Login, string? IpAddress, string? Reason, DateTime AddedAt);

/// <summary>Работа с базой данных MSSQL (пользователи, чёрный список, журнал подключений).</summary>
public class Database
{
    private readonly string _connectionString;

    public Database(string connectionString) => _connectionString = connectionString;

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    public virtual async Task<bool> TestConnectionAsync()
    {
        try
        {
            await using var conn = await OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Не удалось подключиться к БД: {ex.Message}");
            return false;
        }
    }

    // ---------- Пароли ----------

    /// <summary>SHA-256 от (salt + password) в UTF-16LE — совпадает с HASHBYTES('SHA2_256', N'...') в MSSQL.</summary>
    public static byte[] HashPassword(string salt, string password) =>
        SHA256.HashData(Encoding.Unicode.GetBytes(salt + password));

    private static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    // ---------- Пользователи ----------

    /// <summary>Проверяет логин и пароль. Возвращает пользователя или null.</summary>
    public virtual async Task<UserInfo?> AuthenticateAsync(string login, string password)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id, Login, FullName, PasswordHash, Salt FROM dbo.Users WHERE Login = @login", conn);
        cmd.Parameters.AddWithValue("@login", login);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        var storedHash = (byte[])r["PasswordHash"];
        var salt = (string)r["Salt"];
        var actualHash = HashPassword(salt, password);

        if (!CryptographicOperations.FixedTimeEquals(storedHash, actualHash)) return null;

        return new UserInfo(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    public virtual async Task<bool> AddUserAsync(string login, string password, string? fullName)
    {
        var salt = NewSalt();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Login = @login) " +
            "INSERT INTO dbo.Users (Login, PasswordHash, Salt, FullName) VALUES (@login, @hash, @salt, @name)", conn);
        cmd.Parameters.AddWithValue("@login", login);
        cmd.Parameters.AddWithValue("@hash", HashPassword(salt, password));
        cmd.Parameters.AddWithValue("@salt", salt);
        cmd.Parameters.AddWithValue("@name", (object?)fullName ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public virtual async Task<List<UserInfo>> GetUsersAsync()
    {
        var list = new List<UserInfo>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT Id, Login, FullName FROM dbo.Users ORDER BY Id", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new UserInfo(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }

    // ---------- Чёрный список ----------

    /// <summary>Возвращает запись чёрного списка, если заблокирован логин или IP, иначе null.</summary>
    public virtual async Task<BlacklistEntry?> FindInBlacklistAsync(string? login, string ip)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 Id, Login, IpAddress, Reason, AddedAt FROM dbo.Blacklist " +
            "WHERE (Login IS NOT NULL AND Login = @login) OR (IpAddress IS NOT NULL AND IpAddress = @ip)", conn);
        cmd.Parameters.AddWithValue("@login", (object?)login ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ip", ip);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadBlacklist(r) : null;
    }

    public virtual async Task AddToBlacklistAsync(string? login, string? ip, string? reason)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "INSERT INTO dbo.Blacklist (Login, IpAddress, Reason) VALUES (@login, @ip, @reason)", conn);
        cmd.Parameters.AddWithValue("@login", (object?)login ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ip", (object?)ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Удаляет из чёрного списка все записи с указанным логином или IP. Возвращает число удалённых.</summary>
    public virtual async Task<int> RemoveFromBlacklistAsync(string loginOrIp)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM dbo.Blacklist WHERE Login = @v OR IpAddress = @v", conn);
        cmd.Parameters.AddWithValue("@v", loginOrIp);
        return await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task<List<BlacklistEntry>> GetBlacklistAsync()
    {
        var list = new List<BlacklistEntry>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id, Login, IpAddress, Reason, AddedAt FROM dbo.Blacklist ORDER BY Id", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadBlacklist(r));
        return list;
    }

    private static BlacklistEntry ReadBlacklist(SqlDataReader r) => new(
        r.GetInt32(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetDateTime(4));

    // ---------- Журнал подключений ----------

    public virtual async Task<int> LogConnectAsync(int? userId, string? login, string ip, int port, string? code, DateTime connectedAt)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "INSERT INTO dbo.ConnectionLog (UserId, Login, IpAddress, Port, LicenseCode, ConnectedAt) " +
            "OUTPUT INSERTED.Id VALUES (@uid, @login, @ip, @port, @code, @at)", conn);
        cmd.Parameters.AddWithValue("@uid", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@login", (object?)login ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ip", ip);
        cmd.Parameters.AddWithValue("@port", port);
        cmd.Parameters.AddWithValue("@code", (object?)code ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", connectedAt);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public virtual async Task LogCodeChangedAsync(int logId, string code)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("UPDATE dbo.ConnectionLog SET LicenseCode = @code WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@code", code);
        cmd.Parameters.AddWithValue("@id", logId);
        await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task LogDisconnectAsync(int logId, string reason)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE dbo.ConnectionLog SET DisconnectedAt = SYSDATETIME(), DisconnectReason = @reason WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@reason", reason);
        cmd.Parameters.AddWithValue("@id", logId);
        await cmd.ExecuteNonQueryAsync();
    }
}
