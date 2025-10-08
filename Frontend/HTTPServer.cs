using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Resources;

namespace Frontend
{
    internal sealed class HTTPServer : IDisposable
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly int _port;
        private readonly Func<HttpServerState> _stateProvider;
        private readonly string _contentRoot;
        private readonly string _webRoot;
        private readonly object _sync = new object();

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoop;

        private readonly ConcurrentDictionary<Guid, WebSocketClient> _webSocketClients = new ConcurrentDictionary<Guid, WebSocketClient>();
        private readonly SemaphoreSlim _broadcastLock = new SemaphoreSlim(1, 1);
        private string _pendingBroadcastJson;
        private int _broadcastScheduled;

        private string _cachedIndexHtml;
        private DateTime _cachedIndexTimestampUtc;

        public HTTPServer(int port, Func<HttpServerState> stateProvider, string contentRoot)
        {
            if (port <= 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _contentRoot = string.IsNullOrWhiteSpace(contentRoot) ? throw new ArgumentNullException(nameof(contentRoot)) : contentRoot;
            _webRoot = Path.Combine(_contentRoot, "WebAssets");
            _port = port;
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_listener != null)
                {
                    return;
                }

                _listener = new TcpListener(IPAddress.Any, _port);
                _cts = new CancellationTokenSource();
                _listener.Start();
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (_listener == null)
                {
                    return;
                }

                try
                {
                    _cts.Cancel();
                }
                catch
                {
                }

                try
                {
                    _listener.Stop();
                }
                catch
                {
                }

                _listener = null;
            }

            try
            {
                _acceptLoop?.Wait(2000);
            }
            catch
            {
            }
            finally
            {
                _acceptLoop = null;
                _cts?.Dispose();
                _cts = null;
            }

            foreach (var item in _webSocketClients.Keys.ToArray())
            {
                RemoveClient(item);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public void PublishState(HttpServerState state)
        {
            if (state == null)
            {
                return;
            }

            string json = BuildStateJson(state);
            Interlocked.Exchange(ref _pendingBroadcastJson, json);
            TriggerBroadcast();
        }

        private void TriggerBroadcast()
        {
            if (_listener == null || _cts == null || _cts.IsCancellationRequested)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _broadcastScheduled, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await BroadcastLoopAsync().ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _broadcastScheduled, 0);
                    if (_pendingBroadcastJson != null && _cts != null && !_cts.IsCancellationRequested)
                    {
                        TriggerBroadcast();
                    }
                }
            });
        }

        private async Task BroadcastLoopAsync()
        {
            CancellationToken token = _cts?.Token ?? CancellationToken.None;
            while (true)
            {
                string json = Interlocked.Exchange(ref _pendingBroadcastJson, null);
                if (json == null)
                {
                    break;
                }

                try
                {
                    await _broadcastLock.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await BroadcastJsonAsync(json, token).ConfigureAwait(false);
                }
                finally
                {
                    _broadcastLock.Release();
                }
            }
        }

        private async Task BroadcastJsonAsync(string json, CancellationToken token)
        {
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            byte[] payload = Encoding.UTF8.GetBytes(json);
            foreach (var client in _webSocketClients.Values.ToArray())
            {
                try
                {
                    await SendFrameWithLockAsync(client, 0x1, payload, token).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    continue;
                }

                if (client != null)
                {
                    _ = Task.Run(() => HandleClientAsync(client, token), token);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            NetworkStream stream = null;
            bool upgraded = false;

            try
            {
                client.NoDelay = true;
                stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                string requestLine = await ReadLineAsync(stream, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine))
                {
                    return;
                }

                Dictionary<string, string> headers = await ReadHeadersAsync(stream, token).ConfigureAwait(false);
                string path = ParseRequestPath(requestLine);
                if (path == null)
                {
                    await WriteResponseAsync(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad Request"), token).ConfigureAwait(false);
                    return;
                }

                if (IsWebSocketRequest(headers))
                {
                    if (!string.Equals(path, "ws", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("WebSocket endpoint not found"), token).ConfigureAwait(false);
                        return;
                    }

                    if (!headers.TryGetValue("sec-websocket-key", out string websocketKey))
                    {
                        await WriteResponseAsync(stream, "400 Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Missing Sec-WebSocket-Key"), token).ConfigureAwait(false);
                        return;
                    }

                    upgraded = await AcceptWebSocketClientAsync(client, stream, websocketKey, token).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("state", StringComparison.OrdinalIgnoreCase))
                {
                    string json = BuildStateJson(_stateProvider?.Invoke());
                    await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), token).ConfigureAwait(false);
                }
                else if (path.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = path.Substring("images/".Length);
                    await ServeFileAsync(stream, fileName, token).ConfigureAwait(false);
                }
                else if (await TryServeWebAssetAsync(stream, path, token).ConfigureAwait(false))
                {
                    return;
                }
                else if (path.Length == 0)
                {
                    await ServeIndexAsync(stream, token).ConfigureAwait(false);
                }
                else
                {
                    await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), token).ConfigureAwait(false);
                }
            }
            catch
            {
            }
            finally
            {
                if (!upgraded)
                {
                    try
                    {
                        stream?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        client.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task<bool> AcceptWebSocketClientAsync(TcpClient client, NetworkStream stream, string websocketKey, CancellationToken token)
        {
            string acceptKey = ComputeWebSocketAccept(websocketKey);
            string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                              "Upgrade: websocket\r\n" +
                              "Connection: Upgrade\r\n" +
                              $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

            byte[] responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, token).ConfigureAwait(false);

            if (_cts == null || _cts.IsCancellationRequested)
            {
                return false;
            }

            var wsClient = new WebSocketClient(client, stream, _cts);
            if (!_webSocketClients.TryAdd(wsClient.Id, wsClient))
            {
                wsClient.Dispose();
                return false;
            }

            _ = Task.Run(() => RunReceiveLoopAsync(wsClient), wsClient.Lifetime.Token);

            var snapshot = _stateProvider?.Invoke();
            if (snapshot != null)
            {
                string json = BuildStateJson(snapshot);
                _ = SendJsonDirectAsync(wsClient, json);
            }

            return true;
        }

        private async Task RunReceiveLoopAsync(WebSocketClient client)
        {
            try
            {
                while (!client.Lifetime.IsCancellationRequested)
                {
                    WebSocketFrame? frame = await ReadFrameAsync(client.Stream, client.Lifetime.Token).ConfigureAwait(false);
                    if (frame == null)
                    {
                        break;
                    }

                    switch (frame.Value.Opcode)
                    {
                        case WebSocketOpcode.Close:
                            await SendCloseFrameAsync(client, frame.Value.Payload, client.Lifetime.Token).ConfigureAwait(false);
                            return;
                        case WebSocketOpcode.Ping:
                            await SendPongAsync(client, frame.Value.Payload, client.Lifetime.Token).ConfigureAwait(false);
                            break;
                        case WebSocketOpcode.Pong:
                        case WebSocketOpcode.Text:
                        case WebSocketOpcode.Binary:
                        case WebSocketOpcode.Continuation:
                        default:
                            break;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                RemoveClient(client.Id);
            }
        }

        private async Task SendJsonDirectAsync(WebSocketClient client, string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            byte[] payload = Encoding.UTF8.GetBytes(json);
            try
            {
                await SendFrameWithLockAsync(client, 0x1, payload, client.Lifetime.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task SendCloseFrameAsync(WebSocketClient client, byte[] payload, CancellationToken token)
        {
            byte[] closePayload = payload ?? new byte[0];
            try
            {
                await SendFrameWithLockAsync(client, 0x8, closePayload, token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task SendPongAsync(WebSocketClient client, byte[] payload, CancellationToken token)
        {
            byte[] pongPayload = payload ?? new byte[0];
            try
            {
                await SendFrameWithLockAsync(client, 0xA, pongPayload, token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task SendFrameWithLockAsync(WebSocketClient client, byte opcode, byte[] payload, CancellationToken token)
        {
            bool lockTaken = false;
            try
            {
                await client.SendLock.WaitAsync(token).ConfigureAwait(false);
                lockTaken = true;
                await WriteWebSocketFrameAsync(client.Stream, opcode, payload, token).ConfigureAwait(false);
            }
            catch
            {
                RemoveClient(client.Id);
                throw;
            }
            finally
            {
                if (lockTaken)
                {
                    try
                    {
                        client.SendLock.Release();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void RemoveClient(Guid clientId)
        {
            if (_webSocketClients.TryRemove(clientId, out var client))
            {
                try
                {
                    client.Dispose();
                }
                catch
                {
                }
            }
        }

        private async Task<WebSocketFrame?> ReadFrameAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] header = new byte[2];
            int read = await ReadExactAsync(stream, header, 0, 2, token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            bool isFinal = (header[0] & 0x80) != 0;
            byte opcode = (byte)(header[0] & 0x0F);
            bool mask = (header[1] & 0x80) != 0;
            ulong payloadLength = (ulong)(header[1] & 0x7F);

            if (payloadLength == 126)
            {
                byte[] extended = new byte[2];
                await ReadExactAsync(stream, extended, 0, 2, token).ConfigureAwait(false);
                payloadLength = (ulong)ReadUInt16(extended);
            }
            else if (payloadLength == 127)
            {
                byte[] extended = new byte[8];
                await ReadExactAsync(stream, extended, 0, 8, token).ConfigureAwait(false);
                payloadLength = ReadUInt64(extended);
            }

            if (payloadLength > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("WebSocket payload too large");
            }

            if (!mask)
            {
                throw new InvalidDataException("Client frames must be masked");
            }

            byte[] maskingKey = new byte[4];
            await ReadExactAsync(stream, maskingKey, 0, 4, token).ConfigureAwait(false);

            byte[] payload = new byte[payloadLength];
            if (payloadLength > 0)
            {
                await ReadExactAsync(stream, payload, 0, (int)payloadLength, token).ConfigureAwait(false);
                for (ulong i = 0; i < payloadLength; i++)
                {
                    payload[i] ^= maskingKey[i % 4];
                }
            }

            return new WebSocketFrame
            {
                Fin = isFinal,
                Opcode = (WebSocketOpcode)opcode,
                Payload = payload
            };
        }

        private static ushort ReadUInt16(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 2)
            {
                return 0;
            }

            return (ushort)((buffer[0] << 8) | buffer[1]);
        }

        private static ulong ReadUInt64(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 8)
            {
                return 0UL;
            }

            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | buffer[i];
            }
            return value;
        }

        private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer, offset + total, count - total, token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (total == 0)
                    {
                        return 0;
                    }
                    throw new EndOfStreamException();
                }
                total += read;
            }
            return total;
        }

        private static async Task WriteWebSocketFrameAsync(NetworkStream stream, byte opcode, byte[] payload, CancellationToken token)
        {
            int payloadLength = payload?.Length ?? 0;
            using (var memory = new MemoryStream(2 + payloadLength + 8))
            {
                memory.WriteByte((byte)(0x80 | (opcode & 0x0F)));

                if (payloadLength <= 125)
                {
                    memory.WriteByte((byte)payloadLength);
                }
                else if (payloadLength <= ushort.MaxValue)
                {
                    memory.WriteByte(126);
                    memory.WriteByte((byte)((payloadLength >> 8) & 0xFF));
                    memory.WriteByte((byte)(payloadLength & 0xFF));
                }
                else
                {
                    memory.WriteByte(127);
                    for (int i = 7; i >= 0; i--)
                    {
                        memory.WriteByte((byte)((payloadLength >> (i * 8)) & 0xFF));
                    }
                }

                if (payloadLength > 0)
                {
                    memory.Write(payload, 0, payloadLength);
                }

                byte[] buffer = memory.ToArray();
                await stream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            }
        }

        private static string ComputeWebSocketAccept(string key)
        {
            string concat = key.Trim() + WebSocketGuid;
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(concat));
                return Convert.ToBase64String(hash);
            }
        }

        private static bool IsWebSocketRequest(Dictionary<string, string> headers)
        {
            if (headers == null)
            {
                return false;
            }

            if (!headers.TryGetValue("upgrade", out string upgrade) || !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!headers.TryGetValue("connection", out string connection) || connection.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            return true;
        }

        private static async Task<Dictionary<string, string>> ReadHeadersAsync(Stream stream, CancellationToken token)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string line = await ReadLineAsync(stream, token).ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }
                if (line.Length == 0)
                {
                    break;
                }

                int separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, separatorIndex).Trim();
                string value = line.Substring(separatorIndex + 1).Trim();
                headers[name.ToLowerInvariant()] = value;
            }

            return headers;
        }

        private async Task ServeIndexAsync(Stream stream, CancellationToken token)
        {
            string indexPath = Path.Combine(_webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Index not found"), token).ConfigureAwait(false);
                return;
            }

            string html;
            DateTime timestamp = File.GetLastWriteTimeUtc(indexPath);
            if (_cachedIndexHtml == null || timestamp != _cachedIndexTimestampUtc)
            {
                html = File.ReadAllText(indexPath, Encoding.UTF8);
                _cachedIndexHtml = html;
                _cachedIndexTimestampUtc = timestamp;
            }
            else
            {
                html = _cachedIndexHtml;
            }

            await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), token).ConfigureAwait(false);
        }

        private async Task ServeFileAsync(Stream stream, string requestedName, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", new byte[0], token).ConfigureAwait(false);
                return;
            }

            string safeName = Path.GetFileName(requestedName);
            if (!safeName.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Forbidden"), token).ConfigureAwait(false);
                return;
            }

            string filePath = ResolveImagePath(safeName);
            byte[] data = null;

            if (filePath != null && File.Exists(filePath))
            {
                try
                {
                    data = File.ReadAllBytes(filePath);
                }
                catch (IOException)
                {
                    await WriteResponseAsync(stream, "503 Service Unavailable", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("File unavailable"), token).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                data = TryLoadResourceBytes(safeName);
                if (data == null)
                {
                    await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), token).ConfigureAwait(false);
                    return;
                }
            }

            string contentType = GetContentType(filePath ?? safeName);
            await WriteResponseAsync(stream, "200 OK", contentType, data, token).ConfigureAwait(false);
        }

        private async Task<bool> TryServeWebAssetAsync(Stream stream, string requestedPath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return false;
            }

            string safePath = requestedPath.Replace('\\', '/');
            if (safePath.StartsWith("WebAssets/", StringComparison.OrdinalIgnoreCase))
            {
                safePath = safePath.Substring("WebAssets/".Length);
            }

            if (safePath.Contains("..") || safePath.Length == 0)
            {
                return false;
            }

            string fullPath = Path.Combine(_webRoot, safePath);
            if (!fullPath.StartsWith(_webRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!File.Exists(fullPath))
            {
                return false;
            }

            byte[] data;
            try
            {
                data = File.ReadAllBytes(fullPath);
            }
            catch (IOException)
            {
                await WriteResponseAsync(stream, "503 Service Unavailable", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("File unavailable"), token).ConfigureAwait(false);
                return true;
            }

            string contentType = GetContentType(fullPath);
            await WriteResponseAsync(stream, "200 OK", contentType, data, token).ConfigureAwait(false);
            return true;
        }

        private string ResolveImagePath(string fileName)
        {
            string direct = Path.Combine(_contentRoot, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            string imagesFolder = Path.Combine(_contentRoot, "Images", fileName);
            if (File.Exists(imagesFolder))
            {
                return imagesFolder;
            }

            return null;
        }

        private byte[] TryLoadResourceBytes(string resourceName)
        {
            try
            {
                Uri resourceUri = new Uri(resourceName, UriKind.Relative);
                StreamResourceInfo info = Application.GetResourceStream(resourceUri);
                if (info != null && info.Stream != null)
                {
                    using (Stream resourceStream = info.Stream)
                    using (var memory = new MemoryStream())
                    {
                        resourceStream.CopyTo(memory);
                        return memory.ToArray();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string GetContentType(string filePath)
        {
            string extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            switch (extension)
            {
                case ".png":
                    return "image/png";
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".gif":
                    return "image/gif";
                case ".svg":
                    return "image/svg+xml";
                case ".json":
                    return "application/json";
                case ".html":
                    return "text/html; charset=utf-8";
                default:
                    return "application/octet-stream";
            }
        }

        private string BuildStateJson(HttpServerState state)
        {
            state = state ?? new HttpServerState();
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"hasData\":").Append(state.HasData ? "true" : "false");
            sb.Append(',');
            sb.Append("\"levelName\":").Append(ToJsonString(state.LevelName));
            sb.Append(',');
            sb.Append("\"playerX\":").Append(state.PlayerX.ToString("F4", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\"playerY\":").Append(state.PlayerY.ToString("F4", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\"angle\":").Append(state.PointerAngle.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\"imagePath\":").Append(ToJsonString(state.ImagePath));
            sb.Append(',');
            sb.Append("\"mapWidth\":").Append(state.MapWidth.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\"mapHeight\":").Append(state.MapHeight.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\"pointerX\":").Append(state.PointerX.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\"pointerY\":").Append(state.PointerY.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\"scale\":").Append(state.Scale.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\"updated\":").Append(ToJsonString(state.LastUpdatedUtc.ToString("o")));
            sb.Append('}');
            return sb.ToString();
        }

        private static string ToJsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 32)
                        {
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
        {
            var builder = new StringBuilder();
            var buffer = new byte[1];
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }

                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, 0, 1, token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return null;
                }

                if (read == 0)
                {
                    return builder.Length == 0 ? null : builder.ToString();
                }

                char ch = (char)buffer[0];
                if (ch == '\n')
                {
                    break;
                }
                if (ch != '\r')
                {
                    builder.Append(ch);
                }
            }
            return builder.ToString();
        }

        private static async Task WriteResponseAsync(Stream stream, string statusCode, string contentType, byte[] payload, CancellationToken token)
        {
            if (payload == null)
            {
                payload = new byte[0];
            }
            string headers = $"HTTP/1.1 {statusCode}\r\n" +
                             $"Content-Type: {contentType}\r\n" +
                             $"Content-Length: {payload.Length}\r\n" +
                             "Connection: close\r\n" +
                             "Access-Control-Allow-Origin: *\r\n\r\n";
            byte[] headerBytes = Encoding.UTF8.GetBytes(headers);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
            }
        }

        private static string ParseRequestPath(string requestLine)
        {
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return null;
            }

            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            string method = parts[0];
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string rawPath = parts[1];
            int queryIndex = rawPath.IndexOf('?');
            if (queryIndex >= 0)
            {
                rawPath = rawPath.Substring(0, queryIndex);
            }
            if (rawPath.StartsWith("/", StringComparison.Ordinal))
            {
                rawPath = rawPath.Substring(1);
            }

            return Uri.UnescapeDataString(rawPath ?? string.Empty);
        }

        private sealed class WebSocketClient : IDisposable
        {
            public Guid Id { get; } = Guid.NewGuid();
            public TcpClient Client { get; }
            public NetworkStream Stream { get; }
            public SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);
            public CancellationTokenSource Lifetime { get; }

            public WebSocketClient(TcpClient client, NetworkStream stream, CancellationTokenSource serverTokenSource)
            {
                Client = client;
                Stream = stream;
                Lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverTokenSource.Token);
            }

            public void Dispose()
            {
                try
                {
                    Lifetime.Cancel();
                }
                catch
                {
                }

                try
                {
                    Stream.Dispose();
                }
                catch
                {
                }

                try
                {
                    Client.Close();
                }
                catch
                {
                }

                Lifetime.Dispose();
            }
        }

        private struct WebSocketFrame
        {
            public bool Fin { get; set; }
            public WebSocketOpcode Opcode { get; set; }
            public byte[] Payload { get; set; }
        }

        private enum WebSocketOpcode : byte
        {
            Continuation = 0x0,
            Text = 0x1,
            Binary = 0x2,
            Close = 0x8,
            Ping = 0x9,
            Pong = 0xA
        }
    }

    internal class HttpServerState
    {
        public string LevelName { get; set; }
        public double PlayerX { get; set; }
        public double PlayerY { get; set; }
        public double PointerAngle { get; set; }
        public string ImagePath { get; set; }
        public double MapWidth { get; set; }
        public double MapHeight { get; set; }
        public double PointerX { get; set; }
        public double PointerY { get; set; }
        public double Scale { get; set; } = 1.0;
        public DateTime LastUpdatedUtc { get; set; } = DateTime.MinValue;

        public bool HasData => !string.IsNullOrEmpty(ImagePath);

        public HttpServerState Clone()
        {
            return new HttpServerState
            {
                LevelName = LevelName,
                PlayerX = PlayerX,
                PlayerY = PlayerY,
                PointerAngle = PointerAngle,
                ImagePath = ImagePath,
                MapWidth = MapWidth,
                MapHeight = MapHeight,
                PointerX = PointerX,
                PointerY = PointerY,
                Scale = Scale,
                LastUpdatedUtc = LastUpdatedUtc
            };
        }
    }
}
