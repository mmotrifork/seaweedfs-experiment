namespace WorkerService1;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Worker {machineId} running at: {time}", Environment.MachineName, DateTimeOffset.Now);
            }

            await File.AppendAllTextAsync("/data/payload-test.txt", $"Worker {Environment.MachineName} writing at: {DateTimeOffset.Now}\n", stoppingToken);
            
            await Task.Delay(1000, stoppingToken);
        }
    }
}