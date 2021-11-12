using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Q42.HueApi;
using Q42.HueApi.Interfaces;
using Serilog;

namespace HueGuard
{

    class Program
    {
        static async Task Main(string[] args)
        {
            await Host.CreateDefaultBuilder(args)
            .UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext())
            .ConfigureAppConfiguration((hostingContext, configuration) =>
            {
                configuration.Sources.Clear();
                IHostEnvironment env = hostingContext.HostingEnvironment;
                configuration
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddJsonFile($"appsettings.{env.EnvironmentName}.json", true, true);
            }).ConfigureServices((hostContext, services) =>
            {
                services.AddHostedService<ConsoleHostedService>();
            })
            .RunConsoleAsync();
        }   
    }

    internal sealed class ConsoleHostedService : IHostedService
    {
        private readonly ILogger _logger;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly IConfiguration _configuration;

        public ConsoleHostedService(
            ILogger logger,
            IHostApplicationLifetime appLifetime, IConfiguration configuration)
        {
            _logger = logger;
            _appLifetime = appLifetime;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.Information($"Starting with arguments: {string.Join(" ", Environment.GetCommandLineArgs())}");

            _appLifetime.ApplicationStarted.Register(() =>
            {
                Task.Run(async () =>
                {

                    IBridgeLocator locator = new HttpBridgeLocator(); //Or: LocalNetworkScanBridgeLocator, MdnsBridgeLocator, MUdpBasedBridgeLocator
                    var bridges = await locator.LocateBridgesAsync(TimeSpan.FromSeconds(5));

                    if (!bridges.Any())
                    {
                        Console.WriteLine("No bridge found!");
                        _appLifetime.StopApplication();
                    }

                    var bridge = bridges.First();
                    var bridgeIp = bridge.IpAddress;
                    LocalHueClient client = new LocalHueClient(bridgeIp);

                    var appKey = _configuration.GetValue<string>("AppKey");

                    if (appKey == null || appKey.Length == 0)
                    {
                        var machineName = Environment.MachineName;
                        _logger.Information($"No app key found in config. Registering new client '{machineName}', ensure that link button is pressed on Hue hub.");

                        while (appKey?.Length == 0)
                        {
                            try
                            {
                                appKey = await client.RegisterAsync("HueGuard", machineName);
                            }
                            catch (LinkButtonNotPressedException)
                            {

                            }
                        }
                        _logger.Information($"Got app key '{appKey}'");
                    }
                    else
                    {
                        client.Initialize(appKey);
                    }


                    // Check that all the lights aren't currnetly already on, we have no previous state to return to so error and quit
                    var lights = await client.GetLightsAsync();
                    var currentLightsState = lights.ToDictionary(l => l.Id, l => l.State.On);

                    if (currentLightsState.Values.All(status => status == true))
                    {
                        _logger.Error("ERROR: All lights are currently on, so have no previous state to guard, turn a light off and try again!");
                        _appLifetime.StopApplication();
                    }

                    var previousLightsState = currentLightsState;
                    var previousLightStateCaptureTime = DateTime.MinValue;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        lights = await client.GetLightsAsync();
                        currentLightsState = lights.ToDictionary(l => l.Id, l => l.State.On);

                        if (currentLightsState.Values.All(status => status == true))
                        {
                            // ALL the lights are on, probably a problem
                            _logger.Information("All lights on detected, waiting 5 seconds for hue to settle down");
                            await Task.Delay(5000);
                            _logger.Information("Resetting to previous state");
                            var previousOnLights = previousLightsState.Where(kvp => kvp.Value == true);
                            _logger.Information($"{previousOnLights.Count()} were previously on");
                            var previousOffLights = previousLightsState.Where(kvp => kvp.Value == false);
                            _logger.Information($"{previousOffLights.Count()} were previously off");

                            var command = new LightCommand();
                            command.TurnOff();
                            await client.SendCommandAsync(command, previousOffLights.Select(kvp => kvp.Key));
                            command.TurnOn();
                            await client.SendCommandAsync(command, previousOnLights.Select(kvp => kvp.Key));
                        }
                        else
                        {

                            if (DateTime.UtcNow > previousLightStateCaptureTime.Add(TimeSpan.FromSeconds(5)))
                            {
                                previousLightsState = currentLightsState;
                                previousLightStateCaptureTime = DateTime.UtcNow;
                            }
                        }
                        await Task.Delay(1000);
                    }

                    _appLifetime.StopApplication();
                });
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
