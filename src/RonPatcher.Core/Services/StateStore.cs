using System.Text.Json;
using System.Text.Json.Serialization;

namespace RonPatcher.Core;

internal sealed class StateStore
{
    private readonly string _statePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public StateStore(string dataRoot)
    {
        var fullRoot = Path.GetFullPath(dataRoot);
        _statePath = Path.Combine(fullRoot, "state.json");
    }

    public string StatePath => _statePath;

    public async Task<AppState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
            return new AppState();

        try
        {
            await using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 32 * 1024, options: FileOptions.SequentialScan | FileOptions.Asynchronous);
            var state = await JsonSerializer.DeserializeAsync<AppState>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return state ?? new AppState();
        }
        catch (JsonException ex)
        {
            throw new StateCorruptException($"The state file is not valid JSON: {_statePath}", ex);
        }
        catch (IOException ex)
        {
            throw new StateCorruptException($"The state file could not be read: {_statePath}", ex);
        }
    }

    public async Task SaveAsync(AppState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_statePath)
            ?? throw new PatcherException("Invalid state path.");
        Directory.CreateDirectory(directory);
        var temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 32 * 1024, options: FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _statePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Preserve a successful state write even if temp cleanup fails.
            }
        }
    }
}
