using Microsoft.Data.Sqlite;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static readonly HashSet<string> UserRoles = new(StringComparer.Ordinal)
    {
        "superadmin",
        "user"
    };

    public static UserRecord EnsureSuperadmin(string dbPath, string username, string password)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            username = "admin";
        }

        Initialize(dbPath, username);

        var now = UnixTimeSeconds();
        using var connection = OpenConnection(dbPath);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var selectCommand = connection.CreateCommand();
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = "SELECT username FROM users WHERE username = $username";
            selectCommand.Parameters.AddWithValue("$username", username);
            var existing = selectCommand.ExecuteScalar();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (existing is null)
            {
                command.CommandText = """
                    INSERT INTO users (
                        username, password_hash, role, enabled, created_at, updated_at
                    ) VALUES (
                        $username, $password_hash, 'superadmin', 1, $created_at, $updated_at
                    )
                    """;
                command.Parameters.AddWithValue("$created_at", now);
            }
            else
            {
                command.CommandText = """
                    UPDATE users
                    SET password_hash = $password_hash,
                        role = 'superadmin',
                        enabled = 1,
                        updated_at = $updated_at
                    WHERE username = $username
                    """;
            }

            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$password_hash", HashPassword(password));
            command.Parameters.AddWithValue("$updated_at", now);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        return GetUser(dbPath, username) ?? throw new InvalidOperationException("failed to ensure superadmin user");
    }

    public static UserRecord CreateUser(
        string dbPath,
        string username,
        string password,
        string role = "user",
        bool enabled = true)
    {
        Initialize(dbPath);
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (!UserRoles.Contains(role))
        {
            throw new ArgumentException("role is invalid", nameof(role));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("password is required", nameof(password));
        }

        var now = UnixTimeSeconds();
        using var connection = OpenConnection(dbPath);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO users (
                    username, password_hash, role, enabled, created_at, updated_at
                ) VALUES (
                    $username, $password_hash, $role, $enabled, $created_at, $updated_at
                )
                """;
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$password_hash", HashPassword(password));
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$created_at", now);
            command.Parameters.AddWithValue("$updated_at", now);
            command.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ArgumentException("username already exists", nameof(username), exception);
        }

        return GetUser(dbPath, username) ?? throw new InvalidOperationException("failed to create user");
    }

    public static IReadOnlyList<UserRecord> ListUsers(string dbPath)
    {
        Initialize(dbPath);
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT username, role, enabled, created_at, updated_at
            FROM users
            ORDER BY role ASC, username ASC
            """;
        using var reader = command.ExecuteReader();
        var users = new List<UserRecord>();
        while (reader.Read())
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    public static UserRecord? GetUser(string dbPath, string username)
    {
        Initialize(dbPath, username);
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            return null;
        }

        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT username, role, enabled, created_at, updated_at
            FROM users
            WHERE username = $username
            """;
        command.Parameters.AddWithValue("$username", username);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public static UserRecord? AuthenticateUser(string dbPath, string username, string password)
    {
        Initialize(dbPath, username);
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            return null;
        }

        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT username, password_hash, role, enabled, created_at, updated_at
            FROM users
            WHERE username = $username
            """;
        command.Parameters.AddWithValue("$username", username);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(reader.GetOrdinal("enabled")) == 0)
        {
            return null;
        }

        var passwordHash = reader.GetString(reader.GetOrdinal("password_hash"));
        return VerifyPassword(password, passwordHash) ? ReadUser(reader) : null;
    }

    public static UserRecord SetUserEnabled(
        string dbPath,
        string username,
        bool enabled,
        string? protectedUsername = null)
    {
        Initialize(dbPath, protectedUsername ?? username);
        username = NormalizeUsername(username);
        protectedUsername = NormalizeUsername(protectedUsername);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (protectedUsername.Length > 0 && username == protectedUsername && !enabled)
        {
            throw new InvalidOperationException("cannot disable the environment superadmin");
        }

        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
            SET enabled = $enabled,
                updated_at = $updated_at
            WHERE username = $username
            """;
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", UnixTimeSeconds());
        command.Parameters.AddWithValue("$username", username);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException("user not found");
        }

        return GetUser(dbPath, username) ?? throw new InvalidOperationException("failed to update user");
    }

    public static UserRecord ResetUserPassword(string dbPath, string username, string password)
    {
        Initialize(dbPath, username);
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("password is required", nameof(password));
        }

        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
            SET password_hash = $password_hash,
                updated_at = $updated_at
            WHERE username = $username
            """;
        command.Parameters.AddWithValue("$password_hash", HashPassword(password));
        command.Parameters.AddWithValue("$updated_at", UnixTimeSeconds());
        command.Parameters.AddWithValue("$username", username);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException("user not found");
        }

        return GetUser(dbPath, username) ?? throw new InvalidOperationException("failed to reset user password");
    }

    public static UserRecord DeleteUser(string dbPath, string username, string protectedUsername)
    {
        Initialize(dbPath, protectedUsername);
        username = NormalizeUsername(username);
        protectedUsername = NormalizeUsername(protectedUsername);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (protectedUsername.Length == 0)
        {
            throw new ArgumentException("protected_username is required", nameof(protectedUsername));
        }

        if (username == protectedUsername)
        {
            throw new InvalidOperationException("cannot delete current user");
        }

        using var connection = OpenConnection(dbPath);
        using var transaction = connection.BeginTransaction();
        try
        {
            UserRecord? deletedUser;
            using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText = """
                    SELECT username, role, enabled, created_at, updated_at
                    FROM users
                    WHERE username = $username
                    """;
                selectCommand.Parameters.AddWithValue("$username", username);
                using var reader = selectCommand.ExecuteReader();
                deletedUser = reader.Read() ? ReadUser(reader) : null;
            }

            if (deletedUser is null)
            {
                throw new InvalidOperationException("user not found");
            }

            using (var deleteKeysCommand = connection.CreateCommand())
            {
                deleteKeysCommand.Transaction = transaction;
                deleteKeysCommand.CommandText = "DELETE FROM access_api_keys WHERE owner_username = $username";
                deleteKeysCommand.Parameters.AddWithValue("$username", username);
                deleteKeysCommand.ExecuteNonQuery();
            }

            using (var deleteChannelsCommand = connection.CreateCommand())
            {
                deleteChannelsCommand.Transaction = transaction;
                deleteChannelsCommand.CommandText = "DELETE FROM channels WHERE owner_username = $username";
                deleteChannelsCommand.Parameters.AddWithValue("$username", username);
                deleteChannelsCommand.ExecuteNonQuery();
            }

            using (var deleteUserCommand = connection.CreateCommand())
            {
                deleteUserCommand.Transaction = transaction;
                deleteUserCommand.CommandText = "DELETE FROM users WHERE username = $username";
                deleteUserCommand.Parameters.AddWithValue("$username", username);
                deleteUserCommand.ExecuteNonQuery();
            }

            transaction.Commit();
            return deletedUser;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
