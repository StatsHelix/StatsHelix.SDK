using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace StatsHelix.SDK
{
    public class Authentication
    {
        private readonly object AuthenticationLock = new object();

        /// <summary>
        /// This is the location where a token is saved.
        /// </summary>
        public string TokenFileLocation = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StatsHelix.SDK",

#if DEBUG
            "ComputerTokenDebug.dat"
#else
            "ComputerToken.dat"
#endif
        );

        private string _Token;
        /// <summary>
        /// Gets a random string that uniquly identifies the computer to our API.
        /// </summary>
        public string Token => _Token ?? (_Token = GetUserToken());

        const int TOKEN_LENGTH_BYTES = 16;

        bool AuthenticatedCache = false;

        internal Stopwatch LastConnectAttempt = null;

        public void DisconnectThisComputer()
        {
            lock (AuthenticationLock)
            {
                if (File.Exists(TokenFileLocation))
                    File.Delete(TokenFileLocation);
                _Token = null;
                AuthenticatedCache = false;
            }
        }

        /// <summary>
        /// Is this computer/token connected to a Twitch-channel?
        /// </summary>
        /// <returns></returns>
        public async Task<ComputerConnectionState> IsThisComputerConnectedAsync()
        {
            StatsHelixApi.EnsureInitialization();

            string startToken;

            lock (AuthenticationLock)
            {
                startToken = Token;

                // If the user never expressed their desire to connect in any game, we aren't
                if (startToken == null)
                    return ComputerConnectionState.NotConnected;

                if (AuthenticatedCache)
                    return ComputerConnectionState.Connected;
            }

            string response;
            try
            {
                response = await StatsHelixApi.Http.GetStringAsync("/api/Auth/IsTokenConnected?token=" + Uri.EscapeDataString(startToken));
            }
            catch (Exception e)
            {
                StatsHelixApi.Log("Error while checking if computer is authenticated: " + e);
                return ComputerConnectionState.ServerUnreachable;
            }

            lock (AuthenticationLock)
            {
                if (JsonConvert.DeserializeObject<bool>(response))
                {
                    AuthenticatedCache = true;
                    return ComputerConnectionState.Connected;
                }
                else
                {
                    return ComputerConnectionState.NotConnected;
                }
            }
        }

        /// <summary>
        /// Open a website prompting to connect this computer/token to the streamer's twitch channel.
        /// </summary>
        public void StartUserReConnectThisComputer()
        {
            StatsHelixApi.EnsureInitialization();

            // If we don't have a token yet, we create one!
            var token = GetOrCreateUserToken();
            _Token = token;

            LastConnectAttempt = Stopwatch.StartNew();

            var urlPostfix = $"/login-streamer.html#hh_game_token={Uri.EscapeDataString(token)}&hh_game_name={Uri.EscapeDataString(StatsHelixApi.GameName)}";
            if (StatsHelixApi.API_BASEPATH.EndsWith("/"))
                urlPostfix = urlPostfix.Substring(1);

            // Yes, it's this easy!
            // We just shell_exec our https:// url to start the streamer's preferred browser
            Process.Start(new ProcessStartInfo()
            {
                FileName = $"{StatsHelixApi.API_BASEPATH}{urlPostfix}",
                UseShellExecute = true,
            });

            // If everything goes well, IsThisComputerConnectedAsync will start returning ComputerConnectionState.Connected at some point :)
        }

        private string GetOrCreateUserToken()
        {
            // First, we check if we (or another game using StatsHelix-technology) already have
            // generated a token. If yes, we can simply use that!
            var existingToken = GetUserToken();
            if (existingToken != null)
                return existingToken;

            // Alright, there is no token (yet). Looks like we need to generate one!
            // Now, we have the following reqiurements for our tokens:
            // - At least 96 bits of actual entropy
            // - No more than 48 bytes in urlencoded length
            // - Can decode to valid ASCII
            // - They are generated using a secure random number generator
            //
            // Just as a note, if you're not sure if your random number generator is secure, it likely isn't.
            // If you are unsure how to implement this, please do not hesitate at all to contact us!

            // Alright, on to generate it. We will use 16 bytes worth of random data (128 bits entropy), and
            // then simply hex-encode it, doubling it's size to 32 bytes, that are all url-encodable.
            // This is made rather easy with the C# standard library, but every envrionment should have equivalents.

            var secureRNG = new RNGCryptoServiceProvider();
            var data = new byte[TOKEN_LENGTH_BYTES];
            secureRNG.GetBytes(data);
            var dataAsHex = BitConverter.ToString(data);

            // Since the C# representation also contains '-' characters between the bytes, we remove them.
            // We don't strictly need to, though. They just don't add anything of value.
            dataAsHex = dataAsHex.Replace("-", "");

            // Now, we save that to the file, and are happy with it! :)
            Directory.CreateDirectory(Path.GetDirectoryName(TokenFileLocation));
            File.WriteAllText(TokenFileLocation, dataAsHex, Encoding.ASCII);

            return dataAsHex;
        }

        private string GetUserToken()
        {
            // First, we check if we (or another game using StatsHelix-technology) already have
            // generated a token. If yes, we simply use that!
            if (File.Exists(TokenFileLocation))
                return File.ReadAllText(TokenFileLocation, Encoding.ASCII);

            // If no, then we don't do anything unless the user expresses their desire to connect.
            return null;
        }

        internal void DropAuthCache()
        {
            AuthenticatedCache = false;
        }
    }
}
