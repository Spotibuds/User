using System.Net;
using System.Text.Json;
namespace User.Services;

public sealed class CanonicalSongReader(IHttpClientFactory clients)
{
    public sealed class Song { public string Id { get; set; } = ""; public string Title { get; set; } = ""; public int DurationSec { get; set; } public List<Artist> Artists { get; set; } = []; public string? CoverUrl { get; set; } }
    public sealed class Artist { public string Name { get; set; } = ""; }
    public async Task<Song> Read(string id, CancellationToken ct)
    {
        Input.ObjectId(id);
        using var response = await clients.CreateClient("Music").GetAsync($"api/songs/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new ApiProblem(400, "Song no longer exists.");
        if (!response.IsSuccessStatusCode) throw new ApiProblem(503, "Catalogue is unavailable.");
        Song? song;
        try { song = await response.Content.ReadFromJsonAsync<Song>(cancellationToken: ct); }
        catch (JsonException) { throw new ApiProblem(503, "The catalogue contract is invalid."); }
        if (song == null || song.Id != id || string.IsNullOrWhiteSpace(song.Title) || song.Title.Length > 200 || song.DurationSec is < 1 or > 86400 || song.Artists == null || song.Artists.Count is < 1 or > 20 || song.Artists.Any(x => x == null || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 200)) throw new ApiProblem(503, "The catalogue contract is invalid.");
        return song;
    }
    public static string ArtistNames(Song song) => string.Join(", ", song.Artists.Select(x => x.Name.Trim()));
}
