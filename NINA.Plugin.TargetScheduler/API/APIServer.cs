using EmbedIO;
using NINA.Core.Utility.Notification;
using NINA.Plugin.TargetScheduler.Database;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.TargetScheduler.API {

    public class APIServer {
        private const string API_VERSION = "v0";
        private const string BASE_ROUTE = "/ts/" + API_VERSION;

        public WebServer WebServer;

        private Thread serverThread;
        private CancellationTokenSource apiToken;
        private bool started;
        public readonly int Port;
        private readonly ISchedulerDatabaseInteraction database;
        private readonly IProfileService profileService;
        private static bool PrettyPrint;
        private readonly Func<WebServer> webServerFactory;

        public APIServer(int port, bool prettyPrint, IProfileService p, ISchedulerDatabaseInteraction i, Func<WebServer> webServerFactory = null) {
            Port = port;
            PrettyPrint = prettyPrint;
            database = i;
            profileService = p;

            this.webServerFactory = webServerFactory ?? (() =>
                new WebServer(o => o
                    .WithUrlPrefix($"http://*:{Port}")
                    .WithMode(HttpListenerMode.EmbedIO))
                    .WithModule(new PreprocessRequestModule())
                    .WithWebApi(BASE_ROUTE, SerializationCallback, m => m.RegisterController<APIController>(ControllerFactory))
            );
        }

        public static async Task SerializationCallback(IHttpContext context, object data) {
            context.Response.ContentType = "application/json";

            var jsonOptions = new JsonSerializerOptions { WriteIndented = PrettyPrint };
            using var textWriter = context.OpenResponseText(new UTF8Encoding(false));
            await textWriter.WriteAsync(System.Text.Json.JsonSerializer.Serialize(data, jsonOptions)).ConfigureAwait(false);
        }

        public APIController ControllerFactory() {
            return new APIController(database, profileService);
        }

        public void Start() {
            try {
                started = true;
                serverThread = new Thread(() => APITask());
                serverThread.Name = "Target Scheduler API Thread";
                serverThread.SetApartmentState(ApartmentState.STA);
                serverThread.Start();
            } catch (Exception e) {
                started = false;
                TSLogger.Error($"failed to start API server thread: {e}");
            }
        }

        public bool IsRunning => started;

        public void Stop() {
            try {
                started = false;
                if (WebServer == null) return;
                TSLogger.Debug("stopping embedio server");
                apiToken?.Cancel();
                WebServer?.Dispose();
                WebServer = null;
                serverThread = null;
            } catch (Exception e) {
                TSLogger.Error($"failed to stop embedio server: {e}");
            }
        }

        [STAThread]
        private void APITask() {
            const int maxWaitSeconds = 30;
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed.TotalSeconds < maxWaitSeconds) {
                TSLogger.Info($"starting embedio server for TS API on port {Port} ({sw.Elapsed.TotalSeconds:F0}s elapsed)");

                try {
                    WebServer = webServerFactory();
                    apiToken = new CancellationTokenSource();
                    var task = WebServer.RunAsync(apiToken.Token);
                    Thread.Sleep(500);

                    if (task.IsFaulted) {
                        throw task.Exception.InnerException ?? task.Exception;
                    }

                    Notification.ShowInformation($"Target Scheduler API started: http://localhost:{Port}/ts/{API_VERSION}/...");
                    task.Wait();
                    return;
                } catch (Exception e) {
                    if (apiToken != null && apiToken.IsCancellationRequested) {
                        return;
                    }

                    TSLogger.Debug($"API server start failed ({e.Message}), retrying in 2s...");

                    try { WebServer?.Dispose(); } catch { }
                    WebServer = null;
                    Thread.Sleep(2000);
                }
            }

            TSLogger.Error($"failed to start API server on port {Port} after {maxWaitSeconds}s");
            Notification.ShowError($"Failed to start Target Scheduler API server on port {Port}");
        }
    }

    public class PreprocessRequestModule : WebModuleBase {

        public PreprocessRequestModule() : base("/") {
        }

        protected override Task OnRequestAsync(IHttpContext context) {
            TSLogger.Debug($"Request: {context.Request.Url.OriginalString}");
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            return Task.CompletedTask;
        }

        public override bool IsFinalHandler => false;
    }
}