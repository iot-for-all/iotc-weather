using System.Diagnostics;
using Serilog;

using Weather.Data;

namespace Weather.Store;

// Data Generator generates weather station data and stores it in a database.
public class DataGenerator
{
    /////////////////////////////////////////////////////////////////
    // general purpose variables
    /////////////////////////////////////////////////////////////////

    // application settings
    private Settings settings;
    // data store to interact with the database
    private DataStore dataStore;
    // weather stations to generate data for
    private List<Station> stations;
    // generator thread
    private Thread genThread;
    // cancellation token for the generator thread
    private CancellationTokenSource genToken;
    // random number generator
    private Random rand;

    // create a new data generator
    public DataGenerator(Settings settings)
    {
        this.settings = settings;
        this.dataStore = new DataStore(settings);
        stations = new List<Station>();
        rand = new Random();

        // initialize data generation thread
        genToken = new CancellationTokenSource();
        genThread = new Thread(StartDataGenerationPump);
        genThread.IsBackground = true;
        genThread.Name = "DataGeneratorThread";
    }

    // start the data generator thread
    public void Start()
    {
        // if the data generator is not enabled, do not start it
        if (!settings.DataGenerator.Enabled)
        {
            Log.Debug("dataGenerator is disabled, not starting");
            return;
        }

        Log.Debug("data generator starting");

        // open the database connection
        dataStore.Open();

        // create weather stations
        CreateWeatherStations();

        // start data generator thread
        genThread.Start(genToken.Token);

        Log.Information("data generator started");
    }

    // Stop the data generator thread
    public void Stop()
    {
        // if the data generator is not enabled, nothing to do here
        if (!settings.DataGenerator.Enabled)
        {
            return;
        }

        Log.Debug("data generator stopping");

        // stop the data generator pump and cleanup other resources
        genToken.Cancel();
        genThread.Join();
        dataStore.Close();
        Log.Information("data generator stopped");
    }

    // Start the data generator pump that generates the weather data for all stations and stores it in the database.
    private void StartDataGenerationPump(object? obj)
    {
        CancellationToken token = (obj == null) ? CancellationToken.None : (CancellationToken)obj;
        while (!token.IsCancellationRequested)
        {
            if (settings.DataGenerator.Enabled)
            {
                try
                {
                    // generate data
                    GenerateData();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "error generating data");
                }
            }

            // induce some sleep before we generate more data
            var cancelled = token.WaitHandle.WaitOne(1000 * settings.DataGenerator.GenerationInterval);
            if (cancelled)
            {
                break;
            }
        }
    }

    // Generate data for all weather stations with the same timestamp.
    private void GenerateData()
    {
        List<AirHumidity> airHumidityList = new List<AirHumidity>();
        List<AtmosPressure> atmosPressureList = new List<AtmosPressure>();
        List<Pavement> pavementList = new List<Pavement>();
        List<Precipitation> precipitationList = new List<Precipitation>();
        List<Snow> snowList = new List<Snow>();
        List<Wind> windList = new List<Wind>();
        DateTime now = DateTime.Now;
        Log.Information("generating data");
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < stations.Count; i++)
        {
            // Air_Humidity
            airHumidityList.Add(new AirHumidity()
            {
                TmStamp = now,
                RecNum = 0,
                StationID = stations[i].StationID,
                Identifier = 131,
                MaxAirTemp1 = GetRandomFloat(10.0f, 25.0f),
                CurAirTemp1 = GetRandomFloat(5.0f, 25.0f),
                MinAirTemp1 = GetRandomFloat(5.0f, 25.0f),
                AirTempQ = 300f,
                AirTemp2 = -6999f,
                AirTemp2Q = -100f,
                RH = GetRandomFloat(50.0f, 100.0f),
                Dew_Point = GetRandomFloat(5.0f, 15.0f),
            });

            // Atmos_Pressure
            atmosPressureList.Add(new AtmosPressure()
            {
                TmStamp = now,
                RecNum = 0,
                StationID = stations[i].StationID,
                Identifier = 131,
                AtmPressure = GetRandomFloat(900.0f, 915.0f),
            });

            // Pavement
            float pvmntTemp1 = GetRandomFloat(6.0f, 15.0f);
            pavementList.Add(new Pavement()
            {
                TmStamp = now,
                RecNum = 0,
                StationID = stations[i].StationID,
                Identifier = 137,
                PvmntTemp1 = pvmntTemp1,
                PavementQ1 = 500,
                AltPaveTemp1 = pvmntTemp1,
                FrzPntTemp1 = -6999,
                FrzPntTemp1Q = -6999,
                PvmntCond = GetRandomFloat(1.0f, 5.0f),
                PvmntCond1Q = 500,
                SbAsphltTemp = GetRandomFloat(10.0f, 15.0f),
                PvBaseTemp1 = -6999,
                PvBaseTemp1Q = -6999,
                PvmntSrfCvTh = -6999,
                PvmntSrfCvThQ = -6999,
            });

            // Precipitation
            precipitationList.Add(new Precipitation()
            {
                TmStamp = now,
                RecNum = 0,
                StationID = stations[i].StationID,
                Identifier = 132,
                GaugeTot = GetRandomFloat(400.0f, 450.0f),
                NewPrecip = GetRandomFloat(0.0f, 3.0f),
                HrlyPrecip = GetRandomFloat(0.0f, 3.0f),
                PrecipGaugeQ = 500,
                PrecipDetRatio = 0,
                PrecipDetQ = 500
            });

            // Snow
            snowList.Add(new Snow()
            {
                TmStamp = now,
                RecNum = 0,
                StationID = stations[i].StationID,
                Identifier = 132,
                HS = -6999,
                HStd = 0,
                HrlySnow = 0,
                SnowQ = 500,
            });

            // Wind
            windList.Add(new Wind()
            {
                TmStamp = now,
                RecNum = 0,
                StationID = stations[i].StationID,
                Identifier = 134,
                MaxWindSpd = GetRandomFloat(1.0f, 25.0f),
                MeanWindSpd = GetRandomFloat(1.0f, 25.0f),
                WindSpd = GetRandomFloat(1.0f, 25.0f),
                WindSpdQ = 500,
                MeanWindDir = GetRandomFloat(0.0f, 360.0f),
                StDevWind = GetRandomFloat(0.0f, 100.0f),
                WindDir = GetRandomFloat(0.0f, 360.0f),
                DerimeStat = -6999,
            });
        }

        dataStore.AddAirHumidity(airHumidityList);
        dataStore.AddAtmosPressure(atmosPressureList);
        dataStore.AddPavement(pavementList);
        dataStore.AddPrecipitation(precipitationList);
        dataStore.AddSnow(snowList);
        dataStore.AddWind(windList);
        // TODO: try to do a bulk upload to MySQL instead of individual inserts
        //dataStore.BulkAddWind(windList);

        sw.Stop();
        Log.Information($"generated {stations.Count} stations data in {sw.ElapsedMilliseconds} ms");
    }

    // create weather stations in the database
    private void CreateWeatherStations()
    {
        // get existing number of weather stations
        stations = dataStore.ListStations();
        int existingCount = stations.Count;
        if (existingCount > settings.DataGenerator.StationCount)
        {
            stations.RemoveRange(settings.DataGenerator.StationCount, existingCount - settings.DataGenerator.StationCount);
        }
        else
        {
            // add new weather stations only if needed
            for (int i = existingCount + 1; i <= settings.DataGenerator.StationCount; i++)
            {
                var station = new Station()
                {
                    StationID = "Station-" + i,
                    StationName = "Weather Station " + i,
                    LastUploadTime = DateTime.MinValue,
                };
                dataStore.AddStation(station);
                stations.Add(station);
            }
        }
    }

    // get random float between min and max
    private float GetRandomFloat(float min, float max)
    {
        return (float)(rand.NextDouble() * (max - min) + min);
    }
}