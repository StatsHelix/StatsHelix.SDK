using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace StatsHelix.SDK
{
    public static class StatsHelixApi
    {
#if !DEBUG
        public const string API_BASEPATH = "https://sdk.statshelix.com";
#else
        public const string API_BASEPATH = "https://localhost:2000";
#endif

        public static int GameId { get; private set; } = -1;
        public static string GameName { get; private set; } = null;

        public static bool IsInitialized => InitializeTask.Task.IsCompleted;

        /// <summary>
        /// This will allow you to make sure the user is connected to the API properly
        /// </summary>
        public static Authentication Auth { get; } = new Authentication();

        public static GameState GameState { get; } = new GameState();

        private static readonly object InitLock = new object();
        internal static TaskCompletionSource<bool> InitializeTask = new TaskCompletionSource<bool>();

        internal static readonly HttpClient Http;

        static StatsHelixApi()
        {
            var httpHandler = new HttpClientHandler();
#if DEBUG
            try
            {
                httpHandler.ServerCertificateCustomValidationCallback = LocalHostAllowAnyCertCallback;
            }
            catch
            {
                // this fails e.g. on Unity
            }
#endif

            Http = new HttpClient(httpHandler);
            Http.Timeout = TimeSpan.FromSeconds(5);
            Http.BaseAddress = new Uri(API_BASEPATH);

            Http.DefaultRequestHeaders.Add("X-SH-Api-Client", Assembly.GetExecutingAssembly().FullName);
        }

        static bool LocalHostAllowAnyCertCallback(HttpRequestMessage msg, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (msg.RequestUri.Host == "localhost" || msg.RequestUri.Host == "127.0.0.1")
                return true;

            return errors == SslPolicyErrors.None;
        }

        /// <summary>
        /// Initialize the API.
        /// </summary>
        /// <param name="gameId">The GameId for this game. You can find it in your dashboard.</param>
        /// <param name="gameName">The name of the game shown to the user during signup.</param>
        public static void Initialize(int gameId, string gameName)
        {
            lock (InitLock)
            {
                if (!InitializeTask.TrySetResult(true))
                {
                    // trying to re-initialize
                    // we allow (accidental) re-initialization, unless you change the values
                    if ((gameId != GameId) || (gameName != GameName))
                        throw new Exception("Cannot initialize the StatsHelix SDK twice with different values. If this is intentional, invoke ReInitialize() instead.");
                }
                GameId = gameId;
                GameName = gameName;
            }
        }

        /// <summary>
        /// Reinitialize the API with different parameters.
        /// You should not use this unless you actually need to switch between multiple different game IDs at runtime,
        /// i.e. your game process corresponds to multiple games in the SDK backend.
        /// </summary>
        public static void ReInitialize(int gameId, string gameName)
        {
            lock (InitLock)
            {
                InitializeTask.TrySetResult(true);
                GameId = gameId;
                GameName = gameName;
            }
        }

        /// <summary>
        /// This is just a bit of housekeeping, to make sure everyone properly initializes themselves before
        /// using the API.
        /// </summary>
        internal static void EnsureInitialization()
        {
            if (!IsInitialized)
                throw new InvalidOperationException("You need to initialize the StatsHelixApi before using it. Use StatsHelixApi.Intialize() to initialize it.");
        }

        private static readonly MethodInfo UnityLog = Type.GetType("UnityEngine.Debug,UnityEngine.CoreModule")?.GetMethod("Log", new[] { typeof(object) });

        internal static void Log(string s)
        {
            s = "[StatsHelixApi] " + s;
            UnityLog?.Invoke(null, new[] { s });
            Console.Error.WriteLine(s);
            Debug.WriteLine(s);
        }
    }
}
