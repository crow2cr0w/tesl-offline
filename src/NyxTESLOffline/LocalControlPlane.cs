using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NyxTESLOffline
{
    /*
     * In-process Sparkle control plane.
     *
     * It preserves the retail ServicesManager state machine while keeping every
     * service endpoint on loopback.  No request is forwarded or proxied.
     *
     * 0.4 adds real local content seeds and persistent deck operations.  The
     * card/campaign data is consumed through the game's own Inventory/Campaign
     * deserializers; this class does not recreate those models.
     */
    internal sealed class LocalControlPlane : IDisposable
    {
        internal const int Port = 3000;
        internal const string ConfigUrl = "http://127.0.0.1:3000/config.json";

        private readonly LocalContentStore _content;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        internal LocalControlPlane(LocalContentStore content)
        {
            if (Object.ReferenceEquals(content, null))
                throw new ArgumentNullException("content");
            _content = content;
        }

        internal bool IsRunning
        {
            get { return _running; }
        }

        internal void Start()
        {
            if (_running)
                return;

            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start(32);
            _running = true;

            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "NyxTESL-LoopbackHTTP";
            _acceptThread.Start();

            Plugin.Log("local control plane listening on 127.0.0.1:" + Port);
        }

        internal void Stop()
        {
            _running = false;

            if (!Object.ReferenceEquals(_listener, null))
            {
                try { _listener.Stop(); }
                catch { }
                _listener = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch (SocketException)
                {
                    if (!_running)
                        return;
                    if (!Object.ReferenceEquals(client, null))
                        client.Close();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Warn("control-plane accept failed: " + ex.Message);
                    if (!Object.ReferenceEquals(client, null))
                        client.Close();
                }
            }
        }

        private void HandleClient(object state)
        {
            TcpClient client = state as TcpClient;
            if (Object.ReferenceEquals(client, null))
                return;

            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 3000;

                    NetworkStream stream = client.GetStream();
                    string request = ReadHeaders(stream);
                    if (String.IsNullOrEmpty(request))
                        return;

                    string method;
                    string path;
                    if (!ParseRequestLine(request, out method, out path))
                    {
                        SendJson(stream, 400, "{\"error\":\"bad request\"}");
                        return;
                    }

                    int contentLength = GetContentLength(request);
                    string body = contentLength > 0 ? ReadBody(stream, contentLength) : String.Empty;

                    string route = path;
                    int query = route.IndexOf('?');
                    if (query >= 0)
                        route = route.Substring(0, query);

                    int status;
                    string response = Route(method, route, body, out status);
                    SendJson(stream, status, response);
                }
                catch (Exception ex)
                {
                    Plugin.Warn("control-plane client error: " + ex.Message);
                }
            }
        }

        private static string ReadHeaders(NetworkStream stream)
        {
            const int Max = 65536;
            MemoryStream buffer = new MemoryStream();
            int matched = 0;

            while (buffer.Length < Max)
            {
                int value = stream.ReadByte();
                if (value < 0)
                    break;

                buffer.WriteByte((byte)value);

                if ((matched == 0 && value == '\r') ||
                    (matched == 1 && value == '\n') ||
                    (matched == 2 && value == '\r') ||
                    (matched == 3 && value == '\n'))
                {
                    matched++;
                    if (matched == 4)
                        break;
                }
                else
                {
                    matched = value == '\r' ? 1 : 0;
                }
            }

            return Encoding.ASCII.GetString(buffer.ToArray());
        }

        private static int GetContentLength(string headers)
        {
            string[] lines = headers.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            int i;
            for (i = 1; i < lines.Length; ++i)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                string name = line.Substring(0, colon).Trim();
                if (!String.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;

                int value;
                if (Int32.TryParse(line.Substring(colon + 1).Trim(), out value) && value >= 0)
                    return value;
                return 0;
            }
            return 0;
        }

        private static string ReadBody(NetworkStream stream, int length)
        {
            if (length <= 0)
                return String.Empty;
            if (length > 4 * 1024 * 1024)
                throw new IOException("HTTP body exceeds offline safety limit");

            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(data, offset, length - offset);
                if (read <= 0)
                    break;
                offset += read;
            }

            return Encoding.UTF8.GetString(data, 0, offset);
        }

        private static bool ParseRequestLine(string request, out string method, out string path)
        {
            method = null;
            path = null;

            int lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd <= 0)
                return false;

            string line = request.Substring(0, lineEnd);
            string[] parts = line.Split(' ');
            if (parts.Length < 2)
                return false;

            method = parts[0];
            path = parts[1];
            return path.StartsWith("/", StringComparison.Ordinal);
        }

        private static void SendJson(NetworkStream stream, int status, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            string reason = status == 200 ? "OK" :
                status == 400 ? "Bad Request" : "Internal Server Error";
            string header =
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n";

            byte[] head = Encoding.ASCII.GetBytes(header);
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private string Route(string method, string path, string body, out int status)
        {
            status = 200;
            Plugin.Log("local HTTP " + method + " " + path);

            if (path == "/config.json")
                return ConfigJson;

            if (path == "/Account/bnetSteamLogin")
                return AuthLoginJson;
            if (path == "/Account/bnetRefreshSession")
                return "{\"timeToRefresh\":3600000,\"sessionToken\":\"offline-session-token\"}";
            if (path == "/Account/bnetGetLegalDocumentsForUser")
                return "{\"items\":[]}";

            if (path == "/Packages/list")
                return "{\"massOpenMinCount\":999,\"packageInfos\":[]}";

            if (path == "/Inventory/list_v2")
                return _content.BuildInventoryResponse();
            if (path == "/Inventory/createDeck")
                return _content.CreateDeck();
            if (path == "/Inventory/saveDeck" || path == "/Inventory/saveDeck_v2")
            {
                if (!_content.SaveDeck(body))
                    Plugin.Warn("rejected malformed local deck save");
                return "{\"success\":true}";
            }
            if (path == "/Inventory/deleteDeck")
            {
                _content.DeleteDeck(body);
                return "{}";
            }
            if (path == "/Inventory/setSeens")
                return "{\"success\":true}";

            if (path == "/DailyRewards/list")
                return "{\"rewards\":[]}";
            if (path == "/Rewards/list")
                return "{\"rewards\":[]}";
            if (path == "/Profile/fetch")
                return ProfileJson;
            if (path == "/Profile/setOnboardingProgress" || path == "/Profile/setSeen")
                return ProfileJson;
            if (path == "/Events/fetch")
                return "{\"eventDescription\":[]}";
            if (path == "/ServerMessages/fetch")
                return "{\"messages\":[]}";
            if (path == "/Arena/fetchActiveArenaInstances")
                return "{\"clientData\":{\"costGold\":0,\"costTickets\":0,\"instances\":[]}}";
            if (path == "/Shop/fetchCatalog")
                return "{\"catalog\":{\"platform\":\"PC\",\"currencyCode\":\"BRL\",\"items\":[],\"activeTargetedSale\":null}}";
            if (path == "/GenericInventory/fetch")
                return _content.BuildGenericInventoryResponse();
            if (path == "/GenericInventory/setSelected")
            {
                if (!_content.SetSelectedCardBack(body))
                {
                    status = 400;
                    return "{\"resultCode\":5000}";
                }
                return "{}";
            }
            if (path == "/Quests/fetch")
                return "{\"quests\":[],\"choice\":null}";

            if (path == "/Campaigns/list")
                return _content.CampaignsResponse;
            if (path == "/Campaigns/checkCampaignProgress")
            {
                Plugin.CheckCampaignProgress(body);
                return "{}";
            }
            if (path == "/Campaigns/debugAddNextChapter" ||
                path == "/Campaigns/debugAddNextEvent")
                return "{}";
            if (path == "/Campaigns/setChapterDialogStatus" ||
                path == "/Campaigns/setChapterEventChoice")
                return "{\"rewards\":[]}";

            if (path == "/Matching/rejoin")
                return "{\"gameReadyPlayerClient\":null}";
            if (path == "/Matching/reserve")
                return "{\"resultCode\":0,\"requestId\":\"offline-match-request\",\"errorType\":0}";
            if (path == "/Matching/add")
                return "{\"resultCode\":0,\"requestId\":\"offline-match-request\",\"poolName\":\"offline\",\"estimatedWaitSeconds\":0,\"refreshPeriodSeconds\":3}";
            if (path == "/Matching/refresh")
                return "{\"resultCode\":0,\"estimatedWaitSeconds\":0,\"gameReadyPlayerClient\":null}";
            if (path == "/Matching/cancel")
                return "{}";

            if (path == "/PubsubAuth/")
                return "{\"authToken\":\"offline\",\"chatRooms\":{\"global\":[]}}";

            if (path == "/Analytics/reportingConfig" ||
                path == "/ErrorReporting/reportingConfig")
                return "{\"enableReporting\":false}";

            if (path.StartsWith("/Beamsocial/", StringComparison.Ordinal))
                return "{\"items\":[]}";

            return "{}";
        }

        private const string ConfigJson =
            "{" +
            "\"AuthDev\":\"http://127.0.0.1:3000/AuthDev\"," +
            "\"Account\":\"http://127.0.0.1:3000/Account\"," +
            "\"PubsubAuth\":\"http://127.0.0.1:3000/PubsubAuth\"," +
            "\"PubsubConnections\":\"http://127.0.0.1:3000/PubsubConnections\"," +
            "\"Shop\":\"http://127.0.0.1:3000/Shop\"," +
            "\"Packages\":\"http://127.0.0.1:3000/Packages\"," +
            "\"Inventory\":\"http://127.0.0.1:3000/Inventory\"," +
            "\"Matching\":\"http://127.0.0.1:3000/Matching\"," +
            "\"Campaigns\":\"http://127.0.0.1:3000/Campaigns\"," +
            "\"Rewards\":\"http://127.0.0.1:3000/Rewards\"," +
            "\"Profile\":\"http://127.0.0.1:3000/Profile\"," +
            "\"Stats\":\"http://127.0.0.1:3000/Stats\"," +
            "\"Quests\":\"http://127.0.0.1:3000/Quests\"," +
            "\"Crafting\":\"http://127.0.0.1:3000/Crafting\"," +
            "\"DailyRewards\":\"http://127.0.0.1:3000/DailyRewards\"," +
            "\"Events\":\"http://127.0.0.1:3000/Events\"," +
            "\"Arena\":\"http://127.0.0.1:3000/Arena\"," +
            "\"GenericInventory\":\"http://127.0.0.1:3000/GenericInventory\"," +
            "\"ServerMessages\":\"http://127.0.0.1:3000/ServerMessages\"," +
            "\"Beam\":\"http://127.0.0.1:3000/Beam\"," +
            "\"ErrorReporting\":\"http://127.0.0.1:3000/ErrorReporting\"," +
            "\"Analytics\":\"http://127.0.0.1:3000/Analytics\"," +
            "\"MaintenanceMode\":\"Inactive\"" +
            "}";

        private const string AuthLoginJson =
            "{" +
            "\"data\":{" +
            "\"token\":\"offline-token\"," +
            "\"channel\":\"prod-live\"," +
            "\"sessionId\":\"offline-session\"," +
            "\"sessionType\":\"offline\"," +
            "\"sessionToken\":\"offline-session-token\"," +
            "\"buid\":\"offline-local-user\"," +
            "\"steamId\":\"0\"," +
            "\"kpiId\":\"offline\"," +
            "\"deviceGrade\":\"PC\"," +
            "\"nickname\":\"Offline\"," +
            "\"bnetCountry\":\"BR\"," +
            "\"isLimitedAccount\":false," +
            "\"isAnonymousAccount\":false," +
            "\"timeToRefresh\":3600000," +
            "\"maxRefreshFailures\":3," +
            "\"secondsBetweenRefreshFailures\":10.0," +
            "\"allowTP\":false," +
            "\"unityBundleUrl\":\"\"," +
            "\"unityBundleHash\":\"\"," +
            "\"buildSetId\":0," +
            "\"abTestingSets\":[]," +
            "\"wasNativeSignOn\":false" +
            "}," +
            "\"errorCode\":0," +
            "\"errorMessage\":\"\"," +
            "\"validChangelists\":[\"317259\"]" +
            "}";

        private const string ProfileJson =
            "{\"profile\":{" +
            "\"nickname\":\"Offline\"," +
            "\"level\":50," +
            "\"gold\":0," +
            "\"gems\":0," +
            "\"xp\":0," +
            "\"tickets\":0," +
            "\"onboardingProgressFlags\":-1," +
            "\"dailyGold\":0," +
            "\"dailyGems\":0," +
            "\"ratings\":[]," +
            "\"maxRatings\":[]," +
            "\"collectionStats\":[]," +
            "\"seasonRollovers\":[]" +
            "}}";
    }
}
