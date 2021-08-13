using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BoosterImplants;
using CellMenu;
using Dissonance;
using DropServer;
using GameData;
using Gear;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;
using Il2CppSystem.Linq.Expressions.Interpreter;
using Il2CppSystem.Threading;
using Il2CppSystem.Threading.Tasks;
using LevelGeneration;
using Player;
using TMPro;
using UnityEngine;
using Object = Il2CppSystem.Object;

using Player;
using TMPro;
using UnityEngine;
using System.Threading;
using System.Transactions;
using Agents;
using Il2CppSystem.Resources;
using PlayFab.ClientModels;
using SNetwork;
using System.IO.MemoryMappedFiles;
using AIGraph;
using Il2CppSystem.IO;
using UnhollowerBaseLib;
using UnhollowerRuntimeLib;
using CancellationToken = Il2CppSystem.Threading.CancellationToken;
using BinaryWriter = System.IO.BinaryWriter;
using Path = System.IO.Path;
using Assembly = System.Reflection.Assembly;

namespace catrice.LocationDump
{


    //From https://github.com/Flowaria/MTFO.Ext.PartialData
    [HarmonyPatch(typeof(CM_PageRundown_New), "PlaceRundown")]
    public class PrepareInjection
    {
        public static bool InjectedFlag = false;
        // Token: 0x06000005 RID: 5 RVA: 0x00002404 File Offset: 0x00000604
        [HarmonyPostfix]
        public static void PostFix()
        {
            if (InjectedFlag == false)
            {
                InjectedFlag = true;
                GameObject gameObject = new GameObject();
                gameObject.AddComponent<LocationDump>();
                UnityEngine.Object.DontDestroyOnLoad(gameObject);
            }
        }

        // Token: 0x0400000B RID: 11
        private static bool _isInjected;

        // Token: 0x0400000C RID: 12
        public static GameObject _obj;
    }

    //From: https://github.com/Endskill/PlaytimeTimer
    public class LocationDump : MonoBehaviour
    {


        // Token: 0x06000003 RID: 3 RVA: 0x0000209F File Offset: 0x0000029F
        public LocationDump(IntPtr intPtr) : base(intPtr)
        {
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string frontend = Path.Combine(assemblyFolder, "frontend.exe");
            if (System.IO.File.Exists(frontend))
            {
                
                using (System.Diagnostics.Process myProcess = new System.Diagnostics.Process())
                {
                    myProcess.StartInfo.UseShellExecute = false;
                    // You can start any process, HelloWorld is a do-nothing example.
                    myProcess.StartInfo.FileName = frontend;
                    myProcess.StartInfo.Arguments = $"{(ConfigManager.IsTopMost ? "Top" : "")}";
                    myProcess.StartInfo.WorkingDirectory = assemblyFolder;
                    myProcess.StartInfo.CreateNoWindow = true;
                    myProcess.Start();
                    // This code assumes the process you are starting will terminate itself.
                    // Given that it is started without a window so you cannot terminate it
                    // on the desktop, it must terminate itself or you can do it programmatically
                    // from this application using the Kill method.
                }
            }
        }

        private System.Threading.Timer _intervalTimer;
        private bool update_ { get; set; } = false;

        private MemoryMappedFile handle_ = null;

        public void Awake()
        {
            handle_ = MemoryMappedFile.CreateOrOpen("LocationDump", 1024);
        }

        public void FixedUpdate()
        {
            update_ = true;
            var exp = RundownManager.Current;
            
            if (!exp.IsGameSessionActive) return;
            var levelName = exp.m_activeExpedition.Descriptive.PublicName;
            if (handle_ == null) return;
            var player = PlayerManager.Current?.m_localPlayerAgentInLevel;
            if (player == null) return;
            var controller = player.gameObject.GetComponent<PlayerCharacterController>();
            if (controller == null)
            {
                Logger.Log("Invalid controller.");
                return;
            }
            using (MemoryMappedViewStream stream = handle_.CreateViewStream(0, 32))
            {
                BinaryWriter writer = new BinaryWriter(stream);
                writer.Write(levelName);
            }
            using (MemoryMappedViewStream stream = handle_.CreateViewStream(32, 32))
            {
                BinaryWriter writer = new BinaryWriter(stream);
                writer.Write((float)controller.Position.x);
                writer.Write((float)controller.Position.z);
            }
        }
        
        
    }
    
    
}
