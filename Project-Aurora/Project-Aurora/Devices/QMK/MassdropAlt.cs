using Aurora.Devices.UnifiedHID;
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

namespace Aurora.Devices.QMK
{
    class MassdropAlt : UnifiedBase
    {
        private static HidDevice ctrl_device;
        private static HidDevice ctrl_device_leds;
        private CancellationTokenSource tokenSource = new CancellationTokenSource();
        private readonly int frameRateMillis;
        private readonly Stopwatch stopwatch = new Stopwatch();
        public long LastUpdateMillis { get; private set; }
        public bool Active { get; private set; }


        private readonly ConcurrentQueue<Dictionary<DeviceKeys, Color>> colorQueue = new ConcurrentQueue<Dictionary<DeviceKeys, Color>>();
        private const int DiscountLimit = 2500;
        private const int DiscountTries = 3;
        private int disconnectCounter = 0;


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
            IEnumerable<HidDevice> devices = HidDevices.Enumerate(0x04D8, new int[] { 0xEED3 });
            try
            {
                if (devices.Count() > 0)
                {
                    ctrl_device_leds = devices.First(dev => dev.Capabilities.UsagePage == 0x00BE && dev.Capabilities.Usage == 0x00EF);
                    /*ctrl_device = devices.First(dev => dev.Capabilities.FeatureReportByteLength > 50);
                    ctrl_device.OpenDevice();*/
                    ctrl_device_leds.OpenDevice();
                    bool success = true;
/*                    if (!success)
                    {
                        Global.logger.LogLine($"Roccat Tyon Could not connect\n", Logging_Level.Error);
                        ctrl_device.CloseDevice();
                        ctrl_device_leds.CloseDevice();
                    }*/
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
                tokenSource.Cancel();
                ctrl_device_leds.CloseDevice();
                IsConnected = false;
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

            return await SetColorsInternal(ledColors, 0xC4) && await SetColorsInternal(keyColors, 0xC5);            
        }


        public override bool SetLEDColour(DeviceKeys key, byte red, byte green, byte blue)
        {
            colorQueue.Enqueue(new Dictionary<DeviceKeys, Color> {{ key, Color.FromArgb(red, green, blue) }});
            return true;
        }

        public override bool SetMultipleLEDColour(Dictionary<DeviceKeys, Color> keyColors)
        {
            colorQueue.Enqueue(keyColors);
            return true;
        }

        private async Task<bool> SetColorsInternal(Dictionary<byte, Color> colors, byte valueId)
        {
            if (colors == null || !colors.Any())
                return false;
            try
            {
                if (!this.IsConnected)
                    return false;
                byte[] reportHeader = new byte[] { 0x07, valueId, 0x00 };
                List<HidReport> reports = new List<HidReport>();

                MemoryStream reportData = new MemoryStream(64);
                reportData.Write(reportHeader, 0, 3);
                int count = 0;
                foreach (var color in colors)
                {
                    if (count >= 60 / 4)
                    {
                        var report = new HidReport(64);
                        report.ReportId = 0;
                        report.Data = reportData.ToArray();
                        report.Data[2] = Convert.ToByte(count);
                        reports.Add(report);
                        reportData = new MemoryStream(64);
                        reportData.Write(reportHeader, 0, 3);
                        count = 0;
                    }
                    reportData.WriteByte(color.Value.R);
                    reportData.WriteByte(color.Value.G);
                    reportData.WriteByte(color.Value.B);
                    reportData.WriteByte(color.Key);
                    count++;                     
                }

                var finalReport = new HidReport(64);

                finalReport.ReportId = 0;
                finalReport.Data = reportData.ToArray();
                finalReport.Data[2] = Convert.ToByte(count);
                reports.Add(finalReport);

                foreach (var report in reports) await ctrl_device_leds.WriteReportAsync(report);
                return true;
            }
            catch (Exception exc)
            {
                Global.logger.LogLine($"Error when attempting to close UnifiedHID device:\n{exc}", Logging_Level.Error);
                return false;
            }

        }

        public virtual void Dispose()
        {
            tokenSource?.Dispose();
            ctrl_device_leds?.CloseDevice();
        }
    }
}
