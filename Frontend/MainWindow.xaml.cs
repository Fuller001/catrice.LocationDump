using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Web.Script.Serialization;

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

        public static void SetTopomost(IntPtr hWnd)
        {
            WindowRect rect = default;
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

    public partial class MainWindow : Window
    {
        private static readonly string LocationsJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "map-locations.json");
        private float _previousX;
        private float _previousY;
        private readonly object _stateLock = new object();
        private HttpServerState _httpState = new HttpServerState();
        private HTTPServer _httpServer;
        private bool _mapConfigErrorNotified;

        public MainWindow()
        {
            InitializeComponent();

            UpdateHttpState(state =>
            {
                state.Scale = scaleSlider.Value;
            });

            try
            {
                _httpServer = new HTTPServer(31226, GetHttpStateSnapshot, AppDomain.CurrentDomain.BaseDirectory);
                _httpServer.Start();
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                {
                    MessageBox.Show(this, "无法启动HTTP服务器: " + ex.Message, "HTTP 服务器", MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
            }

            Task.Run(RunLoop);
        }

        private bool TryLoadLocationRef(string levelName, out LocationRef location)
        {
            location = default;

            try
            {
                if (!File.Exists(LocationsJsonPath))
                {
                    NotifyMapConfigErrorOnce("未找到地图配置文件: " + LocationsJsonPath);
                    return false;
                }

                string json = File.ReadAllText(LocationsJsonPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    NotifyMapConfigErrorOnce("地图配置文件为空。");
                    return false;
                }

                var serializer = new JavaScriptSerializer();
                var data = serializer.Deserialize<Dictionary<string, LocationRefDto>>(json);
                if (data != null)
                {
                    foreach (var kvp in data)
                    {
                        if (string.Equals(kvp.Key, levelName, StringComparison.OrdinalIgnoreCase))
                        {
                            _mapConfigErrorNotified = false;
                            location = kvp.Value.ToLocationRef();
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                NotifyMapConfigErrorOnce("读取地图配置失败: " + ex.Message);
            }

            System.Diagnostics.Debug.WriteLine($"[LocationDump] 未找到地图配置: {levelName}");
            return false;
        }

        private void NotifyMapConfigErrorOnce(string message)
        {
            if (_mapConfigErrorNotified)
            {
                return;
            }

            _mapConfigErrorNotified = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                MessageBox.Show(this, message, "地图配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }));
        }

        private class LocationRefDto
        {
            public double refX1 { get; set; }
            public double refY1 { get; set; }
            public double imgX1 { get; set; }
            public double imgY1 { get; set; }
            public double refX2 { get; set; }
            public double refY2 { get; set; }
            public double imgX2 { get; set; }
            public double imgY2 { get; set; }
            public double width { get; set; }
            public double height { get; set; }
            public string ImagePath { get; set; }

            public LocationRef ToLocationRef()
            {
                return new LocationRef
                {
                    refX1 = refX1,
                    refY1 = refY1,
                    imgX1 = imgX1,
                    imgY1 = imgY1,
                    refX2 = refX2,
                    refY2 = refY2,
                    imgX2 = imgX2,
                    imgY2 = imgY2,
                    width = width,
                    height = height,
                    ImagePath = ImagePath
                };
            }
        }

        public void RunLoop()
        {
            MemoryMappedFile handle = null;
            double xScale = double.NaN;
            double yScale = double.NaN;
            double origX = double.NaN;
            double origY = double.NaN;
            double dstX = double.NaN;
            double dstY = double.NaN;
            double widthImg = double.NaN;
            double heightImg = double.NaN;
            string currentLevel = string.Empty;

            Thread.Sleep(2000);

            Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                var wih = new WindowInteropHelper(this);
                IntPtr hWnd = wih.Handle;
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && args[1] == "Top")
                {
                    TopMostWindow.SetTopomost(hWnd);
                }
            }));

            while (true)
            {
                Thread.Sleep(20);

                if (handle == null)
                {
                    try
                    {
                        handle = MemoryMappedFile.OpenExisting("LocationDump");
                        continue;
                    }
                    catch
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                }

                string levelName;
                using (MemoryMappedViewStream stream = handle.CreateViewStream(0, 32))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    levelName = reader.ReadString();
                }

                float x;
                float y;
                using (MemoryMappedViewStream stream = handle.CreateViewStream(32, 32))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    x = reader.ReadSingle();
                    y = reader.ReadSingle();
                }

                UpdateHttpState(state =>
                {
                    state.LevelName = levelName;
                    state.PlayerX = x;
                    state.PlayerY = y;
                });

                Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                {
                    Title = $"{levelName}, {x:F5}, {y:F5}";
                }));

                if (Math.Abs(x - _previousX) > 0.0001 || Math.Abs(y - _previousY) > 0.0001)
                {
                    double angle = Math.Atan2(x - _previousX, y - _previousY) * (180.0 / Math.PI);
                    UpdateHttpState(state => state.PointerAngle = angle);
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                    {
                        rotator.Angle = angle;
                    }));
                    _previousX = x;
                    _previousY = y;
                }

                if (currentLevel != levelName)
                {
                    if (!TryLoadLocationRef(levelName, out var locInfo))
                    {
                        continue;
                    }

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
                        CenterImg.Source = new BitmapImage(new Uri("file://" + Environment.CurrentDirectory + "/" + locInfo.ImagePath));
                        cursor.Width = 32;
                        cursor.Height = 32;
                        UpdateHttpState(state =>
                        {
                            state.ImagePath = locInfo.ImagePath;
                            state.MapWidth = locInfo.width;
                            state.MapHeight = locInfo.height;
                        });
                    }));

                    currentLevel = levelName;
                }

                if (!double.IsNaN(xScale))
                {
                    double calcX = xScale * (x - origX) + dstX;
                    double calcY = yScale * (y - origY) + dstY;

                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                    {
                        double viewportWidth = LayoutRoot?.ActualWidth ?? ActualWidth;
                        double viewportHeight = LayoutRoot?.ActualHeight ?? ActualHeight;

                        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
                        {
                            viewportWidth = Width;
                        }

                        if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
                        {
                            viewportHeight = Height;
                        }

                        double scale = scaleSlider.Value;
                        double finalX = calcX * scale - viewportWidth / 2.0;
                        double finalY = calcY * scale - viewportHeight / 2.0;
                        double width = widthImg * scale;
                        double height = heightImg * scale;
                        CenterImg.Width = width;
                        CenterImg.Height = height;
                        CenterImg.Margin = new Thickness(-finalX, -finalY, viewportWidth - (-finalX + width), viewportHeight - (-finalY + height));
                        UpdateHttpState(state =>
                        {
                            state.PointerX = calcX;
                            state.PointerY = calcY;
                            state.Scale = scale;
                            state.LastUpdatedUtc = DateTime.UtcNow;
                        });
                    }));
                }
            }
        }

        private HttpServerState GetHttpStateSnapshot()
        {
            lock (_stateLock)
            {
                return _httpState.Clone();
            }
        }

        private void UpdateHttpState(Action<HttpServerState> updater)
        {
            if (updater == null)
            {
                return;
            }

            HttpServerState snapshot;
            lock (_stateLock)
            {
                updater(_httpState);
                snapshot = _httpState.Clone();
            }

            _httpServer?.PublishState(snapshot);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _httpServer?.Dispose();
            _httpServer = null;
        }
    }
}
