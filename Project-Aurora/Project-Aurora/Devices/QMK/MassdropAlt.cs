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

namespace Aurora.Devices.QMK
{
    public static class Extensions
    {
        /// <summary>
        /// Break a list of items into chunks of a specific size
        /// </summary>
        public static IEnumerable<IEnumerable<T>> Chunk<T>(this IEnumerable<T> source, int chunksize)
        {
            while (source.Any())
            {
                yield return source.Take(chunksize);
                source = source.Skip(chunksize);
            }
        }
    }

    class MassdropAlt : UnifiedBase
    {
        private static HidDevice ctrl_device;
        private static HidDevice ctrl_device_leds;
        private CancellationTokenSource tokenSource = new CancellationTokenSource();
        private readonly int frameRateMillis;
        private readonly Stopwatch stopwatch = new Stopwatch();
        public long LastUpdateMillis { get; private set; }
        public bool Active { get; private set; }
        private Dictionary<QMKKeycodes.Keys, List<int>> LampsByKey = new Dictionary<QMKKeycodes.Keys, List<int>>();
        private int maxLampsPerReport = 0;


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
                    ctrl_device_leds = devices.First(dev => dev.Capabilities.UsagePage == 0x00BE && dev.Capabilities.Usage == 0x0001);
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

                    maxLampsPerReport = (ctrl_device_leds.Capabilities.FeatureReportByteLength - 3) / 6;

                    var responseReport = new byte[ctrl_device_leds.Capabilities.FeatureReportByteLength];
                    ctrl_device_leds.ReadFeatureData(out responseReport, 1);
                    var lampCount = (responseReport[2] << 8) | (responseReport[1] & 0xFF);
                    var updateInterval = (responseReport[16] << 24) | (responseReport[15] << 16) | (responseReport[14] << 8) | (responseReport[13] & 0xFF);

                    var requestReport = new byte[] { 0x02, 0x00, 0x00 };
                    ctrl_device_leds.WriteFeatureData(requestReport);

                    int[] lampKeycodes = new int[lampCount];
                    for (int i = 0; i < lampCount; i++)
                    {
                        ctrl_device_leds.ReadFeatureData(out responseReport, 3);
                        var key = (QMKKeycodes.Keys)responseReport[28];
                        if (key == QMKKeycodes.Keys.KC_NO) continue;
                        if (LampsByKey.ContainsKey(key))
                            LampsByKey[key].Add(i);
                        else
                            LampsByKey.Add(key, new List<int> { i });
                    }

                    requestReport = new byte[] { 0x06, 0x00 };
                    ctrl_device_leds.WriteFeatureData(requestReport);

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
                var requestReport = new byte[] { 0x06, 0x01 };
                ctrl_device_leds.WriteFeatureData(requestReport);
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
                    ProcessQueue();
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

        private bool ProcessQueue()
        {
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
                            else
                            {
                                if (QMKKeycodes.KeyMappings.TryGetValue(color.Key, out var keycode))
                                {
                                    if (!LampsByKey.TryGetValue(keycode, out var lamps))
                                        continue;
                                    foreach (var lampId in lamps)
                                    {
                                        var led = Convert.ToByte(lampId);
                                        if (!ledColors.ContainsKey(led))
                                            ledColors.Add(led, color.Value);
                                        else ledColors[led] = color.Value;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return SetColorsInternal(ledColors, true);
        }

        public override bool SetLEDColour(DeviceKeys key, byte red, byte green, byte blue)
        {
            colorQueue.Enqueue(new Dictionary<DeviceKeys, Color> { { key, Color.FromArgb(red, green, blue) } });
            return true;
        }

        public override bool SetMultipleLEDColour(Dictionary<DeviceKeys, Color> keyColors)
        {
            colorQueue.Enqueue(keyColors);
            return true;
        }

        private bool SetColorsInternal(Dictionary<byte, Color> colors, bool flush)
        {
            if (colors == null || !colors.Any() || !this.IsConnected)
                return false;
            try
            {
                var chunked = colors.Chunk(maxLampsPerReport).ToArray();

                for (int i = 0; i < chunked.Length; i++)
                {
                    var chunk = chunked[i];
                    MemoryStream reportData = new MemoryStream(ctrl_device_leds.Capabilities.FeatureReportByteLength);
                    reportData.WriteByte(0x04);
                    reportData.WriteByte(Convert.ToByte(chunk.Count()));
                    flush &= (i == (chunked.Length - 1));
                    if (flush)
                        reportData.WriteByte(0x01);
                    else
                        reportData.WriteByte(0x00);

                    foreach (var lamp in chunk)
                    {
                        reportData.WriteByte(lamp.Key);
                        reportData.WriteByte(0x00);
                    }
                    foreach (var lamp in chunk)
                    {
                        reportData.WriteByte(lamp.Value.R);
                        reportData.WriteByte(lamp.Value.G);
                        reportData.WriteByte(lamp.Value.B);
                        reportData.WriteByte(lamp.Value.A);
                    }

                    ctrl_device_leds.WriteFeatureData(reportData.ToArray());
                }
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
