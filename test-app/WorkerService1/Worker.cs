using Amazon.S3;
using Amazon.S3.Model;

namespace WorkerService1;

public class Worker(ILogger<Worker> logger, IAmazonS3 s3, IConfiguration configuration) : BackgroundService
{
    private const string S3_KEY = "uploads/test.txt";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Worker {machineId} running at: {time}", Environment.MachineName, DateTimeOffset.Now);
            }

            // try
            // {
            //     var bucketsResponse = await s3.ListBucketsAsync(stoppingToken);
            //     var buckets = bucketsResponse.Buckets.Select(b => b.BucketName);
            //     if (logger.IsEnabled(LogLevel.Information))
            //     {
            //         logger.LogInformation("S3 Buckets available: {buckets}", buckets);
            //     }
            // }
            // catch (Exception e)
            // {
            //     Console.WriteLine(e);
            //     throw;
            // }
            
            var bucketName = configuration["S3:BucketName"];
            var readRequest = new GetObjectRequest
            {
                BucketName = bucketName,
                Key = S3_KEY
            };

            try
            {
                using var response = await s3.GetObjectAsync(readRequest, stoppingToken);
                using var streamReader = new StreamReader(response.ResponseStream);
                var content = await streamReader.ReadToEndAsync(stoppingToken);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Content of {s3key} in {bucket} is: {response}", S3_KEY, bucketName, content);
                }
            }
            catch (Exception e)
            {
                logger.LogWarning("S3 read request failed: {e}", e);
            }
            var request = new PutObjectRequest()
            {
                BucketName = bucketName,
                Key = S3_KEY,
                ContentBody = $"Hello World! I am: {Environment.MachineName}",
                ContentType = "text/plain",
                UseChunkEncoding = false,
                DisableDefaultChecksumValidation = true
            };
            await s3.PutObjectAsync(request, stoppingToken);

            await File.AppendAllTextAsync($"/data/payload-test-{Environment.MachineName}.txt", $"Worker writing at: {DateTimeOffset.Now}\n", stoppingToken);
            
            await Task.Delay(1000, stoppingToken);
        }
    }
}