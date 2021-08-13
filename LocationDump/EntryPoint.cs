using AssetShards;
using BepInEx;
using BepInEx.IL2CPP;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BoosterImplants;
using CellMenu;
using DropServer;
using Gear;
using HarmonyLib;
using LevelGeneration;
using UnhollowerRuntimeLib;

namespace catrice.LocationDump
{
    [BepInPlugin(GUID, "LocationDump", "1.0.0")]
    [BepInProcess("GTFO.exe")]
    public class EntryPoint : BasePlugin
    {
        public const string GUID = "com.catrice.LocationDump";
        public override void Load()
        {

            Logger.LogInstance = Log;

            //ClassInjector.RegisterTypeInIl2Cpp<BoosterHack>();
            var harmony = new Harmony(GUID);




            {
                ClassInjector.RegisterTypeInIl2Cpp<LocationDump>();
            }
            

            harmony.PatchAll();
            //AssetShardManager.add_OnStartupAssetsLoaded((Il2CppSystem.Action)OnAssetLoaded);
        }

        private bool once = false;
        /*
        private void OnAssetLoaded()
        {
            if (once)
                return;
            once = true;

            PartialDataManager.UpdatePartialData();
            PartialDataManager.WriteToFile(Path.Combine(PartialDataManager.PartialDataPath, "persistentID.json"));
        }
        */
    }
}