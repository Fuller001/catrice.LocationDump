using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Dissonance;

namespace catrice.LocationDump
{


    public static class ConfigManager
    {
        // Token: 0x06000035 RID: 53 RVA: 0x00002B60 File Offset: 0x00000D60
        static ConfigManager()
        {
            string text = Path.Combine(Paths.ConfigPath, "LocationDump.cfg");
            ConfigFile configFile = new ConfigFile(text, true);
            ConfigManager._IsTopMost = configFile.Bind<bool>("Nyan", "IsTopMost",
                false, "Indicates whether ExternalMinimap window is topmost.");
        }

        // Token: 0x17000010 RID: 16
        // (get) Token: 0x06000036 RID: 54 RVA: 0x00002EA4 File Offset: 0x000010A4
        public static bool IsTopMost
        {
            get { return ConfigManager._IsTopMost.Value; }
        }

        private static ConfigEntry<bool> _IsTopMost;
    }
}