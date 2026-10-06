namespace OmniCore.Infrastructure;

/// <summary>Presentation preferences; hiding a model never changes its authorization policy.</summary>
public sealed class ModelPickerPreferences : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    public ModelPickerPreferences(string databasePath)
    {
        _connection = new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS model_picker_preferences (provider_id TEXT NOT NULL, model_id TEXT NOT NULL, visible INTEGER NOT NULL, PRIMARY KEY(provider_id, model_id))";
        command.ExecuteNonQuery();
    }
    public bool IsVisible(string provider, string model)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT visible FROM model_picker_preferences WHERE provider_id=$provider AND model_id=$model";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$model", model);
        return command.ExecuteScalar() is not long value || value != 0;
    }
    public void SetVisible(string provider, string model, bool visible, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO model_picker_preferences(provider_id,model_id,visible) VALUES($provider,$model,$visible) ON CONFLICT(provider_id,model_id) DO UPDATE SET visible=excluded.visible";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$visible", visible ? 1 : 0);
        command.ExecuteNonQuery();
    }
    public void Dispose() => _connection.Dispose();
}
