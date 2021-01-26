using System;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace StatsHelix.SDK
{
    public class GameState
    {
        // This class is a fair bit more complicated then it strictly needs to be.
        // We just want to make sure that we don't send requests too often, and we definitely
        // do not want to throttle your main loop, so all the heavy lifting happens off-thread.
        Thread ProcessGameStateThread;
        Dictionary<string, string> NextStateUpdate = null;
        AutoResetEvent NewStateEvent = new AutoResetEvent(false);

        private static readonly TimeSpan UpdateThrottle = TimeSpan.FromSeconds(1); // update at most this often
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(60); // update at least this often; make sure this is always significantly smaller than StateTimeout in Model/Channel.cs!
        private static readonly TimeSpan ConnectingUpdateInterval = TimeSpan.FromSeconds(5);

        bool NotifiedLibraryUser = false;

        public GameStateStatus Status { get; private set; } = GameStateStatus.Uninitialized;

        object CurrentGameStateLock = new object();
        Dictionary<string, string> CurrentGameState = new Dictionary<string, string>();


        HttpClient UpdateClient => StatsHelixApi.Http;

        public GameState()
        {
            ProcessGameStateThread = new Thread(new ThreadStart(ProcessGameStateThreadProc));
            // If the game needs CPU-time, they can definitely have it!
            ProcessGameStateThread.Priority = ThreadPriority.BelowNormal;
            ProcessGameStateThread.IsBackground = true;

            // Give this thread a friendly name, so you know what we are doing.
            ProcessGameStateThread.Name = "StatsHelix: ProcessGameStateThread";

            ProcessGameStateThread.Start();
        }

        public void SetGameState(params (string key, string value)[] newState)
        {
            SetGameState(newState.ToDictionary(x => x.key, x => x.value));
        }
        public void SetGameState(Dictionary<string, string> newState)
        {
            if (!StatsHelixApi.IsInitialized)
            {
                // So we're in a bit of a weird situation here.
                // We never want to break the game, especially in the main-loop in production.
                // But we want to inform the user of their misimplementation, so we do it once,
                // and then never again.
                if (!NotifiedLibraryUser)
                {
                    NotifiedLibraryUser = true;

                    // If the user is debugging, we throw an exception as usual, so they see it in their face.
                    if (Debugger.IsAttached)
                        StatsHelixApi.EnsureInitialization();

                    // Otherwise, we shall inform them on the various consoles.
                    StatsHelixApi.Log("WARNING: You need to initialize the StatsHelixApi before using it. Use StatsHelixApi.Intialize() to initialize it.");
                }

                return;
            }

            var newStateCopy = new Dictionary<string, string>(newState);
            // We always remember the current game state.
            lock (CurrentGameStateLock)
            {
                CurrentGameState = newStateCopy;
            }

            // We simply set the next hero (assignments are atomic in C#)
            // The ProcessGameStateThread will make sure it gets sent out.
            NextStateUpdate = newStateCopy;

            // And we tell the AutoResetEvent that we have some data ready.
            NewStateEvent.Set();

            // We have it slightly easy here, since the C# garbage collector will clean up after us,
            // so we don't need to free any resources or anything.
        }

        public void UpdatePartialGameState(params (string key, string value)[] updates)
        {
            lock (CurrentGameStateLock)
            {
                foreach (var update in updates)
                {
                    CurrentGameState[update.key] = update.value;
                }
                SetGameState(CurrentGameState);
            }
        }

        private class SetRequest
        {
            public string Token { get; set; }
            public int Game { get; set; }
            public Dictionary<string, string> State { get; set; }
        }

        private void ProcessGameStateThreadProc()
        {
            // At this point we're guaranteed to be authenticated :)
            Dictionary<string, string> lastUpdate = new Dictionary<string, string>();
            string warnToken = null;
            while (true)
            {
                try
                {
                    var waitTimeout = UpdateInterval - UpdateThrottle; // we just waited for UpdateThrottle, so we have to subtract it from UpdateInterval
                    if (StatsHelixApi.Auth.LastConnectAttempt?.Elapsed < TimeSpan.FromMinutes(5))
                    {
                        // if we are attempting to connect, poll more frequently for the next five minutes, until we succeed
                        waitTimeout = ConnectingUpdateInterval;
                    }

                    // Wait for data!
                    Dictionary<string, string> update;
                    if (NewStateEvent.WaitOne(waitTimeout))
                    {
                        update = Interlocked.Exchange(ref NextStateUpdate, null);
                        if (update == null)
                        {
                            // this can only happen if:
                            // 1. we wake up
                            // 2. another thread overwrites NextHeroUpdate
                            // 3. the other thread triggers AutoReset
                            // 4. we take the update
                            // 5. we wake up in the next iteration because the event was set again
                            //    but the update we sent out was already the new one

                            // the correct action is to go back to sleep and wait for the next update
                            continue;
                        }
                    }
                    else
                    {
                        // just repeat the last update if there is nothing new to send
                        update = lastUpdate;
                    }

                    lastUpdate = update;

                    var payload = new SetRequest
                    {
                        Token = StatsHelixApi.Auth.Token,
                        Game = StatsHelixApi.GameId,
                        State = update,
                    };

                    if (String.IsNullOrEmpty(payload.Token))
                    {
                        // currently no token, i.e. not authenticated
                        // skip this update and just try again later
                        continue;
                    }

                    var response = UpdateClient.PostAsync("/api/Gamestate/Set", new StringContent(JsonConvert.SerializeObject(payload))).Result;
                    switch (response.StatusCode)
                    {
                        case System.Net.HttpStatusCode.Forbidden:
                            if (warnToken != payload.Token) // warn only once per token
                            {
                                warnToken = payload.Token;
                                StatsHelixApi.Log("Can't update game state because the token is not authorized. If you are currently doing the Twitch signup, then it is not YET authorized - this is completely normal.");
                            }
                            Status = GameStateStatus.Unauthenticated;
                            StatsHelixApi.Auth.DropAuthCache();
                            break;
                        case System.Net.HttpStatusCode.NotFound:
                            StatsHelixApi.Log("WARNING: Your game was not found. Did you specify the correct game ID in the Initialize() call?");
                            return;
                        default:
                            if (!response.IsSuccessStatusCode)
                            {
                                string body = null;
                                try
                                {
                                    body = response.Content.ReadAsStringAsync().Result;
                                }
                                catch (Exception e)
                                {
                                    body = e.ToString();
                                }
                                StatsHelixApi.Log($"WARNING: Unexpected error response while updating game state: {response.StatusCode} - {body}");
                            }
                            else
                            {
                                Status = GameStateStatus.Sending;
                                StatsHelixApi.Auth.LastConnectAttempt = null;
                            }
                            break;
                    }

                    Thread.Sleep(UpdateThrottle);
                }
                catch (Exception e)
                {
                    // Whoops?
                    StatsHelixApi.Log("WARNING: Error when uploading game data: " + e);
                }
            }
        }
    }
}
