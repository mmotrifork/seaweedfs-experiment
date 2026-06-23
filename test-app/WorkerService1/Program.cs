using Amazon.S3;
using WorkerService1;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole();
builder.Services.AddHostedService<Worker>();
builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
builder.Services.AddAWSService<IAmazonS3>();

// Amazon.AWSConfigs.LoggingConfig.LogTo = Amazon.LoggingOptions.Console;
// Amazon.AWSConfigs.LoggingConfig.LogResponses = Amazon.ResponseLoggingOption.Always;
// Amazon.AWSConfigs.LoggingConfig.LogMetrics = true;
// Amazon.AWSConfigs.AddTraceListener("Amazon", new System.Diagnostics.ConsoleTraceListener());

var host = builder.Build();
host.Run();