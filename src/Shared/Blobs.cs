using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace Shared
{
    public static class Blobs
    {
        public static async Task<T> ReadAppDataBlob<T>(string file, ILogger log)
            where T : new()
        {
            var containerClient = new BlobContainerClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "app-data");
            var blobClient = containerClient.GetBlobClient(file);

            if (!await blobClient.ExistsAsync())
            {
                log.LogInformation($"File {file} doesn't exist");
                return new T();
            }

            log.LogInformation($"Loading file {file}");
            await using var readStream = await blobClient.OpenReadAsync();
            using var reader = new StreamReader(readStream);
            var blobContent = await reader.ReadToEndAsync();
            return JsonConvert.DeserializeObject<T>(blobContent) ?? new T();
        }

        public static async Task WriteAppDataBlob<T>(T saveObject, string file, ILogger log)
        {
            var containerClient = new BlobContainerClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "app-data");
            var blobClient = containerClient.GetBlobClient(file);

            log.LogInformation($"Writing file {file}");
            await using var writeStream = await blobClient.OpenWriteAsync(true);
            var json = JsonConvert.SerializeObject(saveObject, Formatting.Indented);
            var byteArray = Encoding.UTF8.GetBytes(json);
            writeStream.Write(byteArray);
        }
    }
}
