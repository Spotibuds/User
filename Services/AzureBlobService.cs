using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace User.Services;

public interface IAzureBlobService
{
    Task<string> UploadUserProfilePictureAsync(string userId, Stream imageStream, string fileName);
    Task<Stream> DownloadFileAsync(string containerName, string blobName);
    Task<bool> DeleteFileAsync(string containerName, string blobName);
    Task<List<string>> ListFilesAsync(string containerName, string prefix = "");
    BlobContainerClient GetBlobContainerClient(string containerName);
    string GenerateSasUrl(string containerName, string blobName, TimeSpan? expiry = null);
}

public class AzureBlobService : IAzureBlobService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _usersContainer;

    public AzureBlobService(IConfiguration configuration)
    {
        var connectionString = configuration["AzureStorage:ConnectionString"];
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new ArgumentException("Azure Storage connection string not found");
        }

        _blobServiceClient = new BlobServiceClient(connectionString);
        _usersContainer = configuration["AzureStorage:UsersContainer"] ?? "users";
    }

    public async Task<string> UploadUserProfilePictureAsync(string userId, Stream imageStream, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_usersContainer);
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

        var imageGuid = Guid.NewGuid().ToString();
        var fileExtension = Path.GetExtension(fileName).ToLower();
        var blobName = $"{userId}/profile_pic/{imageGuid}{fileExtension}";

        var blobClient = containerClient.GetBlobClient(blobName);
        await blobClient.UploadAsync(imageStream, overwrite: true);

        return GenerateSasUrl(_usersContainer, blobName, TimeSpan.FromDays(365));
    }

    public async Task<Stream> DownloadFileAsync(string containerName, string blobName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        var response = await blobClient.DownloadStreamingAsync();
        return response.Value.Content;
    }

    public async Task<bool> DeleteFileAsync(string containerName, string blobName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        var response = await blobClient.DeleteIfExistsAsync();
        return response.Value;
    }

    public async Task<List<string>> ListFilesAsync(string containerName, string prefix = "")
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobs = new List<string>();

        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
        {
            blobs.Add(blobItem.Name);
        }

        return blobs;
    }

    public BlobContainerClient GetBlobContainerClient(string containerName)
    {
        return _blobServiceClient.GetBlobContainerClient(containerName);
    }

    public string GenerateSasUrl(string containerName, string blobName, TimeSpan? expiry = null)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        // Check if we can generate SAS (requires account key)
        if (!blobClient.CanGenerateSasUri)
        {
            // Fallback to direct URL (won't work with private containers)
            return blobClient.Uri.ToString();
        }

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b", // blob resource
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiry ?? TimeSpan.FromHours(1)) // Default 1 hour expiry
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return blobClient.GenerateSasUri(sasBuilder).ToString();
    }
}
