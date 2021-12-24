using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Weather.Device;
using Weather.Store;

namespace Weather;

public class Program
{
    public static void Main(string[] args)
    {
        var env = Environment.GetEnvironmentVariable("Environment");
        if (env == null)
        {
            env = "development";
        }

        // load configuration from appsettings.json
        // override default values with appsettings.{Environment}.json
        // override default values with environment variables
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", false, true)
            .AddJsonFile($"appsettings.{env}.json", true, true)
            .AddEnvironmentVariables()
            .Build();

        // configure logging
        Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File("logs/weather.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();

        // Get values from the config given their key and their target type.
        Settings settings = config.GetRequiredSection("Settings").Get<Settings>();
        Log.Information($"starting application in {env} mode");

        // start data generator that will generate data for the devices and store them in the database
        DataGenerator generator = new DataGenerator(settings);
        generator.Start();

        // start the gateway that reads data from database and send data to IoT Central through IoT devices
        Gateway gateway = new Gateway(settings);
        gateway.Start();

        // wait until the user presses a key
        // TODO: make this better!!
        Console.ReadLine();

        // stop all services
        generator.Stop();
        gateway.Stop();
        Log.Information($"stopped application");
    }
}