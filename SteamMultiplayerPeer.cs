using Godot;
using System;
using System.Collections.Generic;
using GodotSteam;
using Godot.Collections;
using System.Linq;

public partial class SteamMultiplayerPeer : MultiplayerPeerExtension
{
    private const int MaxMessageCount = 255;
    private const int MaxSteamPacketSize = 512 * 1024;
    private const int SteamNetConnectionInvalid = 0;

    public bool NoNagle { get; set; } = false;
    public bool NoDelay { get; set; } = false;

    private System.Collections.Generic.Dictionary<ulong, SteamConnection> connectionsBySteamId64 = new System.Collections.Generic.Dictionary<ulong, SteamConnection>();
    private System.Collections.Generic.Dictionary<int, SteamConnection> peerIdToSteamId = new System.Collections.Generic.Dictionary<int, SteamConnection>();
    private int transferMode = (int)TransferModeEnum.Reliable;
    private uint listenSocket = 0;
    private Queue<SteamPacketPeer> incomingPackets = new Queue<SteamPacketPeer>();
    private ConnectionStatus connectionStatus = ConnectionStatus.Disconnected;
    private MultiplayerPeerMode mode = MultiplayerPeerMode.NONE;
    private int targetPeer = -1;
    private uint uniqueId = 0;

    private Godot.Collections.Array _configs;
    public Array<Steam.NetworkingConfigValue> Configs {
        set {
            _configs = new Godot.Collections.Array();
            foreach (var item in value) {
                _configs.Add((long) item);
            }
        }
        get {
            return new Array<Steam.NetworkingConfigValue>(_configs);
        }
    }

    public SteamMultiplayerPeer()
    {
        Steam.NetworkConnectionStatusChanged += _OnNetworkConnectionStatusChanged;
        Configs = new Array<Steam.NetworkingConfigValue>();
    }

    public Error CreateServer(int localVirtualPort) {
        if (IsActive()) {
            GD.PrintErr("The multiplayer instance is already active");
            return Error.AlreadyInUse;
        }
        Steam.InitRelayNetworkAccess();

        listenSocket = Steam.CreateListenSocketP2P(localVirtualPort, _configs);

        if (listenSocket == SteamNetConnectionInvalid) {
            return Error.CantCreate;
        }

        uniqueId = 1;
        mode = MultiplayerPeerMode.SERVER;
        connectionStatus = ConnectionStatus.Connected;
        return Error.Ok;
    }

    public Error CreateClient(ulong identityRemote, int remoteVirtualPort) {
        if (IsActive()) {
            GD.PrintErr("The multiplayer instance is already active");
            return Error.AlreadyInUse;
        }
        uniqueId = GenerateUniqueId();
        Steam.InitRelayNetworkAccess();

        uint connection = Steam.ConnectP2P(identityRemote, remoteVirtualPort, _configs);
        
        if (connection == SteamNetConnectionInvalid) {
            uniqueId = 0;
            GD.PrintErr("Failed to connect; connection is invalid");
            return Error.CantConnect;
        }

        mode = MultiplayerPeerMode.CLIENT;
        connectionStatus = ConnectionStatus.Connecting;
        return Error.Ok;
    }

    public override byte[] _GetPacketScript()
    {
        if (incomingPackets.Count == 0)
        {
            return new byte[]{};
        }

        SteamPacketPeer nextReceivedPacket = incomingPackets.Dequeue();
        return nextReceivedPacket.Data;
    }

    public override Error _PutPacketScript(byte[] buffer)
    {
        if (!IsActive() || connectionStatus != ConnectionStatus.Connected || !peerIdToSteamId.ContainsKey(Math.Abs(targetPeer)))
        {
            return Error.Unconfigured;
        }

        int packetTransferMode = GetSteamTransferFlag();

        if (targetPeer == 0)
        {
            Error returnValue = Error.Ok;
            foreach (var connection in connectionsBySteamId64)
            {
                SteamPacketPeer packet = new SteamPacketPeer(buffer, transferMode: packetTransferMode);
                Error errorCode = connection.Value.Send(packet);
                if (errorCode != Error.Ok)
                {
                    returnValue = errorCode;
                }
            }
            return returnValue;
        }
        else
        {
            SteamPacketPeer packet = new SteamPacketPeer(buffer, transferMode: packetTransferMode);
            return GetConnectionByPeer(targetPeer).Send(packet);
        }
    }

    public override int _GetAvailablePacketCount()
    {
        return incomingPackets.Count;
    }

    public override int _GetMaxPacketSize()
    {
        return MaxSteamPacketSize;
    }

    public override TransferModeEnum _GetPacketMode()
    {
        if (!IsActive() || incomingPackets.Count == 0)
        {
            return TransferModeEnum.Reliable;
        }

        return incomingPackets.Peek().TransferMode == Steam.NetworkingSendReliable
            ? TransferModeEnum.Reliable
            : TransferModeEnum.Unreliable;
    }

    public override void _SetTransferMode(TransferModeEnum mode)
    {
        transferMode = (int) mode;
    }

    public override TransferModeEnum _GetTransferMode()
    {
        return (TransferModeEnum)transferMode;
    }

    public override void _SetTransferChannel(int pChannel)
    {
        // Channels not implemented yet
    }

    public override int _GetTransferChannel()
    {
        // Channels not implemented yet
        return 0;
    }

    public override void _SetTargetPeer(int peer)
    {
        targetPeer = peer;
    }

    public override int _GetPacketPeer()
    {
        if (!IsActive() || incomingPackets.Count == 0)
        {
            return 1;
        }

        return connectionsBySteamId64[incomingPackets.Peek().Sender].PeerId;
    }

    public override int _GetPacketChannel()
    {
        return 0;
    }

    public override bool _IsServer()
    {
        return uniqueId == 1;
    }

    public bool IsActive() {
        return mode != MultiplayerPeerMode.NONE;
    }

    public override void _Poll()
    {
        if (!IsActive())
        {
            return;
        }

        foreach (var entry in connectionsBySteamId64)
        {
            var messages = Steam.ReceiveMessagesOnConnection(entry.Value.ConnectionHandle, MaxMessageCount);
            foreach (Dictionary message in messages) {
                if (GetPeerIdFromSteam64(message["identity"].AsUInt64()) != -1)
                {
                    ProcessMessage(message);
                }
                else
                {
                    ProcessPing(message);
                }
            }
        }
    }

    public override void _Close()
    {
        if (!IsActive() || connectionStatus != ConnectionStatus.Connected)
        {
            return;
        }

        foreach (var entry in connectionsBySteamId64)
        {
            entry.Value.Close();
        }

        if (_IsServer())
        {
            CloseListenSocket();
        }

        peerIdToSteamId.Clear();
        connectionsBySteamId64.Clear();
        mode = MultiplayerPeerMode.NONE;
        uniqueId = 0;
        connectionStatus = ConnectionStatus.Disconnected;
    }

    public override void _DisconnectPeer(int peer, bool force)
    {
        if (!IsActive() || !peerIdToSteamId.ContainsKey(peer))
        {
            return;
        }

        SteamConnection connection = GetConnectionByPeer(peer);
        if (!connection.Close())
        {
            return;
        }

        connection.Flush();
        connectionsBySteamId64.Remove(connection.SteamId);
        peerIdToSteamId.Remove(peer);

        if (mode == MultiplayerPeerMode.CLIENT || mode == MultiplayerPeerMode.SERVER)
        {
            GetConnectionByPeer(0).Flush();
        }
        else if (force)
        {
            connectionsBySteamId64.Clear();
            Close();
        }
    }

    public override int _GetUniqueId()
    {
        if (!IsActive())
        {
            return 0;
        }
        return (int) uniqueId;
    }

    public override bool _IsServerRelaySupported()
    {
        return mode == MultiplayerPeerMode.SERVER || mode == MultiplayerPeerMode.CLIENT;
    }

    public override ConnectionStatus _GetConnectionStatus()
    {
        return connectionStatus;
    }

    private int GetSteamTransferFlag()
    {
        TransferModeEnum transferMode = _GetTransferMode();
        
        int flags = (int) ((Steam.NetworkingSendNoNagle * Convert.ToInt64(NoNagle)) | (Steam.NetworkingSendNoDelay * Convert.ToInt64(NoDelay)));

        return transferMode switch
        {
            TransferModeEnum.Reliable => (int) Steam.NetworkingSendReliable | flags,
            TransferModeEnum.Unreliable => (int) Steam.NetworkingSendUnreliable | flags,
            _ => throw new InvalidOperationException("Unknown transfer mode")
        };
    }

    private void _OnNetworkConnectionStatusChanged(long connectHandle, Dictionary connection, long oldState)
    {
        ulong steamId = connection["identity"].AsUInt64();
        Steam.NetworkingConnectionState oldStateEnum = (Steam.NetworkingConnectionState) oldState;
        Steam.NetworkingConnectionState newStateEnum = (Steam.NetworkingConnectionState) connection["connection_state"].AsInt32();
        
        if (connection["listen_socket"].AsUInt32() != 0 &&
            oldStateEnum == Steam.NetworkingConnectionState.None &&
            newStateEnum == Steam.NetworkingConnectionState.Connecting)
        {
            // TODO: Confirm connectHandle is supposed to be a uint
            ErrorResult result = (ErrorResult) Steam.AcceptConnection((uint)connectHandle);
            if (result != ErrorResult.Ok)
            {
                Steam.CloseConnection((uint)connectHandle, (int) Steam.NetworkingConnectionEnd.AppExceptionGeneric, "Failed to accept connection", false);
            }
        }

        if ((oldStateEnum == Steam.NetworkingConnectionState.Connecting ||
            oldStateEnum == Steam.NetworkingConnectionState.FindingRoute) &&
            newStateEnum == Steam.NetworkingConnectionState.Connected)
        {
            AddConnection(steamId, (uint) connectHandle);
            if (!_IsServer())
            {
                connectionStatus = ConnectionStatus.Connected;
                connectionsBySteamId64[steamId].SendPeer(uniqueId);
            }
        }

        if ((oldStateEnum == Steam.NetworkingConnectionState.Connecting ||
            oldStateEnum == Steam.NetworkingConnectionState.Connected) &&
            newStateEnum == Steam.NetworkingConnectionState.ClosedByPeer)
        {
            if (!_IsServer())
            {
                if (connectionStatus == ConnectionStatus.Connected)
                {
                    EmitSignal("peer_disconnected", 1);
                }
                Close();
            }
            else
            {
                if (connectionsBySteamId64.ContainsKey(steamId))
                {
                    SteamConnection steamConnection = connectionsBySteamId64[steamId];
                    int peerId = steamConnection.PeerId;
                    if (peerId != -1)
                    {
                        EmitSignal("peer_disconnected", peerId);
                        peerIdToSteamId.Remove(peerId);
                    }
                    connectionsBySteamId64.Remove(steamId);
                }
            }
        }

        if ((oldStateEnum == Steam.NetworkingConnectionState.Connecting ||
            oldStateEnum == Steam.NetworkingConnectionState.Connected) &&
            newStateEnum == Steam.NetworkingConnectionState.ProblemDetectedLocally)
        {
            if (!_IsServer())
            {
                if (connectionStatus == ConnectionStatus.Connected)
                {
                    EmitSignal("peer_disconnected", 1);
                }
                Close();
            }
            else
            {
                if (connectionsBySteamId64.ContainsKey(steamId))
                {
                    SteamConnection steamConnection = connectionsBySteamId64[steamId];
                    int peerId = steamConnection.PeerId;
                    if (peerId != -1)
                    {
                        EmitSignal("peer_disconnected", peerId);
                        peerIdToSteamId.Remove(peerId);
                    }
                    connectionsBySteamId64.Remove(steamId);
                }
            }
        }
    }

    private int GetPeerIdFromSteam64(ulong steam64)
    {
        if (connectionsBySteamId64.ContainsKey(steam64))
        {
            return connectionsBySteamId64[steam64].PeerId;
        }
        return -1;
    }

    private void SetSteamIdPeer(ulong steamId, int peerId) {
        if (steamId == Steam.GetSteamID()) {
            GD.PrintErr("Cannot add self as a new peer");
            return;
        }
        if (!connectionsBySteamId64.ContainsKey(steamId)) {
            GD.PrintErr("Steam ID missing");
            return;
        }

        SteamConnection connection = connectionsBySteamId64[steamId];
        if (connection.PeerId == -1) {
            connection.PeerId = peerId;
            peerIdToSteamId[peerId] = connection;
        }
    }

    private SteamConnection GetConnectionByPeer(int peerId)
    {
        if (peerIdToSteamId.ContainsKey(peerId))
        {
            return peerIdToSteamId[peerId];
        }
        return null;
    }

    private void ProcessMessage(Dictionary message)
    {
        byte[] data = message["payload"].AsByteArray();
        ulong identity = message["identity"].AsUInt64();
        incomingPackets.Enqueue(new SteamPacketPeer(data, sender: identity));
    }

    private void ProcessPing(Dictionary message)
    {
        int peerId = BitConverter.ToInt32(message["payload"].AsByteArray());
        ulong steamId = message["identity"].AsUInt64();

        SteamConnection connection = connectionsBySteamId64[steamId];

        if (peerId != 0) {
            if (connection.PeerId == -1) {
                SetSteamIdPeer(steamId, peerId);
            }
            if (_IsServer()) {
                Error error = connection.SendPeer(uniqueId);
                if (error != Error.Ok) {
                    GD.PrintErr("Error sending server peer ID to client: ", error);
                }
            }
            EmitSignal(SignalName.PeerConnected, connection.PeerId);
        }
    }

    private void AddConnection(ulong steamId, uint connectionHandle)
    {
        if (steamId == Steam.GetSteamID()) {
            GD.PrintErr("Cannot add self as a new peer");
        }
        if (!connectionsBySteamId64.ContainsKey(steamId))
        {
            SteamConnection connection = new SteamConnection
            {
                SteamId = steamId,
                ConnectionHandle = connectionHandle,
            };
            connectionsBySteamId64.Add(steamId, connection);
            peerIdToSteamId.Add(connection.PeerId, connection);
        }
    }

    private int AssignPeerId()
    {
        return peerIdToSteamId.Count() + 2;
    }

    private void CloseListenSocket()
    {
        if (listenSocket != 0)
        {
            Steam.CloseListenSocket(listenSocket);
            listenSocket = 0;
        }
    }

    private enum MultiplayerPeerMode {
        NONE, SERVER, CLIENT
    }

    private class SteamPacketPeer {
        public byte[] Data { get; private set; } = new byte[MaxSteamPacketSize];
        public ulong Sender { get; set; }
        public long TransferMode { get; private set; } = Steam.NetworkingSendReliable;
        
        public SteamPacketPeer(byte[] buffer, ulong sender = 0, long transferMode = Steam.NetworkingSendReliable)
        {
            if (buffer.Length > MaxSteamPacketSize)
            {
                GD.PrintErr("Error: Tried to send a packet larger than MaxSteamPacketSize");
                return;
            }

            Sender = sender;
            Data = buffer;
            TransferMode = transferMode;
        }
    }

    private class SteamConnection {

        public ulong SteamId { get; set; }
        public  uint ConnectionHandle { get; set; }
        public int PeerId { get; set; } = -1;
        List<SteamPacketPeer> pendingRetryPackets = new List<SteamPacketPeer>();

        ~SteamConnection() {
            Steam.CloseConnection(ConnectionHandle, (int) Steam.NetworkingConnectionEnd.AppGeneric, "Disconnect Default!", true);
        }

        public ErrorResult RawSend(SteamPacketPeer packet) {
            Dictionary result = Steam.SendMessageToConnection(ConnectionHandle, packet.Data, packet.TransferMode);
            return (ErrorResult) result["result"].AsInt32();
        }

        public Error SendPending() {
            while (pendingRetryPackets.Count > 0) {
                var packet = pendingRetryPackets.First();
                ErrorResult errorCode = RawSend(packet);
                if (errorCode == ErrorResult.Ok) {
                    pendingRetryPackets.RemoveAt(0);
                } else {
                    string errorString = ConvertErrorResultToString(errorCode);
                    if ((packet.TransferMode & Steam.NetworkingSendReliable) != 0) {
                        GD.PrintErr("Send error (reliable, will retry): ", errorString);
                        break;
                    } else {
                        GD.PrintErr("Send error (unreliable, won't retry): ", errorString);
                        pendingRetryPackets.RemoveAt(0);
                    }
                }
            }
            return Error.Ok;
        }

        public void AddPacket(SteamPacketPeer packet) {
            pendingRetryPackets.Add(packet);
        }

        public Error Send(SteamPacketPeer packet) {
            AddPacket(packet);
            return SendPending();
        }

        public void Flush() {
            if (ConnectionHandle == SteamNetConnectionInvalid) {
                return;
            }
            Steam.FlushMessagesOnConnection(ConnectionHandle);
        }

        public bool Close() {
            if (ConnectionHandle == SteamNetConnectionInvalid) {
                return false;
            }
            return Steam.CloseConnection(ConnectionHandle, (int) Steam.NetworkingConnectionEnd.AppGeneric, "Failed to accept connection", false);
        }

        public override bool Equals(object obj)
        {
            return obj is SteamConnection other &&
                   SteamId == other.SteamId;
        }

        public override int GetHashCode()
        {
            return SteamId.GetHashCode();
        }

        public Error SendPeer(uint peerId) {
            SetupPeerPayload payload = new SetupPeerPayload(peerId);
            return SendSetupPeer(payload);
        }

        private Error SendSetupPeer(SetupPeerPayload payload) {
            var packet = new SteamPacketPeer(BitConverter.GetBytes(payload.PeerId), transferMode: (long) TransferModeEnum.Reliable);
            return Send(packet);
        }

        string ConvertErrorResultToString(ErrorResult errorResult) {
            return errorResult.ToString();
        }

        struct SetupPeerPayload {
            public uint PeerId { get; private set; }

            public SetupPeerPayload(uint peerId) {
                PeerId = peerId;
            }
        }
    }
}

