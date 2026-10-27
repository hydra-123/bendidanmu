using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class DesktopLauncher
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    private static readonly string Api = "https://api.dandanplay.net/api/v2";
    private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalVideoPlayer");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "dandanplay-auth.json");
    private static readonly string HistoryPath = Path.Combine(ConfigDir, "video-history.json");
    private static readonly string PortableHistoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video-history.json");
    private static readonly string PortPath = Path.Combine(ConfigDir, "webview-port.txt");
    private static readonly string PortablePortPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview-port.txt");
    private static readonly string WebViewProfilePath = Path.Combine(ConfigDir, "webview-profile.txt");
    private static readonly string PortableWebViewProfilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview-profile.txt");
    private static TcpListener listener;
    private static string root;
    private static bool isFullscreen;
    private static FormWindowState previousWindowState;
    private static FormBorderStyle previousBorderStyle;
    private static System.Drawing.Rectangle previousBounds;
    private static MpvPlayerEngine playerEngine;
    private static DanmakuOverlayForm danmakuOverlay;
    private static PlayerControlsOverlayForm playerControls;
    private static VideoInputFilter videoInputFilter;
    private static Panel videoSurface;
    private static WebView2 webView;
    private static Form mainForm;
    private static string activeSubtitlePath;

    private static void PostToPage(string functionName, object value)
    {
        if (webView == null || webView.CoreWebView2 == null) return;
        webView.CoreWebView2.ExecuteScriptAsync("window." + functionName + "(" + Json.Serialize(value) + ");");
    }

    private static void ReportPlayerState(double time, double duration, bool paused, bool ended, double volume)
    {
        if (danmakuOverlay != null) danmakuOverlay.SetPlaybackState(time, paused);
        if (playerControls != null) playerControls.SetState(time, duration, paused, volume);
        PostToPage("onNativePlayerState", new Dictionary<string, object> {
            { "currentTime", time }, { "duration", duration }, { "paused", paused }, { "ended", ended }, { "volume", volume }
        });
    }

    private static void SyncOverlayToSurface()
    {
        if (danmakuOverlay != null && videoSurface != null)
        {
            Rectangle bounds = videoSurface.RectangleToScreen(videoSurface.ClientRectangle);
            danmakuOverlay.SetViewport(bounds, videoSurface.Visible);
            if (playerControls != null) playerControls.Place(bounds);
        }
    }

    private sealed class VideoInputFilter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message message)
        {
            if (videoSurface == null || playerEngine == null || !videoSurface.Visible || videoSurface.Width <= 0 || videoSurface.Height <= 0) return false;
            Point point = Cursor.Position;
            Rectangle video = videoSurface.RectangleToScreen(videoSurface.ClientRectangle);
            if (!video.Contains(point)) return false;
            if (message.Msg == 0x0200)
            {
                if (playerControls != null) playerControls.Reveal();
                return false;
            }
            bool overControls = playerControls != null && playerControls.Visible && playerControls.Bounds.Contains(point);
            if (message.Msg == 0x0201 && !overControls)
            {
                playerEngine.TogglePause();
                return true;
            }
            if (message.Msg == 0x020A && !overControls)
            {
                int delta = unchecked((short)((long)message.WParam >> 16));
                playerEngine.SetVolume(Math.Max(0, Math.Min(100, playerEngine.Volume + Math.Sign(delta) * 5)));
                if (playerControls != null) playerControls.Reveal();
                return true;
            }
            return false;
        }
    }

    private static void SelectVideos(bool multiple)
    {
        using (OpenFileDialog dialog = new OpenFileDialog())
        {
            dialog.Title = multiple ? "批量导入视频" : "打开视频";
            dialog.Filter = "视频文件|*.mp4;*.mkv;*.m4v;*.mov;*.avi;*.wmv;*.flv;*.webm;*.ts;*.m2ts;*.mpg;*.mpeg;*.ogv|所有文件|*.*";
            dialog.Multiselect = multiple;
            if (dialog.ShowDialog(mainForm) != DialogResult.OK) return;
            List<Dictionary<string, object>> files = new List<Dictionary<string, object>>();
            foreach (string path in dialog.FileNames)
            {
                try
                {
                    FileInfo info = new FileInfo(path);
                    double modified = Math.Floor((info.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                    files.Add(new Dictionary<string, object> { { "path", info.FullName }, { "name", info.Name }, { "size", info.Length }, { "lastModified", modified } });
                }
                catch { }
            }
            PostToPage("onNativeFilesSelected", files);
        }
    }

    private static void SelectSubtitle()
    {
        using (OpenFileDialog dialog = new OpenFileDialog())
        {
            dialog.Title = "载入字幕";
            dialog.Filter = "字幕文件|*.ass;*.ssa;*.srt;*.vtt;*.sub;*.idx|所有文件|*.*";
            if (dialog.ShowDialog(mainForm) == DialogResult.OK)
                PostToPage("onNativeSubtitleSelected", new Dictionary<string, object> { { "path", dialog.FileName }, { "name", Path.GetFileName(dialog.FileName) } });
        }
    }

    private static void ToggleFullscreen()
    {
        if (!isFullscreen)
        {
            previousWindowState = mainForm.WindowState;
            previousBorderStyle = mainForm.FormBorderStyle;
            previousBounds = mainForm.Bounds;
            mainForm.WindowState = FormWindowState.Normal;
            mainForm.FormBorderStyle = FormBorderStyle.None;
            mainForm.Bounds = Screen.FromControl(mainForm).Bounds;
            mainForm.TopMost = true;
            isFullscreen = true;
        }
        else
        {
            mainForm.TopMost = false;
            mainForm.FormBorderStyle = previousBorderStyle;
            mainForm.Bounds = previousBounds;
            mainForm.WindowState = previousWindowState;
            isFullscreen = false;
        }
        if (webView != null && webView.CoreWebView2 != null)
            webView.CoreWebView2.ExecuteScriptAsync("document.body.classList.toggle('native-fullscreen', " + (isFullscreen ? "true" : "false") + ");window.dispatchEvent(new Event('resize'));");
    }

    private static WebView2 CreateWebView(Form form, string profile, int port)
    {
        Directory.CreateDirectory(profile);
        WebView2 view = new WebView2 { Dock = DockStyle.Fill };
        view.ZoomFactor = 1.15;
        view.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = profile };
        view.CoreWebView2InitializationCompleted += delegate(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess) return;
            view.CoreWebView2.WebMessageReceived += delegate(object messageSender, CoreWebView2WebMessageReceivedEventArgs messageArgs)
            {
                HandleWebMessage(messageArgs.WebMessageAsJson);
            };
            view.CoreWebView2.Navigate("http://127.0.0.1:" + port + "/");
        };
        form.Controls.Add(view);
        return view;
    }

    private static void HandleWebMessage(string json)
    {
        Dictionary<string, object> message;
        try { message = Json.Deserialize<Dictionary<string, object>>(json); } catch { return; }
        if (message == null || !message.ContainsKey("type")) return;
        string type = Convert.ToString(message["type"]);
        object value;
        if (type == "fullscreen-toggle") { ToggleFullscreen(); return; }
        if (type == "select-videos") { SelectVideos(message.ContainsKey("multiple") && Convert.ToBoolean(message["multiple"])); return; }
        if (type == "select-subtitle") { SelectSubtitle(); return; }
        if (playerEngine == null && (type.StartsWith("player-") || type.StartsWith("subtitle-"))) return;
        if (type == "player-load")
        {
            string path = message.ContainsKey("path") ? Convert.ToString(message["path"]) : "";
            double resume = message.ContainsKey("resumeAt") ? Convert.ToDouble(message["resumeAt"], System.Globalization.CultureInfo.InvariantCulture) : 0;
            try { if (playerEngine == null) throw new InvalidOperationException("原生播放引擎尚未初始化"); videoSurface.Visible = true; playerEngine.Load(path, resume); if (!String.IsNullOrEmpty(activeSubtitlePath)) playerEngine.AddSubtitle(activeSubtitlePath); SyncOverlayToSurface(); if (playerControls != null) playerControls.Reveal(); }
            catch (Exception ex) { videoSurface.Visible = false; SyncOverlayToSurface(); PostToPage("onNativePlayerError", ex.Message); }
            return;
        }
        if (type == "player-play") { playerEngine.Play(); return; }
        if (type == "player-pause") { playerEngine.Pause(); return; }
        if (type == "player-seek" && message.TryGetValue("seconds", out value)) { playerEngine.Seek(Convert.ToDouble(value)); return; }
        if (type == "player-volume" && message.TryGetValue("value", out value)) { playerEngine.SetVolume(Convert.ToDouble(value)); return; }
        if (type == "player-stop") { playerEngine.Stop(); videoSurface.Visible = false; if (danmakuOverlay != null) danmakuOverlay.SetViewport(Rectangle.Empty, false); if (playerControls != null) playerControls.Hide(); return; }
        if (type == "subtitle-load" && message.TryGetValue("path", out value)) { activeSubtitlePath = Convert.ToString(value); if (String.IsNullOrEmpty(activeSubtitlePath)) playerEngine.ClearSubtitle(); else playerEngine.AddSubtitle(activeSubtitlePath); return; }
        if (type == "subtitle-content")
        {
            try
            {
                string name = message.ContainsKey("name") ? Path.GetFileName(Convert.ToString(message["name"])) : "subtitle.ass";
                string extension = Path.GetExtension(name).ToLowerInvariant();
                if (extension != ".ass" && extension != ".ssa" && extension != ".srt" && extension != ".vtt" && extension != ".sub" && extension != ".idx") extension = ".ass";
                string folder = Path.Combine(root, "Subtitles");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
                string encoded = message.ContainsKey("contentBase64") ? Convert.ToString(message["contentBase64"]) : "";
                if (encoded.Length > 12 * 1024 * 1024) throw new InvalidOperationException("字幕文件超过 8 MB");
                File.WriteAllBytes(path, Convert.FromBase64String(encoded));
                activeSubtitlePath = path;
                playerEngine.AddSubtitle(path);
                PostToPage("onNativeSubtitleLoaded", new Dictionary<string, object> { { "name", name } });
            }
            catch (Exception ex) { PostToPage("onNativePlayerError", ex.Message); }
            return;
        }
        if (type == "subtitle-visible" && message.TryGetValue("visible", out value)) { playerEngine.SetSubtitleVisible(Convert.ToBoolean(value)); return; }
        if (type == "subtitle-delay" && message.TryGetValue("seconds", out value)) { playerEngine.SetSubtitleDelay(Convert.ToDouble(value)); return; }
        if (type == "danmaku-items")
        {
            try { danmakuOverlay.SetItems(Json.Deserialize<DanmakuItem[]>(Json.Serialize(message.ContainsKey("items") ? message["items"] : null))); } catch { }
            return;
        }
        if (type == "danmaku-settings")
        {
            try { danmakuOverlay.SetSettings(Json.Deserialize<DanmakuSettings>(Json.Serialize(message.ContainsKey("settings") ? message["settings"] : null))); } catch { }
            return;
        }
        if (type == "video-layout")
        {
            try
            {
                Dictionary<string, object> rect = (Dictionary<string, object>)message["rect"];
                double factor = Convert.ToDouble(rect["scale"], System.Globalization.CultureInfo.InvariantCulture);
                int x = (int)Math.Round(Convert.ToDouble(rect["x"]) * factor), y = (int)Math.Round(Convert.ToDouble(rect["y"]) * factor);
                int width = (int)Math.Round(Convert.ToDouble(rect["width"]) * factor), height = (int)Math.Round(Convert.ToDouble(rect["height"]) * factor);
                Point origin = webView.PointToScreen(Point.Empty);
                Rectangle bounds = new Rectangle(origin.X + x, origin.Y + y, Math.Max(0, width), Math.Max(0, height));
                videoSurface.Bounds = mainForm.RectangleToClient(bounds);
                if (danmakuOverlay != null) danmakuOverlay.SetViewport(bounds, videoSurface.Visible && width > 0 && height > 0);
            }
            catch { }
        }
    }

    private static Dictionary<string, object> ReadConfig()
    {
        try { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath, Encoding.UTF8)); }
        catch { return null; }
    }

    private static string ReadHistory()
    {
        try { if (File.Exists(HistoryPath)) return File.ReadAllText(HistoryPath, Encoding.UTF8); } catch { }
        try { if (File.Exists(PortableHistoryPath)) return File.ReadAllText(PortableHistoryPath, Encoding.UTF8); } catch { }
        return "[]";
    }

    private static void WriteHistory(string value)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(HistoryPath, value, new UTF8Encoding(false));
            return;
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        File.WriteAllText(PortableHistoryPath, value, new UTF8Encoding(false));
    }

    private static int ReadSavedPort()
    {
        string[] paths = { PortPath, PortablePortPath };
        foreach (string path in paths)
        {
            try
            {
                int port;
                if (Int32.TryParse(File.ReadAllText(path).Trim(), out port) && port > 1024 && port < 65536) return port;
            }
            catch { }
        }
        return 0;
    }

    private static void SavePort(int port)
    {
        string value = port.ToString();
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(PortPath, value, Encoding.ASCII);
            return;
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        File.WriteAllText(PortablePortPath, value, Encoding.ASCII);
    }

    private static string ApiError(string message) { return Json.Serialize(new Dictionary<string, object> { { "error", message } }); }

    private static void Send(NetworkStream stream, int status, string contentType, byte[] body)
    {
        string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" : status == 404 ? "Not Found" : status == 413 ? "Payload Too Large" : "Bad Gateway";
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(body, 0, body.Length);
    }

    private static void SendJson(NetworkStream stream, int status, string json) { Send(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json)); }

    private static string QueryValue(string target, string name)
    {
        Uri uri = new Uri("http://localhost" + target);
        return HttpUtility.ParseQueryString(uri.Query)[name];
    }

    private static Dictionary<string, string> AuthHeaders()
    {
        Dictionary<string, object> config = ReadConfig();
        if (config == null || !config.ContainsKey("appId") || !config.ContainsKey("appSecret")) throw new InvalidOperationException("尚未配置 DanDanPlay AppId / AppSecret");
        string appId = Convert.ToString(config["appId"]);
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string secret = Convert.ToString(config["appSecret"]);
        string signature;
        using (MD5 md5 = MD5.Create())
            signature = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(appId + timestamp + secret))).Replace("-", "").ToLowerInvariant();
        return new Dictionary<string, string> {
            { "Accept", "application/json" }, { "User-Agent", "Kazumi/2.3.7" },
            { "X-AppId", appId }, { "X-Timestamp", timestamp }, { "X-Signature", signature }
        };
    }

    private static void Proxy(NetworkStream stream, string url)
    {
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 20000;
            foreach (KeyValuePair<string, string> header in AuthHeaders())
            {
                if (header.Key == "Accept") request.Accept = header.Value;
                else if (header.Key == "User-Agent") request.UserAgent = header.Value;
                else request.Headers[header.Key] = header.Value;
            }
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (MemoryStream ms = new MemoryStream())
            {
                response.GetResponseStream().CopyTo(ms);
                Send(stream, (int)response.StatusCode, "application/json; charset=utf-8", ms.ToArray());
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            int status = response == null ? 502 : (int)response.StatusCode;
            string message = response == null ? "无法连接 DanDanPlay: " + ex.Message : response.Headers["X-Error-Message"];
            if (String.IsNullOrEmpty(message)) message = response == null ? ex.Message : "DanDanPlay 返回 HTTP " + status;
            SendJson(stream, status == 401 || status == 403 ? status : 502, ApiError(message));
        }
        catch (InvalidOperationException ex) { SendJson(stream, 401, ApiError(ex.Message)); }
        catch (Exception ex) { SendJson(stream, 502, ApiError("DanDanPlay 请求失败: " + ex.Message)); }
    }

    private static void HandleClient(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            try
            {
                List<byte> headerBytes = new List<byte>();
                int matched = 0, b;
                byte[][] delimiter = { new byte[] { 13, 10, 13, 10 } };
                while (headerBytes.Count < 16384 && (b = stream.ReadByte()) >= 0)
                {
                    headerBytes.Add((byte)b);
                    matched = (byte)b == delimiter[0][matched] ? matched + 1 : ((byte)b == 13 ? 1 : 0);
                    if (matched == 4) break;
                }
                string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2) { SendJson(stream, 400, ApiError("无效请求")); return; }
                string method = requestLine[0];
                string target = requestLine[1];
                int contentLength = 0;
                foreach (string line in lines)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) Int32.TryParse(line.Substring(15).Trim(), out contentLength);
                }
                if (contentLength < 0 || contentLength > 262144) { SendJson(stream, 413, ApiError("请求过大")); return; }
                byte[] body = new byte[contentLength];
                int read = 0;
                while (read < contentLength) { int n = stream.Read(body, read, contentLength - read); if (n <= 0) break; read += n; }

                Uri uri = new Uri("http://localhost" + target);
                string path = uri.AbsolutePath;
                if (method == "GET" && path == "/api/history")
                {
                    SendJson(stream, 200, ReadHistory());
                }
                else if (method == "POST" && path == "/api/history")
                {
                    object data = Json.DeserializeObject(Encoding.UTF8.GetString(body));
                    if (!(data is System.Collections.ArrayList) && !(data is object[])) { SendJson(stream, 400, ApiError("历史记录格式无效")); return; }
                    WriteHistory(Json.Serialize(data));
                    SendJson(stream, 200, "{\"saved\":true}");
                }
                else if (method == "GET" && path == "/")
                {
                    Stream page = typeof(DesktopLauncher).Assembly.GetManifestResourceStream("LocalVideoPlayer.html");
                    if (page == null)
                    {
                        string externalPage = Path.Combine(root, "local-video-player.html");
                        if (File.Exists(externalPage)) page = File.OpenRead(externalPage);
                    }
                    if (page == null) throw new FileNotFoundException("播放器界面未嵌入应用，且应用目录中缺少 local-video-player.html");
                    using (page)
                    using (MemoryStream ms = new MemoryStream())
                    {
                        page.CopyTo(ms);
                        Send(stream, 200, "text/html; charset=utf-8", ms.ToArray());
                    }
                }
                else if (method == "GET" && path == "/api/config")
                {
                    Dictionary<string, object> config = ReadConfig();
                    SendJson(stream, 200, Json.Serialize(new Dictionary<string, object> {
                        { "configured", config != null }, { "appId", config != null && config.ContainsKey("appId") ? config["appId"] : "" }
                    }));
                }
                else if (method == "POST" && path == "/api/config")
                {
                    Dictionary<string, object> data = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                    string appId = data == null || !data.ContainsKey("appId") ? "" : Convert.ToString(data["appId"]).Trim();
                    string secret = data == null || !data.ContainsKey("appSecret") ? "" : Convert.ToString(data["appSecret"]).Trim();
                    if (appId.Length == 0 || appId.Length > 64 || secret.Length < 8 || secret.Length > 256)
                    { SendJson(stream, 400, ApiError("AppId 或 AppSecret 格式无效")); return; }
                    Directory.CreateDirectory(ConfigDir);
                    File.WriteAllText(ConfigPath, Json.Serialize(new Dictionary<string, object> { { "appId", appId }, { "appSecret", secret } }), new UTF8Encoding(false));
                    SendJson(stream, 200, "{\"saved\":true}");
                }
                else if (method == "GET" && path == "/api/search")
                {
                    string anime = QueryValue(target, "anime");
                    if (String.IsNullOrWhiteSpace(anime)) { SendJson(stream, 400, ApiError("缺少番名")); return; }
                    Proxy(stream, Api + "/search/episodes?" + HttpUtility.UrlEncode("anime") + "=" + Uri.EscapeDataString(anime));
                }
                else if (method == "GET" && path == "/api/comments")
                {
                    string id = QueryValue(target, "episodeId");
                    if (String.IsNullOrEmpty(id) || id.Length > 80 || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]+$"))
                    { SendJson(stream, 400, ApiError("集数 ID 格式无效")); return; }
                    Proxy(stream, Api + "/comment/" + id + "?withRelated=true&chConvert=0");
                }
                else SendJson(stream, 404, ApiError("Not found"));
            }
            catch (Exception ex)
            {
                try { SendJson(stream, 500, ApiError(ex.Message)); } catch { }
            }
        }
    }

    private static void ServerLoop()
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
        }
    }

    [STAThread]
    private static void Main()
    {
        root = AppDomain.CurrentDomain.BaseDirectory;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            int savedPort = ReadSavedPort();
            listener = new TcpListener(IPAddress.Loopback, savedPort);
            try { listener.Start(); }
            catch (SocketException)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
            }
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port != savedPort) SavePort(port);
            Thread serverThread = new Thread(ServerLoop) { IsBackground = true };
            serverThread.Start();
            Directory.CreateDirectory(ConfigDir);
            string defaultProfile = Path.Combine(ConfigDir, "WebView2");
            string profile = defaultProfile;
            string[] profileMarkers = { WebViewProfilePath, PortableWebViewProfilePath };
            foreach (string marker in profileMarkers)
            {
                try { string savedProfile = File.ReadAllText(marker, Encoding.UTF8).Trim(); if (Path.IsPathRooted(savedProfile) && Directory.Exists(savedProfile)) { profile = savedProfile; break; } } catch { }
            }
            Form form = new Form { Text = "本地影像播放器", StartPosition = FormStartPosition.CenterScreen, Width = 1280, Height = 800, MinimumSize = new System.Drawing.Size(820, 560), KeyPreview = true };
            mainForm = form;
            danmakuOverlay = new DanmakuOverlayForm(form);
            videoSurface = new Panel { BackColor = Color.Black, Visible = false };
            WebView2 view = CreateWebView(form, profile, port);
            webView = view;
            form.Controls.Add(videoSurface);
            videoSurface.BringToFront();
            playerControls = new PlayerControlsOverlayForm(form,
                delegate { if (playerEngine != null) playerEngine.TogglePause(); },
                delegate(double seconds) { if (playerEngine != null) playerEngine.Seek(seconds); },
                delegate(double volume) { if (playerEngine != null) playerEngine.SetVolume(volume); },
                ToggleFullscreen);
            videoInputFilter = new VideoInputFilter();
            Application.AddMessageFilter(videoInputFilter);
            form.LocationChanged += delegate { SyncOverlayToSurface(); };
            form.SizeChanged += delegate { SyncOverlayToSurface(); };
            form.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape && isFullscreen) { ToggleFullscreen(); e.Handled = true; } };
            form.Shown += async delegate(object sender, EventArgs e)
            {
                try
                {
                    Exception initError = null;
                    try { await view.EnsureCoreWebView2Async(); } catch (Exception ex) { initError = ex; }
                    if (initError != null)
                    {
                        if (initError.HResult != unchecked((int)0x800700AA))
                        {
                            MessageBox.Show(form, "无法启动播放器页面。请确认已安装 Microsoft Edge WebView2 Runtime。\n\n" + initError.Message, "本地影像播放器");
                            return;
                        }
                        form.Controls.Remove(view);
                        view.Dispose();
                        profile = Path.Combine(root, "WebView2-mpv");
                        view = CreateWebView(form, profile, port);
                        webView = view;
                        videoSurface.BringToFront();
                        try { await view.EnsureCoreWebView2Async(); }
                        catch (Exception fallbackError)
                        {
                            MessageBox.Show(form, "WebView2 配置目录仍被占用，无法启动播放器页面。\n\n" + fallbackError.Message, "本地影像播放器");
                            return;
                        }
                        try { File.WriteAllText(WebViewProfilePath, profile, Encoding.UTF8); }
                        catch { File.WriteAllText(PortableWebViewProfilePath, profile, Encoding.UTF8); }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(form, "无法启动播放器页面。请确认已安装 Microsoft Edge WebView2 Runtime。\n\n" + ex.Message, "本地影像播放器");
                    return;
                }
                SetDllDirectory(root);
                playerEngine = new MpvPlayerEngine(videoSurface, ReportPlayerState);
                string engineError = playerEngine.Initialize();
                if (!String.IsNullOrEmpty(engineError))
                {
                    MessageBox.Show(form, "原生视频引擎启动失败：\n" + engineError, "本地影像播放器");
                    PostToPage("onNativePlayerError", engineError);
                }
            };
            form.FormClosed += delegate { if (videoInputFilter != null) Application.RemoveMessageFilter(videoInputFilter); if (playerControls != null) playerControls.Close(); if (playerEngine != null) playerEngine.Dispose(); if (danmakuOverlay != null) danmakuOverlay.Close(); };
            Application.Run(form);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "本地影像播放器"); }
        finally { if (listener != null) listener.Stop(); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
