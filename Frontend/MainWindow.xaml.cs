using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Frontend
{
    public class TopMostWindow
    {
        public const int HWND_TOP = 0;
        public const int HWND_BOTTOM = 1;
        public const int HWND_TOPMOST = -1;
        public const int HWND_NOTOPMOST = -2;


        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint wFlags);


        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out WindowRect lpRect);

        /// <summary>
        /// 设置窗体为TopMost
        /// </summary>
        /// <param name="hWnd"></param>
        public static void SetTopomost(IntPtr hWnd)
        {
            WindowRect rect = new WindowRect();
            GetWindowRect(hWnd, out rect);
            SetWindowPos(hWnd, (IntPtr)HWND_TOPMOST, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0);
        }
    }

    public struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public struct LocationRef
    {
        public double refX1, refY1;
        public double imgX1, imgY1;

        public double refX2, refY2;
        public double imgX2, imgY2;

        public double width, height;

        public string ImagePath;
    }


    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            LocInfo.Add("Floodways", new LocationRef
            {
                refX1 = -30.53606f,
                refY1 = 33.62107f,
                imgX1 = 1042f,
                imgY1 = 1188f,
                refX2 = 154.68150f,
                refY2 = 158.36510f,
                imgX2 = 1941f,
                imgY2 = 578f,
                width = 2390,
                height = 1623,
                ImagePath = "R5A1.png"
            });

            LocInfo.Add("Recollect", new LocationRef
            { 
                refX1 = -15.68219f, refY1 = 33.39020f, 
                imgX1 = 3438f, imgY1 = 2238f, 
                refX2 = 21.66232f, refY2 = 66.58588f, 
                imgX2 = 3922f, imgY2 = 1805f,
                width = 4961, height = 3128,
                ImagePath = "R5A2.png"
            });

            LocInfo.Add("Mining", new LocationRef
            {
                refX1 = -15.48526f,
                refY1 = 35.48653f,
                imgX1 = 6273f,
                imgY1 = 5236f,
                refX2 = -222.14380f,
                refY2 = 157.75900f,
                imgX2 = 3092f,
                imgY2 = 3344f,
                width = 10442,
                height = 6441,
                ImagePath = "R5A3.png"
            });

            LocInfo.Add("Smother", new LocationRef
            {
                refX1 = -92.53666f,
                refY1 = 33.50102f,
                imgX1 = 223f,
                imgY1 = 3096f,
                refX2 = 41.55623f,
                refY2 = 139.47990f,
                imgX2 = 2319f,
                imgY2 = 1445f,
                width = 8192,
                height = 8168,
                ImagePath = "R5B1.png"
            });

            LocInfo.Add("Discharge", new LocationRef
            {
                refX1 = 141.84010f,
                refY1 = 38.63810f,
                imgX1 = 4767f,
                imgY1 = 4057f,
                refX2 = -5.77276f,
                refY2 = 274.48680f,
                imgX2 = 2455f,
                imgY2 = 362f,
                width = 6255,
                height = 7192,
                ImagePath = "R5B2.png"
            });

            LocInfo.Add("Unseal", new LocationRef
            {
                refX1 = 150.20460f,
                refY1 = 33.66447f,
                imgX1 = 2923f,
                imgY1 = 3009f,
                refX2 = -10.64170f,
                refY2 = 195.12910f,
                imgX2 = 416f,
                imgY2 = 481f,
                width = 4410,
                height = 5726,
                ImagePath = "R5B3.png"
            });

            LocInfo.Add("Diversion", new LocationRef
            {
                refX1 = -93.98196f,
                refY1 = 34.98185f,
                imgX1 = 1248f,
                imgY1 = 7104f,
                refX2 = 8.39555f,
                refY2 = 286.54610f,
                imgX2 = 2840f,
                imgY2 = 3167f,
                width = 5366,
                height = 8171,
                ImagePath = "R5B4.png"
            });

            LocInfo.Add("Binary", new LocationRef
            {
                refX1 = 153.99590f,
                refY1 = 97.71357f,
                imgX1 = 4937f,
                imgY1 = 5030f,
                refX2 = -86.11004f,
                refY2 = 284.36490f,
                imgX2 = 1207f,
                imgY2 = 2084f,
                width = 5203,
                height = 7184,
                ImagePath = "R5C1.png"
            });

            LocInfo.Add("Access", new LocationRef
            {
                refX1 = -281.10670f,
                refY1 = 37.05779f,
                imgX1 = 2149f,
                imgY1 = 4050f,
                refX2 = 86.86817f,
                refY2 = 277.01640f,
                imgX2 = 7875f,
                imgY2 = 309f,
                width = 9669,
                height = 5197,
                ImagePath = "R5C2.png"
            });

            LocInfo.Add("Starvation", new LocationRef
            {
                refX1 = -27.15706f,
                refY1 = 97.54346f,
                imgX1 = 3948f,
                imgY1 = 8174f,
                refX2 = -208.65670f,
                refY2 = 289.90450f,
                imgX2 = 1138f,
                imgY2 = 5174f,
                width = 5962,
                height = 10207,
                ImagePath = "R5C3.png"
            });

            LocInfo.Add("Even Deeper", new LocationRef
            {
                refX1 = 94.37240f,
                refY1 = 161.55690f,
                imgX1 = 2571f,
                imgY1 = 5768f,
                refX2 = -94.63663f,
                refY2 = 610.64720f,
                imgX2 = 707f,
                imgY2 = 1328f,
                width = 3227,
                height = 7709,
                ImagePath = "R5D1.png"
            });

            LocInfo.Add("Error", new LocationRef
            {
                refX1 = 222.31290f,
                refY1 = 33.37928f,
                imgX1 = 7409f,
                imgY1 = 4104f,
                refX2 = -154.00380f,
                refY2 = 228.45910f,
                imgX2 = 1554f,
                imgY2 = 1075f,
                width = 9529,
                height = 5532,
                ImagePath = "R5D2.png"
            });

            LocInfo.Add("KDS Deep", new LocationRef
            {
                refX1 = 29.83016f,
                refY1 = 34.39783f,
                imgX1 = 5459f,
                imgY1 = 1861f,
                refX2 = -273.94760f,
                refY2 = -448.13540f,
                imgX2 = 726f,
                imgY2 = 9359f,
                width = 5735,
                height = 12843,
                ImagePath = "R5E1.png"
            });

            System.Threading.Tasks.Task.Run(RunLoop);
        }

        public Dictionary<string, LocationRef> LocInfo = new Dictionary<string, LocationRef>();

        public void RunLoop()
        {
            MemoryMappedFile handle = null;
            double xScale = Double.NaN;
            double yScale = Double.NaN;
            double origX = Double.NaN;
            double origY = Double.NaN;
            double dstX = Double.NaN;
            double dstY = Double.NaN;
            double widthImg = Double.NaN;
            string currentRundown = "";

            double heightImg = Double.NaN;

            Thread.Sleep(2000);

            Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                var wih = new WindowInteropHelper(this);
                IntPtr hWnd = wih.Handle;
                var args = Environment.GetCommandLineArgs();
                //this.Title = string.Join(",", args);
                if (args.Length > 1 && args[1] == "Top")
                {
                    TopMostWindow.SetTopomost(hWnd);
                }
            }));
            for (;;)
            {
                Thread.Sleep(20);
                if (handle == null)
                {
                    try
                    {

                        handle = MemoryMappedFile.OpenExisting("LocationDump");
                    }
                    catch (Exception e)
                    {
                        Thread.Sleep(2000);
                    }
                }
                else
                {
                    string levelName = "";
                    float x, y;
                    using (MemoryMappedViewStream stream = handle.CreateViewStream(0, 32))
                    {
                        BinaryReader reader = new BinaryReader(stream);
                        levelName = reader.ReadString();
                    }
                    using (MemoryMappedViewStream stream = handle.CreateViewStream(32, 32))
                    {
                        BinaryReader reader = new BinaryReader(stream);
                        x = reader.ReadSingle();
                        y = reader.ReadSingle();
                    }
                    /*
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal,(Action)(()=>
                    {
                        this.Title = $"{levelName}, {x:F5}, {y:F5}";
                    }));*/
                    if (currentRundown != levelName)
                    {
                        if (!LocInfo.ContainsKey(levelName))
                            continue;
                        var locInfo = LocInfo[levelName];
                        xScale = (locInfo.imgX2 - locInfo.imgX1) / (locInfo.refX2 - locInfo.refX1);
                        yScale = (locInfo.imgY2 - locInfo.imgY1) / (locInfo.refY2 - locInfo.refY1);
                        origX = locInfo.refX1;
                        origY = locInfo.refY1;
                        dstX = locInfo.imgX1;
                        dstY = locInfo.imgY1;
                        widthImg = locInfo.width;
                        heightImg = locInfo.height;
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                        {
                            CenterImg.Source =
                                new BitmapImage(new Uri($"file://{Environment.CurrentDirectory}/{locInfo.ImagePath}"));
                            {
                                //Cursor
                                var cursorW = 32;
                                var cursorH = 32;
                                cursor.Width = 64;
                                cursor.Height = 64;
                                cursor.Margin = new Thickness(Width / 2 - cursorW, Height / 2 - cursorH,
                                    Width / 2 - cursorW, Height / 2 - cursorH);
                            }
                        }));
                        currentRundown = levelName;

                    }

                    if (!double.IsNaN(xScale))
                    {
                        double calcX = xScale * (x - origX) + dstX;
                        double calcY = yScale * (y - origY) + dstY;
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                        {
                            var scale = scaleSlider.Value;
                            double finalX = calcX * scale - Width / 2;
                            double finalY = calcY * scale - Height / 2;
                            var width = widthImg * scale;
                            var height = heightImg * scale;
                            CenterImg.Width = width;
                            CenterImg.Height = height;
                            CenterImg.Margin = new Thickness(-finalX, -finalY, Width - (-finalX + width),
                                Height - (-finalY + height));
                        }));
                    }
                }
            }
        }
    }
}
