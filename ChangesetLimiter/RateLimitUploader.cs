using System.Text.Json;
using Azure.Storage.Blobs;

internal class RateLimitUploader
{
    static readonly string blobStorageConnectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION");

    internal static void Upload(RateLimitedAccount[] data)
    {
        string json = JsonSerializer.Serialize(data);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var memStream = new MemoryStream(bytes);

        string blobStorageContainerName = "data";
        string fileName = "RateLimit.json";

        BlobContainerClient containerClient = new(blobStorageConnectionString, blobStorageContainerName);
        BlobClient blobClient = containerClient.GetBlobClient(fileName);
        blobClient.Upload(memStream, overwrite: true);
    }
}

public class RateLimitedAccount
{
    public long UserId { get; set; }
    public string Username { get; set; }
    public List<RateLimitedChangeset> Changesets { get; set; }

    public class RateLimitedChangeset
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public List<string> Reasons { get; set; }
    }
}
