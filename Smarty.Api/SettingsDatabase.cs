using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace Smarty.Api;

public class SettingsDatabase
{
    private readonly string _connectionString;

    public SettingsDatabase(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
        ";
        command.ExecuteNonQuery();

        // Seed defaults if empty
        command.CommandText = "SELECT COUNT(*) FROM settings";
        var count = (long)(command.ExecuteScalar() ?? 0L);
        if (count == 0)
        {
            SeedDefaults();
        }
    }

    private void SeedDefaults()
    {
        var defaults = new Dictionary<string, string>
        {
            { "ollama.baseUrl", "http://localhost:11434" },
            { "ollama.model", "qwen3.5:latest" },
            { "together.apiKey", "" },
            { "together.baseUrl", "https://api.together.xyz/v1" },
            { "model.provider", "ollama" },
            { "model.modelName", "qwen3.5:latest" },
            { "model.baseUrl", "" },
            { "secondaryModel.provider", "" },
            { "secondaryModel.modelName", "" },
            { "secondaryModel.baseUrl", "" },
            { "smarty.trace", "0" }
        };

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        foreach (var kvp in defaults)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO settings (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = $value;
            ";
            command.Parameters.AddWithValue("$key", kvp.Key);
            command.Parameters.AddWithValue("$value", kvp.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string? GetSetting(string key, string? defaultValue = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return reader.GetString(0);
        }

        return defaultValue;
    }

    public void SaveSetting(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO settings (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
        ";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value ?? "");

        command.ExecuteNonQuery();
    }

    public void SaveSettings(Dictionary<string, string> newSettings)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        foreach (var kvp in newSettings)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO settings (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = $value;
            ";
            command.Parameters.AddWithValue("$key", kvp.Key);
            command.Parameters.AddWithValue("$value", kvp.Value ?? "");
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public Dictionary<string, string> GetAllSettings()
    {
        var settings = new Dictionary<string, string>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }
}
