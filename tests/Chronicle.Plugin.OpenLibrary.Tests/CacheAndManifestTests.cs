using System.Text.Json;
using Chronicle.Plugin.OpenLibrary;

namespace Chronicle.Plugin.OpenLibrary.Tests;

public class TtlCacheTests
{
    [Fact]
    public void AnEntry_IsRemembered_UntilItExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new TtlCache<string>(TimeSpan.FromMinutes(10), clock: () => now);
        cache.Set("a", "one");

        Assert.True(cache.TryGet("a", out var fresh));
        Assert.Equal("one", fresh);

        now += TimeSpan.FromMinutes(9);
        Assert.True(cache.TryGet("a", out _));

        now += TimeSpan.FromMinutes(2);
        Assert.False(cache.TryGet("a", out var expired));
        Assert.Null(expired);
    }

    [Fact]
    public void AnUnknownKey_IsAMiss() => Assert.False(new TtlCache<int>(TimeSpan.FromMinutes(1)).TryGet("nothing", out _));

    [Fact]
    public void SettingAgain_ReplacesTheValue_AndRestartsItsLife()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new TtlCache<string>(TimeSpan.FromMinutes(10), clock: () => now);
        cache.Set("a", "old");
        now += TimeSpan.FromMinutes(9);

        cache.Set("a", "new");
        now += TimeSpan.FromMinutes(9);

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal("new", value);
    }

    [Fact]
    public void WhenFull_TheCacheStartsOver_SoItNeverGrowsWithoutLimit()
    {
        var cache = new TtlCache<int>(TimeSpan.FromMinutes(10), capacity: 3);
        for (var i = 0; i < 3; i++) cache.Set($"k{i}", i);
        Assert.Equal(3, cache.Count);

        cache.Set("k3", 3);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("k3", out _));
        Assert.False(cache.TryGet("k0", out _));
    }
}

public class ManifestTests
{
    private static JsonElement Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json"))).RootElement.Clone();

    [Fact]
    public void TheManifest_MatchesTheCode()
    {
        var provider = new OpenLibraryMetadataProvider();
        var manifest = Manifest();

        Assert.Equal(provider.PluginId, manifest.GetProperty("plugin_id").GetString());
        Assert.Equal(provider.Version, manifest.GetProperty("version").GetString());
        Assert.Equal(provider.Name, manifest.GetProperty("name").GetString());
        Assert.Equal(typeof(OpenLibraryMetadataProvider).FullName, manifest.GetProperty("entry_type").GetString());
        Assert.Equal(provider.GetSupportedMediaTypes().Select(t => t.MediaTypeName),
            manifest.GetProperty("supported_media_types").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void TheManifest_DeclaresTheTwoStandardTasks()
    {
        var tasks = Manifest().GetProperty("background_tasks").EnumerateArray().ToList();

        Assert.Equal(["fetch-missing-metadata", "resync-all-metadata"], tasks.Select(t => t.GetProperty("task_id").GetString()));
        Assert.True(tasks[0].GetProperty("default_enabled").GetBoolean());
        Assert.False(tasks[1].GetProperty("default_enabled").GetBoolean());   // slow on a large library: opt in
    }

    [Fact]
    public void TheCsprojVersion_AgreesWithTheCode()
    {
        var csproj = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Chronicle.Plugin.OpenLibrary.csproj"));

        Assert.Contains($"<Version>{new OpenLibraryMetadataProvider().Version}</Version>", csproj);
    }

    [Fact]
    public void TheManifest_NeedsNoKey_AndHasAFixMatchHint()
    {
        var manifest = Manifest();

        Assert.Equal(0, manifest.GetProperty("settings").GetArrayLength());   // settings come from the provider's own schema
        Assert.Contains("OL16114008W", manifest.GetProperty("fixMatchHint").GetString());
    }
}
