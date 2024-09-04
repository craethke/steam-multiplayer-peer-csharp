# SteamMultiplayerPeer for C#

This is an implementation of Godot's MultiplayerPeer (used by the high-level multiplayer API) using Steam for the underlying networking. 

## To use:
* Install the GodotSteam extension, or use their pre-compiled Godot binaries: https://github.com/GodotSteam/GodotSteam
* Copy and paste SteamMultiplayerPeer.cs into your Godot C# project (e.g. /addons/steam-multiplayer-peer-csharp/SteamMultiplayerPeer.cs)
* Create an instance of SteamMultiplayerPeer and use the CreateServer/CreateClient methods as below:
```
    private const int VIRTUAL_PORT = 0;

    public void CreateServer() {
        var multiplayerPeer = new SteamMultiplayerPeer();
        var error = multiplayerPeer.CreateServer(VIRTUAL_PORT);

        if (error != Error.Ok) {
            GD.PrintErr("Error creating host: ", error);
            return;
        }

        Multiplayer.MultiplayerPeer = multiplayerPeer;
    }

    public void CreateClient(ulong steamId) {
        var multiplayerPeer = new SteamMultiplayerPeer();
        var error = multiplayerPeer.CreateClient(steamId, VIRTUAL_PORT);

        if (error != Error.Ok) {
            GD.PrintErr("Failed to create client: ", error);
            return;
        }

        Multiplayer.MultiplayerPeer = multiplayerPeer;
    }
```

## Known issues
* Configuration flags are not yet fully implemented
* Channels are not implemented

## See also:
* https://github.com/GodotSteam/GodotSteam - Bindings for the Steam API in Godot
* https://github.com/LauraWebdev/GodotSteam_CSharpBindings - C Sharp bindings for GodotSteam
* https://github.com/expressobits/steam-multiplayer-peer - The original SteamMultiplayerPeer GDExtension that this is based off of. Use this if you're using GDScript or another language.
