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
        private Windows.Devices.Lights.LampArray lampArray;

        public MassdropAlt()
        {
            PrettyName = "Massdrop ALT";
            IsKeyboard = true;
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

            return IsConnected = (lampArray != null);
        }

        public override bool Disconnect()
        {
            try
            {
                lampArray = null;
                return true;
            } 
            catch (Exception exc)
            {
                Global.logger.LogLine($"Error when attempting to close UnifiedHID device:\n{exc}", Logging_Level.Error);

            }
            return false;

        }

        public override bool SetLEDColour(DeviceKeys key, byte red, byte green, byte blue)
        {
            if (QMKKeycodes.LEDMappings.ContainsKey(key))
                lampArray.SetColorForIndex(QMKKeycodes.LEDMappings[key], Windows.UI.Color.FromArgb(255, red, green, blue));
            else
            {
                var formsKey = KeyUtils.GetFormsKey(key);
                if (formsKey == 0 || (int)formsKey > 255)
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
    }
}
