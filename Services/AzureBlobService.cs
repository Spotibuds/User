using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SkiaSharp;
using MongoDB.Driver;
using User.Data;
using User.Entities;

namespace User.Services;
public interface IAzureBlobService
{
    Task<string> UploadUserProfilePictureAsync(string userId, Stream imageStream, string fileName);
    Task<Stream> DownloadAvatar(string userId, string filename);
    Task Cleanup(string url);
    Task Published(string url);
    Task Delete(string url);
    Task Ready(CancellationToken ct);
}
public sealed class AzureBlobService : IAzureBlobService
{
    private readonly BlobContainerClient container;
    private readonly string publicUrl;
    private readonly MongoDbContext db;
    private readonly TimeProvider clock;
    public AzureBlobService(IConfiguration config, MongoDbContext context, TimeProvider time) { db = context; clock = time; var options = new BlobClientOptions(); options.Retry.MaxRetries = 2; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5); container = new BlobServiceClient(config["AzureStorage:ConnectionString"], options).GetBlobContainerClient("avatars"); publicUrl = (config["AzureStorage:PublicBaseUrl"] ?? throw new InvalidOperationException("AzureStorage:PublicBaseUrl required")).TrimEnd('/'); }
    public async Task Ready(CancellationToken ct) => await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
    public async Task<string> UploadUserProfilePictureAsync(string userId, Stream imageStream, string fileName)
    {
        Input.GuidId(userId); using (imageStream) using (var bytes = new MemoryStream())
        {
            var buffer = new byte[81920]; int read;
            while ((read = await imageStream.ReadAsync(buffer)) > 0) { if (bytes.Length + read > 5 * 1024 * 1024) throw new ApiProblem(400, "Avatar exceeds 5 MiB."); await bytes.WriteAsync(buffer.AsMemory(0, read)); }
            bytes.Position = 0;
            try
            {
                using var encoded = SKData.CreateCopy(bytes.ToArray());
                using var codec = SKCodec.Create(encoded) ?? throw new ApiProblem(400, "Invalid image content.");
                if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Gif)) throw new ApiProblem(400, "Use PNG, JPEG, WebP or GIF.");
                if (codec.Info.Width > 4096 || codec.Info.Height > 4096 || (long)codec.Info.Width * codec.Info.Height > 16000000) throw new ApiProblem(400, "Avatar dimensions are too large.");
                using var image = new SKBitmap(codec.Info);
                if (codec.GetPixels(image.Info, image.GetPixels()) != SKCodecResult.Success) throw new ApiProblem(400, "Invalid or incomplete image content.");
                using var png = image.Encode(SKEncodedImageFormat.Png, 90);
                using var normalized = new MemoryStream(png.ToArray());
                var name = $"{Guid.NewGuid():N}.png";
                var url = $"{publicUrl}/api/users/avatar/{userId}/{name}";
                await db.AvatarCleanup.InsertOneAsync(new AvatarCleanupIntent { Url = url, DueAt = clock.GetUtcNow().UtcDateTime.AddHours(1) });
                await container.CreateIfNotExistsAsync(PublicAccessType.None);
                await container.GetBlobClient($"{userId}/{name}").UploadAsync(normalized, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "image/png", CacheControl = "private, max-age=60" } });
                return url;
            }
            catch (ArgumentException) { throw new ApiProblem(400, "Upload a valid PNG, JPEG, GIF or WebP image."); }
        }
    }
    public async Task<Stream> DownloadAvatar(string userId, string filename) { Input.GuidId(userId); if (!System.Text.RegularExpressions.Regex.IsMatch(filename, "^[a-f0-9]{32}\\.png$")) throw new ApiProblem(400, "Invalid avatar path."); return (await container.GetBlobClient($"{userId}/{filename}").DownloadStreamingAsync()).Value.Content; }
    public async Task Cleanup(string url)
    {
        await db.AvatarCleanup.UpdateOneAsync(x => x.Url == url, Builders<AvatarCleanupIntent>.Update.SetOnInsert(x => x.Url, url).Set(x => x.DueAt, clock.GetUtcNow().UtcDateTime), new UpdateOptions { IsUpsert = true });
        try { await Delete(url); await Published(url); } catch (Exception ex) when (DependencyErrors.IsDependency(ex)) { /* Durable intent retains the cleanup; metadata replacement has already succeeded. */ }
    }
    public async Task Published(string url) => await db.AvatarCleanup.DeleteManyAsync(x => x.Url == url);
    public async Task Delete(string url)
    {
        var prefix = publicUrl + "/api/users/avatar/"; if (!url.StartsWith(prefix, StringComparison.Ordinal)) return;
        var path = url[prefix.Length..]; var parts = path.Split('/'); if (parts.Length != 2 || !Guid.TryParse(parts[0], out _) || !System.Text.RegularExpressions.Regex.IsMatch(parts[1], "^[a-f0-9]{32}\\.png$")) throw new ApiProblem(400, "Invalid avatar path.");
        await container.GetBlobClient(path).DeleteIfExistsAsync();
    }
}
