using Aurora.Devices.UnifiedHID;
using Aurora.Utils;
using HidLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Aurora.Devices.QMK
{
    class MassdropAlt : UnifiedBase
    {
        enum FeatureReportIds : byte
        {
            Attributes = 0x01,
            AttributesRequest,
            AttributesResponse,
            MultiUpdate,
            RangeUpdate,
            Control
        }

        private static HidDevice ctrl_device;
        private static HidDevice ctrl_device_leds;
        private CancellationTokenSource tokenSource = new CancellationTokenSource();
        private readonly int frameRateMillis;
        private readonly Stopwatch stopwatch = new Stopwatch();
        public long LastUpdateMillis { get; private set; }
        public bool Active { get; private set; }
        private Windows.Devices.Lights.LampArray lampArray;



        private readonly ConcurrentQueue<Dictionary<DeviceKeys, Color>> colorQueue = new ConcurrentQueue<Dictionary<DeviceKeys, Color>>();
        private const int DiscountLimit = 2500;
        private const int DiscountTries = 3;
        private int disconnectCounter = 0;
        private int reportMaxSize = 64;


        public MassdropAlt()
        {
            PrettyName = "Massdrop ALT";
            IsKeyboard = true;
            frameRateMillis = (int)((1f / 30) * 1000f);
        }


        public override bool Connect()
        {
            if (!Global.Configuration.VarRegistry.GetVariable<bool>($"UnifiedHID_{this.GetType().Name}_enable"))
            {
                return false;
            }


            Task.Run(async () =>
            {
                var devices =
                    await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Lights.LampArray.GetDeviceSelector());
                if (devices.FirstOrDefault() != null)
                    lampArray = await Windows.Devices.Lights.LampArray.FromIdAsync(devices.FirstOrDefault().Id);
            }).Wait();

            var lShift = lampArray.GetIndicesForKey(Windows.System.VirtualKey.LeftShift);
            var shift = lampArray.GetIndicesForKey(Windows.System.VirtualKey.Shift);

            Console.WriteLine($"LShift: {lShift.FirstOrDefault()}");
            Console.WriteLine($"Shift: {shift.FirstOrDefault()}");


            return IsConnected = (lampArray != null);

            IEnumerable<HidDevice> devices = HidDevices.Enumerate(0x04D8, new int[] { 0xEED3 });
            try
            {
                if (devices.Count() > 0)
                {
                    ctrl_device_leds = devices.First(dev => dev.Capabilities.UsagePage == 0x0059 && dev.Capabilities.Usage == 0x0001);
                    /*ctrl_device = devices.First(dev => dev.Capabilities.FeatureReportByteLength > 50);
                    ctrl_device.OpenDevice();*/
                    ctrl_device_leds.OpenDevice();
                    reportMaxSize = ctrl_device_leds.Capabilities.FeatureReportByteLength - 1;
                    bool success = true;
                    /*                    if (!success)
                                        {
                                            Global.logger.LogLine($"Roccat Tyon Could not connect\n", Logging_Level.Error);
                                            ctrl_device.CloseDevice();
                                            ctrl_device_leds.CloseDevice();
                                        }*/


                    ctrl_device_leds.WriteFeatureData(new byte[] { (byte)FeatureReportIds.Control, 0x00 });

                    Global.logger.LogLine($"Massdrop ALT Connected\n", Logging_Level.Info);

                    if (!tokenSource.IsCancellationRequested)
                        tokenSource.Cancel();

                    tokenSource = new CancellationTokenSource();
                    var parallelOptions = new ParallelOptions();
                    parallelOptions.CancellationToken = tokenSource.Token;
                    parallelOptions.MaxDegreeOfParallelism = 1;
                    Parallel.Invoke(parallelOptions, () => { System.Threading.Thread.Sleep(200); Thread(tokenSource.Token); });
                    IsConnected = success;
                    return (IsConnected);
                }
            }
            catch (Exception exc)
            {
                Global.logger.LogLine($"Error when attempting to open UnifiedHID device:\n{exc}", Logging_Level.Error);
            }
            return false;
        }

        public override bool Disconnect()
        {
            try
            {
                lampArray = null;
                /*
                tokenSource.Cancel();
                ctrl_device_leds.WriteFeatureData(new byte[] { (byte)FeatureReportIds.Control, 0x01 });
                ctrl_device_leds.CloseDevice();
                IsConnected = false; */
                
                return true;
            } 
            catch (Exception exc)
            {
                Global.logger.LogLine($"Error when attempting to close UnifiedHID device:\n{exc}", Logging_Level.Error);

            }
            return false;

        }

        private async void Thread(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(frameRateMillis, token);
                    if (colorQueue.IsEmpty) continue;

                    stopwatch.Restart();
                    await ProcessQueue();
                    LastUpdateMillis = stopwatch.ElapsedMilliseconds;
                    stopwatch.Stop();

                    // If the device did not take long to update, continue
                    if (stopwatch.ElapsedMilliseconds < DiscountLimit)
                    {
                        disconnectCounter = 0;
                        continue;
                    }

                    Global.logger.LogLine($"Device {PrettyName} took too long to update {stopwatch.ElapsedMilliseconds}ms");
                    // penalize the device if it took too long to update
                    disconnectCounter++;

                    // disconnect device if it takes too long to update
                    if (disconnectCounter < DiscountTries)
                        continue;

                    Global.logger.LogLine($"Device {PrettyName} disconnected");
                    this.Disconnect();
                    return;
                }
                catch (TaskCanceledException)
                {
                    this.Disconnect();
                    return;
                }
                catch (Exception exception)
                {
                    Global.logger.LogLine($"ERROR {exception}");
                    this.Disconnect();
                    return;
                }
            }

            Dispose();
        }

        private async Task<bool> ProcessQueue()
        {
            Dictionary<byte, Color> keyColors = new Dictionary<byte, Color>();
            Dictionary<byte, Color> ledColors = new Dictionary<byte, Color>();

            lock (colorQueue)
            {
                Dictionary<DeviceKeys, Color> colors = null;
                while (colorQueue.Count > 0)
                {
                    if (colorQueue.TryDequeue(out colors))
                    {
                        foreach (var color in colors)
                        {
                            // Try LED mappings first
                            if (QMKKeycodes.LEDMappings.ContainsKey(color.Key))
                            {
                                var led = Convert.ToByte(QMKKeycodes.LEDMappings[color.Key]);
                                if (!ledColors.ContainsKey(led))
                                    ledColors.Add(led, color.Value);
                                else ledColors[led] = color.Value;
                            }
                            
                            else if (QMKKeycodes.KeyMappings.ContainsKey(color.Key))
                            {
                                var keycode = Convert.ToByte(QMKKeycodes.KeyMappings[color.Key]);

                                if (!keyColors.ContainsKey(keycode))
                                    keyColors.Add(keycode, color.Value);
                                else keyColors[keycode] = color.Value;
                            }
                        }
                    }
                }
            }

            return SetLampsFeature(ledColors);
//            return await SetColorsInternal(ledColors, SET_LED_COMMAND) && await SetColorsInternal(keyColors, SET_KEY_COMMAND);            
        }


        private bool SetLampsFeature(Dictionary<byte, Color> colors)
        {
            if (colors == null || !colors.Any())
                return false;
            try
            {
                if (!this.IsConnected)
                    return false;

                List<byte[]> featureReports = new List<byte[]>();

                int chunkSize = 8;
                int processed = 0;
                var chunk = colors.Take(chunkSize).ToArray();
                while (chunk.Length > 0)
                {
                    int padding = Math.Max(0, 8 - chunk.Length);
                    using (var reportStream = new MemoryStream(reportMaxSize))
                    {
                        reportStream.WriteByte(0x04);
                        reportStream.WriteByte(Convert.ToByte(chunk.Length));
                        reportStream.WriteByte(0x00);
                        foreach (var color in chunk)
                        {
                            reportStream.WriteByte(color.Key);
                            reportStream.WriteByte(0x00);
                        }
                        reportStream.Seek(padding * 2, SeekOrigin.Current);

                        foreach (var color in chunk)
                        {
                            reportStream.WriteByte(color.Value.R);
                            reportStream.WriteByte(color.Value.G);
                            reportStream.WriteByte(color.Value.B);
                            reportStream.WriteByte(color.Value.A);
                        }
                        reportStream.Seek(padding * 4, SeekOrigin.Current);

                        featureReports.Add(reportStream.ToArray());
                        processed += chunk.Length;
                    }
                    chunk = colors.Skip(processed).Take(chunkSize).ToArray();
                }
                featureReports.Last()[2] = 0x01;
                foreach (var report in featureReports) ctrl_device_leds.WriteFeatureData(report);
                return true;
            }
            catch (Exception exc)
            {
                Global.logger.LogLine($"Error when attempting to close UnifiedHID device:\n{exc}", Logging_Level.Error);
                return false;
            }

        }

        public override bool SetLEDColour(DeviceKeys key, byte red, byte green, byte blue)
        {
            if (QMKKeycodes.LEDMappings.ContainsKey(key))
                lampArray.SetColorForIndex(QMKKeycodes.LEDMappings[key], Windows.UI.Color.FromArgb(255, red, green, blue));
            else
            {
                var formsKey = KeyUtils.GetFormsKey(key);
                if (formsKey == System.Windows.Forms.Keys.None)
                    return false;

                lampArray.SetColorsForKey(Windows.UI.Color.FromArgb(255, red, green, blue), (Windows.System.VirtualKey)formsKey);                
            }
            return true;
        }   

        public override bool SetMultipleLEDColour(Dictionary<DeviceKeys, Color> keyColors)
        {
            foreach (var item in keyColors)
            {
                SetLEDColour(item.Key, item.Value.R, item.Value.G, item.Value.B);
            }
            return true;
        }

        public virtual void Dispose()
        {
            tokenSource?.Dispose();
            ctrl_device_leds?.CloseDevice();
        }
    }
}
