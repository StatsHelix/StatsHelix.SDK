using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StatsHelix.SDK.SampleClient
{
    class Program
    {
        static void Main()
        {
            AsyncMain().Wait();
        }

        static async Task AsyncMain()
        {
            // First, let's tell the api who we are.
            StatsHelixApi.Initialize(5, "Sample Game");

            var areWeAuthorized = await StatsHelixApi.Auth.IsThisComputerConnectedAsync();

            // Looks like we are not authenticated to the StatsHelix API, so we prompt the player
            // to connect this computer to their API account.
            // In your game you should to bind this to some kind of button.

            Thread.Sleep(5000);
            if (areWeAuthorized != ComputerConnectionState.Connected)
                StatsHelixApi.Auth.StartUserReConnectThisComputer();

            // Your code does not need to check whether the player actually chooses to authorize.
            // As soon as the player authenticates, the game state will be updated correctly.
            // If they don't, we simply throw away the data you submit.

            // Now, we just set the game-state that we're in.
            // This could be the main - loop of your program.
            // Don't worry, we will never block it, or throw exceptions.
            // Everything here is just quickly queued for processing on a seperate thread.
            var possibleNames = new string[] { "morf", "am" };
            var counter = 0;
            StatsHelixApi.GameState.SetGameState(new Dictionary<string, string> { { "Name", possibleNames[counter++ % possibleNames.Length] } });

            while (true)
            {
                StatsHelixApi.GameState.UpdatePartialGameState(
                    ("Name2", possibleNames[counter++ % possibleNames.Length]),
                    ("Name3", possibleNames[counter++ % possibleNames.Length])
                );

                // Pretend that things happen in our game
                await Task.Delay(TimeSpan.FromSeconds(7.5));
            }
        }

        static async void StatsHelixIn10Lines()
        {
            StatsHelixApi.Initialize(5, "Sample Game");
            if (ComputerConnectionState.Connected != await StatsHelixApi.Auth.IsThisComputerConnectedAsync())
                StatsHelixApi.Auth.StartUserReConnectThisComputer();

            var possibleNames = new string[] { "Dana", "Moritz", "Noah", "Mo" };
            var counter = 0;
            while (true)
            {
                StatsHelixApi.GameState.SetGameState(new Dictionary<string, string> { { "Name", possibleNames[counter++ % possibleNames.Length] } });
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }
}
