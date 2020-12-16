using Aurora.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;

namespace Aurora.Devices.QMK
{
    class LampArrayDevice : IDevice
    {
        private bool isInitialized = false;
        private List<LampArray> lampArrays = new List<LampArray>();
        private DeviceWatcher deviceWatcher;
        private Thread thread;
        private DeviceColorComposition colorComposition;

        public string DeviceName => "LampArray";

        public string DeviceDetails => isInitialized ? $"Initialized with {lampArrays.Count} devices" : "Not Initialized";

        public string DeviceUpdatePerformance => "N/A";

        public bool IsInitialized => isInitialized;

        public VariableRegistry RegisteredVariables => new VariableRegistry();

        public bool Initialize()
        {
            if (this.isInitialized)
                return true;

            deviceWatcher = DeviceInformation.CreateWatcher(LampArray.GetDeviceSelector());
            deviceWatcher.Added += DeviceWatcher_Added;
            deviceWatcher.Removed += DeviceWatcher_Removed;
            deviceWatcher.Start();

            return this.isInitialized = true;
        }

        private void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            lampArrays.RemoveAll(l => l.DeviceId == args.Id);
        }

        private async void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            var lampArray = await LampArray.FromIdAsync(args.Id);
            if (lampArray != null)
            {
                lampArrays.Add(lampArray);
                int[] ix = new int[lampArray.LampCount];
                for (int i = 0; i < lampArray.LampCount; i++) ix[i] = i;
                var effect = new Windows.Devices.Lights.Effects.LampArrayCustomEffect(lampArray, ix);
                effect.UpdateRequested += Effect_UpdateRequested;
                effect.Duration = TimeSpan.MaxValue;
                effect.UpdateInterval = TimeSpan.FromMilliseconds(12);
                var playlist = new Windows.Devices.Lights.Effects.LampArrayEffectPlaylist();
                playlist.Append(effect);
                playlist.Start();
            }
        }

        private void Effect_UpdateRequested(Windows.Devices.Lights.Effects.LampArrayCustomEffect sender, Windows.Devices.Lights.Effects.LampArrayUpdateRequestedEventArgs args)
        {
            foreach (var kv in colorComposition.keyColors)
            {
                if (QMKKeycodes.LEDMappings.ContainsKey(kv.Key))
                    args.SetColorForIndex(QMKKeycodes.LEDMappings[kv.Key], Windows.UI.Color.FromArgb(255, kv.Value.R, kv.Value.G, kv.Value.B));
            }
        }

        /*
        private void Effect_BitmapRequested(Windows.Devices.Lights.Effects.LampArrayBitmapEffect sender, Windows.Devices.Lights.Effects.LampArrayBitmapRequestedEventArgs args)
        {
            using (var stream = new MemoryStream())
            {
                keyBitmap.Save(stream, ImageFormat.Bmp);
                var buf = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(stream.ToArray());
                var bmp = new Windows.Graphics.Imaging.SoftwareBitmap(Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8, keyBitmap.Width, keyBitmap.Height);
                bmp.CopyFromBuffer(buf);
                args.UpdateBitmap(bmp);
            }
        }*/

        public void Reset()
        {
            deviceWatcher.Stop();
            lampArrays.Clear();
            deviceWatcher.Start();
        }

        public void Shutdown()
        {
            deviceWatcher.Stop();
            lampArrays.Clear();
        }

        public bool UpdateDevice(Dictionary<DeviceKeys, Color> keyColors, DoWorkEventArgs e, bool forced = false)
        {
            return false;
        }

        public bool UpdateDevice(DeviceColorComposition colorComposition, DoWorkEventArgs e, bool forced = false)
        {
            this.colorComposition = colorComposition;
            return true;
        }
    }
}
