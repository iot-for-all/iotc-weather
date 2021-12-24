using System.Diagnostics;
using Serilog;

using Weather.Data;
using Weather.Store;

namespace Weather.Device;

// Gateway spins up Weather Station devices to connect to IoT Central.
// It reads data from data store and sends it as telemetry messages to IoT Central using these Weather Stations devices.
public class Gateway
{
    /////////////////////////////////////////////////////////////////
    // general purpose variables
    /////////////////////////////////////////////////////////////////

    // application settings
    private Settings settings;
    // data store to interact with the database
    private DataStore dataStore;
    // weather station devices dictionary (name is the station ID, value is the device)
    private Dictionary<string, WeatherDevice> devices;
    // thread to process the data from data store
    private Thread processorThread;
    // cancellation token to cancel the processing thread
    private CancellationTokenSource processorToken;

    // Create a new Gateway
    public Gateway(Settings settings)
    {
        this.settings = settings;
        this.dataStore = new DataStore(settings);
        devices = new Dictionary<string, WeatherDevice>();

        // initialize telemetry thread
        processorToken = new CancellationTokenSource();
        processorThread = new Thread(StartProcessorPump);
        processorThread.IsBackground = true;
        processorThread.Name = "ProcessorThread";
    }

    // Start the gateway
    public void Start()
    {
        // if the gateway is not enabled, do not start it
        if (!settings.Gateway.Enabled)
        {
            Log.Debug("gateway is disabled, not starting");
            return;
        }

        Log.Debug("gateway starting");

        // open the database connection
        dataStore.Open();

        // create devices locally by reading them from database
        // they get provisioned in IoT Central when they connect
        CreateDevices();

        // start processor pump
        CancellationTokenSource token = new CancellationTokenSource();
        processorThread.Start(processorToken.Token);

        Log.Information($"gateway started, processing {devices.Count()} stations");
    }

    // Stop the gateway
    public void Stop()
    {
        // if the gateway is not enabled, nothing to do here
        if (!settings.Gateway.Enabled)
        {
            return;
        }

        // stop the processor pump and cleanup other resources
        processorToken.Cancel();
        processorThread.Join();
        dataStore.Close();
        Log.Information("gateway stopped");
    }

    // Start the processor pump that reads the data from the data store and sends it to IoT Central.
    // Devices are first provisioned and then they are connected to IoT Central.
    private void StartProcessorPump(object? obj)
    {
        CancellationToken token = (obj == null) ? CancellationToken.None : (CancellationToken)obj;
        Log.Information($"starting processor pump");

        try
        {
            // provision the devices in IoT Central
            ProvisionDevices(token);

            // conect all devices to IoT Central
            ConnectDevices(token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "devices failed to connect");
            return;
        }

        // this pump goes on forever until it is cancelled
        while (!token.IsCancellationRequested)
        {
            try
            {
                Log.Information($"looking for new telemetry in database");

                // get the telemetry that needs to be sent from database
                // only Weather data at a given timestamp with all the 6 data records (air humidity, atmos pressure, pavement, precipitation, snow, wind) are sent
                // not all stations may have data to send
                List<WeatherTelemetry> telemetryList = dataStore.GetWeatherTelemetry();

                // if there is telemetry that needs to be sent, send it
                if (telemetryList.Count() > 0)
                {
                    Log.Information($"found {telemetryList.Count} telemetry messages in database to send");

                    // send the telemetry from the devices
                    SendTelemetry(telemetryList, token);
                }
                else
                {
                    Log.Information($"no new data found in database to send to IoT Central");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "error sending telemetry");
            }

            // wait for some time before looking for new telemetry
            var cancelled = token.WaitHandle.WaitOne(1000 * settings.Gateway.RefreshInterval);
            if (cancelled)
            {
                break;
            }
        }
    }

    // send the telemetry from the devices
    private void SendTelemetry(List<WeatherTelemetry> telemetryList, CancellationToken token)
    {
        // ensure that all devices are connected so that we can handle failures
        // its a no-op if a device is already connected, otherwise it will connect the device
        ConnectDevices(token);

        Stopwatch batchSw = Stopwatch.StartNew();
        Stopwatch sw = Stopwatch.StartNew();
        int totalMessagesSent = 0;
        int batchMessagesSent = 0;
        List<Task<TelemetryResult>> tasks = new List<Task<TelemetryResult>>();
        Dictionary<string, Station> lastUploadStation = new Dictionary<string, Station>();
        for (int i = 0; i < telemetryList.Count; i++)
        {
            WeatherTelemetry telemetry = telemetryList[i];

            // check if the station already exists in gateway
            if (!devices.ContainsKey(telemetry.StationID))
            {
                // got a new station, create a new device
                var newDevice = new WeatherDevice(settings, new Station() { StationID = telemetry.StationID, ConnectionString = "" });

                // listen for provisioning changes
                newDevice.ProvisioningChanged += Device_ProvisioningChanged;
                devices[telemetry.StationID] = newDevice;
                Log.Debug($"device {telemetry.StationID} created");
            }

            // send telemetry if the device is connected
            WeatherDevice device = devices[telemetry.StationID];
            tasks.Add(device.SendTelemetry(telemetry, token));

            // honor the messages throttle limits
            // send telemetry from only so many devices at a time
            if (tasks.Count > 0 && (tasks.Count == settings.Gateway.ConcurrentMessageLimit || (i == telemetryList.Count - 1)))
            {
                Log.Debug($"waiting for {tasks.Count} devices to send telemetry in parallel");
                Task.WaitAll(tasks.ToArray(), token);
                batchSw.Stop();
                long elapsedMS = batchSw.ElapsedMilliseconds;

                // if the telemetry went through, update the last upload time for the station
                foreach (var result in tasks)
                {
                    if (result.Result.Success)
                    {
                        lastUploadStation[result.Result.DeviceId] = new Station() { StationID = result.Result.DeviceId, LastUploadTime = result.Result.TmStamp };
                        totalMessagesSent++;
                        batchMessagesSent++;
                    }
                }

                Log.Debug($"sent {batchMessagesSent} telemetry messages in {elapsedMS} ms");
                tasks.Clear();
                batchMessagesSent = 0;
                batchSw.Restart();
            }
        }

        // update the last upload time for all the stations
        if (totalMessagesSent > 0)
        {
            sw.Stop();
            Log.Information($"sent {totalMessagesSent} telemetry messages from {devices.Count} devices in {sw.ElapsedMilliseconds} ms");
            dataStore.UpdateStationsLastUploadTime(lastUploadStation.Values.ToList<Station>());
        }
    }

    // create devices locally by reading them from database
    // devices are not create in IoT Central here, they are created after provisioning
    private void CreateDevices()
    {
        // get the list of stations from the database
        List<Station> stations = dataStore.ListStations();
        foreach (Station station in stations)
        {
            // create a device for each station
            var device = new WeatherDevice(settings, station);

            // listen for provisioning changes
            device.ProvisioningChanged += Device_ProvisioningChanged;
            devices[station.StationID] = device;
        }
    }

    // connect all devices to IoT Central
    private void ConnectDevices(CancellationToken token)
    {
        Log.Debug($"started connecting devices to IoT Central");

        Stopwatch batchSw = Stopwatch.StartNew();
        Stopwatch sw = Stopwatch.StartNew();
        List<Task<bool>> tasks = new List<Task<bool>>();
        int totalConnectCount = 0;
        int batchConnectCount = 0;
        Log.Debug($"gateway connect loop {devices.Count}");
        for (int i = 0; i < devices.Count; i++)
        {
            WeatherDevice device = devices.Values.ElementAt(i);

            // only try to connect a device if it is not already connected
            if (!device.IsConnected)
            {
                Log.Debug($"gateway connecting device {device.Station.StationID}");
                tasks.Add(device.ConnectAsync());
            }

            // honor the connection throttle limits
            if (tasks.Count > 0 && (tasks.Count == settings.Gateway.ConcurrentConnectionLimit || i == (devices.Count - 1)))
            {
                Log.Information($"waiting for {tasks.Count} devices to connect in parallel");

                Task.WaitAll(tasks.ToArray(), token);
                foreach (var result in tasks)
                {
                    if (result.Result)
                    {
                        totalConnectCount++;
                        batchConnectCount++;
                    }
                }

                batchSw.Stop();
                long elapsedMS = batchSw.ElapsedMilliseconds;
                Log.Information($"connected {batchConnectCount} devices in {elapsedMS} ms");
                tasks.Clear();
                batchConnectCount = 0;

                // induce some sleep before we connect next batch of devices
                if (elapsedMS < 1000)
                {
                    var cancelled = token.WaitHandle.WaitOne(250);
                    if (cancelled)
                    {
                        break;
                    }
                }
                batchSw.Restart();
            }
        }

        if (totalConnectCount > 0)
        {
            sw.Stop();
            Log.Information($"connected total {totalConnectCount} devices to IoT Central in {sw.ElapsedMilliseconds} ms");
        }
    }

    // provision all devices in IoT Central
    private void ProvisionDevices(CancellationToken token)
    {
        Log.Debug($"started provisioning devices in IoT Central");

        Stopwatch batchSw = Stopwatch.StartNew();
        Stopwatch sw = Stopwatch.StartNew();
        int numDevicesProvisioned = 0;
        List<Task<ProvisioningResult>> results = new List<Task<ProvisioningResult>>();
        for (int i = 0; i < devices.Count; i++)
        {
            WeatherDevice device = devices.Values.ElementAt(i);

            // proivison the device if there is no cached connection string
            if (device.Station.ConnectionString == "")
            {
                results.Add(device.ProvisionAsync());
            }

            // honor the provisioning throttle limits
            if (results.Count > 0 && (results.Count == settings.Gateway.ConcurrentConnectionLimit || i == (devices.Count - 1)))
            {
                Log.Debug($"waiting for {results.Count} devices to provision in parallel");
                Task.WaitAll(results.ToArray(), token);

                // cache connection strings
                List<Station> csStations = new List<Station>();
                foreach (Task<ProvisioningResult> result in results)
                {
                    // if the proivisoning was successful, cache the connection string
                    // otherwise, wipe out the old connection string so that we can try again later
                    csStations.Add(new Station() { StationID = result.Result.DeviceId, ConnectionString = result.Result.ConnectionString });
                    devices[result.Result.DeviceId].Station.ConnectionString = result.Result.ConnectionString;
                    csStations.Add(devices[result.Result.DeviceId].Station);
                    if (result.Result.Success)
                    {
                        numDevicesProvisioned++;
                    }
                }
                // write the data back to database
                dataStore.UpdateStationsConnectionString(csStations);

                batchSw.Stop();
                long elapsedMS = batchSw.ElapsedMilliseconds;
                Log.Debug($"provisioned {numDevicesProvisioned} devices in {elapsedMS} ms");
                results.Clear();

                // induce some sleep before we provision next batch of devices
                if (elapsedMS < 1000)
                {
                    var cancelled = token.WaitHandle.WaitOne(1000);
                    if (cancelled)
                    {
                        break;
                    }
                }
                batchSw.Restart();
            }
        }

        sw.Stop();
        Log.Information($"provisioned all {numDevicesProvisioned} devices to IoT Central in {sw.ElapsedMilliseconds} ms");
    }

    // handle provisioning changes after the initial provisioning is completed
    private void Device_ProvisioningChanged(object? sender, ProvisioningResult e)
    {
        if (e.Success)
        {
            Log.Debug($"provisioned device {e.DeviceId} in IoT Central");
        }
        else
        {
            Log.Error($"failed to provision device {e.DeviceId} to IoT Central");
        }

        // if the proivisoning was successful, cache the connection string
        // otherwise, wipe out the old connection string so that we can try again later
        devices[e.DeviceId].Station.ConnectionString = e.ConnectionString;

        // write the data back to database using a new connection
        DataStore tempDS = new DataStore(settings);
        tempDS.Open();
        tempDS.UpdateStationsConnectionString(new List<Station>() { devices[e.DeviceId].Station });
        tempDS.Close();
    }
}