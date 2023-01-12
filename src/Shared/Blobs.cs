using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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
            try
            {
                var blobContent = await ReadAppDataBlobRaw(file, log);
                return JsonConvert.DeserializeObject<T>(blobContent) ?? new T();
            }
            catch (FileNotFoundException e)
            {
                log.LogInformation("File {file} doesn't exist", file);
                await WriteAppDataBlob(new T(), file, log);
                return new T();
            }
            catch (Exception e)
            {
                log.LogError(e, "Error loading {file}", file);
                return new T();
            }
        }

        public static async Task<string> ReadAppDataBlobRaw(string file, ILogger log)
        {
            var blobClient = await GetClient(file);

            if (!await blobClient.ExistsAsync())
            {
                log.LogInformation("File {file} doesn't exist", file);
                throw new FileNotFoundException(file);
            }

            log.LogInformation("Loading file {file}", file);
            try
            {
                await using var readStream = await blobClient.OpenReadAsync();
                using var reader = new StreamReader(readStream);
                var blobContent = await reader.ReadToEndAsync();
                return blobContent;
            }
            catch (Exception e)
            {
                log.LogError(e, "Error loading {file}", file);
                throw;
            }
        }

        public static async Task WriteAppDataBlob<T>(T saveObject, string file, ILogger log)
        {
            var blobClient = await GetClient(file);

            log.LogInformation("Writing file {file}", file);
            await using var writeStream = await blobClient.OpenWriteAsync(true);
            var json = JsonConvert.SerializeObject(saveObject, Formatting.Indented);
            var byteArray = Encoding.UTF8.GetBytes(json);
            writeStream.Write(byteArray);
        }

        private static async Task<BlobClient> GetClient(string file)
        {
            var containerClient = new BlobContainerClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "app-data");
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.BlobContainer);

            var blobClient = containerClient.GetBlobClient(file);

            return blobClient;
        }
    }
}
