using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.ServiceRuntime;
using Newtonsoft.Json.Linq;
using NLog;

namespace Sensorlab.Receivers
{
    public class UdpReceiver : RoleEntryPoint
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly ManualResetEvent _runCompleteEvent = new ManualResetEvent(false);
        private UdpClient _udpListener;

        public override void Run()
        {
            Logger.Info("UDP Receiver is running");

            try
            {
                // UDP Client
                _udpListener = new UdpClient(RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["udp10100"].IPEndpoint);

                // Identify direct communication port
                var publicEp = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["udp10100"].PublicIPEndpoint;
                Logger.Info("Listening on public IP:{0}, Port:{1}", publicEp?.Address, publicEp?.Port);

                // Identify public endpoint
                var internalEp = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["udp10100"].IPEndpoint;
                Logger.Info("Listening on private IP:{0}, Port:{1}", internalEp.Address, internalEp.Port);
            }
            catch (Exception x)
            {
                Logger.Error(x, "Error setting up UDP listener");
            }

            try
            {
                _udpListener.BeginReceive(OnReceive, null);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Error receiving UDP packet");
            }

            try
            {
                RunAsync(_cancellationTokenSource.Token).Wait();
            }
            finally
            {
                _runCompleteEvent.Set();
            }
        }

        private void OnReceive(IAsyncResult res)
        {
            byte[] received;
            try
            {
                var remoteIpEndPoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["udp10100"].IPEndpoint;
                received = _udpListener.EndReceive(res, ref remoteIpEndPoint);
                Logger.Trace($"UDP message received: {Encoding.UTF8.GetString(received)}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error receiving payload");
                _udpListener.BeginReceive(OnReceive, null);
                return;
            }

            _udpListener.BeginReceive(OnReceive, null);

            try
            {
                var jtoken = JToken.Parse(Encoding.UTF8.GetString(received));
                switch (jtoken.Type)
                {
                    case JTokenType.Array:
                        foreach (var jobj in (JArray) jtoken)
                            HandleJsonPayload((JObject) jobj);
                        break;
                    case JTokenType.Object:
                        HandleJsonPayload((JObject) jtoken);
                        break;
                    default:
                        Logger.Warn($"Unknown payload type: {jtoken.Type}");
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "Error handling JSON payload");
            }
        }

        private void HandleJsonPayload(JObject jo)
        {
            // Check if event is encrypted
            if (jo.Properties().Any(p => "aes".Equals(p.Name.ToLowerInvariant())))
            {
                // TODO: Decrypt JSON event
            }
            else
            {
                HandleJsonEvent(jo);
            }
        }

        private void HandleJsonEvent(JObject jo)
        {
            var connectionString = Environment.GetEnvironmentVariable("SQLAZURECONNSTR_sensorlabdb");
            if (connectionString == null)
                throw new Exception("Cannot find connection string in environment");

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                Logger.Info($"Opened SQL connection to {connection.DataSource}\\{connection.Database}");

                var uniqueId = jo.Property("UID")?.Value?.Value<string>();
                var imei = jo.Property("IMEI")?.Value?.Value<string>();
                if (uniqueId == null && imei == null) throw new Exception("No sensor unique id or IMEI in payload");

                if (imei != null)
                    imei = Regex.Replace(imei, "[^0-9]", String.Empty);

                var cmd = new SqlCommand(@"
                            SELECT id 
                            FROM sensor 
                            WHERE (uniqueid IS NOT NULL AND uniqueid = @uniqueid) or (imei IS NOT NULL AND imei = @imei)", connection);
                cmd.Parameters.AddWithValue("uniqueid", (object) uniqueId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("imei", (object) imei ?? DBNull.Value);
                var sensorId = cmd.ExecuteScalar();
                if (sensorId == null || sensorId == DBNull.Value)
                    throw new Exception($"Could not find sensor with unique id {uniqueId} or IMEI {imei}");

                var received = DateTime.Now;
                var created = jo.Property("created")?.Value?.Value<DateTime>();
                var measurementGroup = Guid.NewGuid().ToString();

                foreach (var property in jo.Properties())
                    try
                    {
                        cmd = new SqlCommand(@"
                        INSERT INTO sensormeasurement (
                            [sensor_id]
                           ,[type]
                           ,[measurementnumber]
                           ,[measurementdecimal]
                           ,[measurementtext]
                           ,[received]
                           ,[created]
                           ,[measurementgroup]
                        )
                        VALUES (
                            @sensor_id,
                            @type,
                            @measurementnumber,
                            @measurementdecimal,
                            @measurementtext,
                            @received,
                            @created,
                            @measurementgroup
                        )
                    ", connection);

                        var typeCode = property.Name.ToUpperInvariant();
                        if (!SensorMeasurementType.SensorMeasurementTypes.Any(p => p.Code.Equals(typeCode)))
                            continue;

                        var type = SensorMeasurementType.SensorMeasurementTypes.Single(p => p.Code.Equals(typeCode));

                        cmd.Parameters.Add(new SqlParameter("sensor_id", (object) sensorId ?? DBNull.Value));
                        cmd.Parameters.Add(new SqlParameter("imei", (object) imei ?? DBNull.Value));
                        cmd.Parameters.Add(new SqlParameter("type", typeCode));
                        cmd.Parameters.Add(new SqlParameter("measurementnumber",
                            type.DataType == SensorMeasurementType.SensorMeasurementDataType.Integer
                                ? (object) property.Value.Value<long>()
                                : DBNull.Value));
                        cmd.Parameters.Add(new SqlParameter("measurementdecimal",
                            type.DataType == SensorMeasurementType.SensorMeasurementDataType.Float
                                ? (object) property.Value.Value<double>()
                                : DBNull.Value));
                        cmd.Parameters.Add(new SqlParameter("measurementtext",
                            type.DataType == SensorMeasurementType.SensorMeasurementDataType.String
                                ? (object) property.Value.Value<string>()
                                : DBNull.Value));
                        cmd.Parameters.Add(new SqlParameter("received", received.ToUniversalTime()));
                        cmd.Parameters.Add(new SqlParameter("created", (object) created?.ToUniversalTime() ?? DBNull.Value));
                        cmd.Parameters.Add(new SqlParameter("measurementgroup", measurementGroup));

                        cmd.ExecuteNonQuery();

                        Logger.Trace($"Persisted measurement: {type.Name} - {property.Value.Value<string>()} {type.Unit}");
                    }
                    catch (SqlException e)
                    {
                        Logger.Error(e, "Database error saving measurement");
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Error saving measurement");
                    }
            }
        }

        public override bool OnStart()
        {
            // Set the maximum number of concurrent connections
            // ServicePointManager.DefaultConnectionLimit = 12;

            // For information on handling configuration changes
            // see the MSDN topic at https://go.microsoft.com/fwlink/?LinkId=166357.

            var result = base.OnStart();

            Logger.Info("UdpReceiver has been started");

            return result;
        }

        public override void OnStop()
        {
            Logger.Info("UdpReceiver is stopping");

            _cancellationTokenSource.Cancel();
            _runCompleteEvent.WaitOne();

            base.OnStop();

            Trace.TraceInformation("UdpReceiver has stopped");
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            // TODO: Replace the following with your own logic.
            while (!cancellationToken.IsCancellationRequested) await Task.Delay(1000, cancellationToken);
        }

        public class SensorMeasurementType
        {
            public enum SensorMeasurementDataType
            {
                Date,
                String,
                Float,
                Integer,
                Image
            }

            public static readonly List<SensorMeasurementType> SensorMeasurementTypes =
                new List<SensorMeasurementType>
                {
                    new SensorMeasurementType("TMP", "Temperature", "K", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("HUM", "Absolute humidity", "g/m3", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("LAT", "Latitude", "rad", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("LNG", "Longitude", "rad", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("ACX", "Acceleration X", "m/s2", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("ACY", "Acceleration Y", "m/s2", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("ACZ", "Acceleration Z", "m/s2", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("APS", "Air pressure", "kPa", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("BAT", "Battery", "V", SensorMeasurementDataType.Float),
                    new SensorMeasurementType("LUX", "Illuminance", "lx", SensorMeasurementDataType.Float)
                };

            private SensorMeasurementType(string code, string name, string unit, SensorMeasurementDataType dataType)
            {
                Code = code;
                Name = name;
                Unit = unit;
                DataType = dataType;
            }

            private SensorMeasurementType(string code, string name, string unit, SensorMeasurementDataType dataType,
                Func<bool> validatorFunc)
            {
                Code = code;
                Name = name;
                Unit = unit;
                DataType = dataType;
                Validator = validatorFunc;
            }

            public string Code { get; }
            public string Name { get; }
            public string Unit { get; }
            public SensorMeasurementDataType DataType { get; }
            public Func<bool> Validator { get; }
        }
    }
}