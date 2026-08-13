using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameLock.Core;

public sealed class FrameLockPreferences
{
    public int Version { get; set; } = 1;

    public Resolution LastResolution { get; set; } = new(1920, 1080);

    public Dictionary<string, Resolution> PerApplication { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Resolution ResolutionFor(string preferenceKey) =>
        PerApplication.TryGetValue(preferenceKey, out var resolution) && resolution.IsValid
            ? resolution
            : LastResolution.IsValid
                ? LastResolution
                : new Resolution(1920, 1080);

    public void Remember(string preferenceKey, Resolution resolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferenceKey);
        if (!resolution.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        LastResolution = resolution;
        PerApplication[preferenceKey] = resolution;
    }
}

public interface IPreferencesStore
{
    FrameLockPreferences Load();

    void Save(FrameLockPreferences preferences);
}

public sealed class JsonPreferencesStore(string path) : IPreferencesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public FrameLockPreferences Load()
    {
        lock (_sync)
        {
            if (!File.Exists(Path))
            {
                return new FrameLockPreferences();
            }

            try
            {
                var json = File.ReadAllText(Path);
                var preferences = JsonSerializer.Deserialize<FrameLockPreferences>(json, SerializerOptions);
                return Sanitize(preferences);
            }
            catch (IOException)
            {
                return new FrameLockPreferences();
            }
            catch (UnauthorizedAccessException)
            {
                return new FrameLockPreferences();
            }
            catch (JsonException)
            {
                return new FrameLockPreferences();
            }
        }
    }

    public void Save(FrameLockPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_sync)
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = Path + ".tmp";
            var json = JsonSerializer.Serialize(Sanitize(preferences), SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, Path, overwrite: true);
        }
    }

    private static FrameLockPreferences Sanitize(FrameLockPreferences? preferences)
    {
        if (preferences is null)
        {
            return new FrameLockPreferences();
        }

        if (!preferences.LastResolution.IsValid)
        {
            preferences.LastResolution = new Resolution(1920, 1080);
        }

        preferences.PerApplication = new Dictionary<string, Resolution>(
            (preferences.PerApplication ?? [])
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value.IsValid),
            StringComparer.OrdinalIgnoreCase);
        preferences.Version = 1;
        return preferences;
    }
}
