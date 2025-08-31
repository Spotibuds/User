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
    Task UpdateContainerAccessLevelAsync(string containerName);
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
        try
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            // Check if we can generate SAS (requires account key)
            if (!blobClient.CanGenerateSasUri)
            {
                Console.WriteLine($"ERROR: Cannot generate SAS URI for {containerName}/{blobName}. The connection string may not include the account key.");
                Console.WriteLine($"Connection string format needed: DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net");
                throw new InvalidOperationException($"Cannot generate SAS URI. This usually means the connection string doesn't include the account key, or managed identity is being used without proper SAS delegation.");
            }

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = blobName,
                Resource = "b", // blob resource
                ExpiresOn = DateTimeOffset.UtcNow.Add(expiry ?? TimeSpan.FromDays(365)) // Long-lived for media files
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sasUrl = blobClient.GenerateSasUri(sasBuilder).ToString();
            Console.WriteLine($"Generated SAS URL for {containerName}/{blobName}");
            return sasUrl;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating SAS URL for {containerName}/{blobName}: {ex.Message}");
            throw; // Re-throw the exception since we can't fallback to direct URLs with private containers
        }
    }

    public async Task UpdateContainerAccessLevelAsync(string containerName)
    {
        try
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            
            // Check if container exists
            var exists = await containerClient.ExistsAsync();
            if (!exists.Value)
            {
                Console.WriteLine($"Container {containerName} does not exist, creating with public blob access");
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);
                return;
            }

            // Update existing container to allow public blob access
            await containerClient.SetAccessPolicyAsync(PublicAccessType.Blob);
            Console.WriteLine($"Updated container {containerName} to allow public blob access");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating container access level for {containerName}: {ex.Message}");
            throw;
        }
    }
}
