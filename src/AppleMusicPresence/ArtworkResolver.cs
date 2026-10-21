using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppleMusicPresence;

public sealed record Artwork(string Url, string StoreUrl);

public interface IArtworkResolver : IDisposable
{
    Task<Artwork?> ResolveAsync(Track track, string country, CancellationToken ct);
}

public sealed class ArtworkResolver : IArtworkResolver
{
    private readonly HttpClient http;
    private readonly Dictionary<string, (DateTimeOffset Expires, Artwork? Art)> cache = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;

    public ArtworkResolver(HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(7);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AppleMusicPresence/1.0");
    }

    public async Task<Artwork?> ResolveAsync(Track track, string country, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await ResolveCoreAsync(track, country, ct); }
        finally { gate.Release(); }
    }

    private async Task<Artwork?> ResolveCoreAsync(Track track, string country, CancellationToken ct)
    {
        var key = country + "\0" + track.Key;
        if (cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Art;
        Artwork? art = null;
        bool failed = false;
        try
        {
            // At most two requests for an uncached track. Only song metadata is sent to Apple.
            foreach (var store in new[] { country, "US" }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var wait = nextRequest - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                nextRequest = DateTimeOffset.UtcNow.AddSeconds(4);
                var term = Uri.EscapeDataString($"{track.Artist} {track.Title}");
                var uri = $"https://itunes.apple.com/search?term={term}&country={Uri.EscapeDataString(store)}&media=music&entity=song&limit=25";
                using var response = await http.GetAsync(uri, ct);
                if ((int)response.StatusCode == 429)
                {
                    nextRequest = DateTimeOffset.UtcNow.AddMinutes(1);
                    failed = true;
                    break;
                }
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                art = Match(track, json.RootElement);
                if (art is not null) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
        {
            // Do not log request URLs: they contain song names.
            failed = true;
        }
        if (cache.Count >= 128) cache.Remove(cache.Keys.First());
        cache[key] = (DateTimeOffset.UtcNow.Add(art is not null ? TimeSpan.FromHours(12) : failed ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5)), art);
        return art;
    }

    public static string Normalize(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormD))
            if (char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    private static string WithoutFeatures(string value) => Regex.Replace(value, @"\s*[\(\[]\s*(feat\.?|ft\.?|featuring)\s+[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase).Trim();

    public static Artwork? Match(Track track, JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        var title = Normalize(WithoutFeatures(track.Title));
        var artist = Normalize(track.Artist);
        var albumArtist = Normalize(track.AlbumArtist);
        if (title.Length == 0 || artist.Length == 0) return null;
        var choices = new List<(int Score, Artwork Art)>();
        foreach (var item in results.EnumerateArray())
        {
            var candidateTitle = GetString(item, "trackName");
            var candidateArtist = Normalize(GetString(item, "artistName"));
            if (Normalize(WithoutFeatures(candidateTitle)) != title) continue;
            if (candidateArtist != artist && (albumArtist.Length == 0 || candidateArtist != albumArtist)) continue;
            var image = GetString(item, "artworkUrl100");
            if (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase)) continue;
            image = Regex.Replace(image, @"/\d+x\d+bb", "/512x512bb");
            var score = candidateArtist == artist ? 4 : 2;
            if (Normalize(GetString(item, "collectionName")) == Normalize(track.Album)) score += 8;
            if (Normalize(candidateTitle) == Normalize(track.Title)) score += 2;
            choices.Add((score, new(image, GetString(item, "trackViewUrl"))));
        }
        return choices.OrderByDescending(x => x.Score).FirstOrDefault().Art;
    }

    private static string GetString(JsonElement item, string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public void Dispose() => http.Dispose();
}
