using System;
using System.IO;
using System.Linq;
using System.Timers;
using System.Collections.Generic;

using ENet;

using GameServer.Logging;
using GameServer.Networking.Packet;
using GameServer.Networking.Utils;
using GameServer.Networking.Message;

using Common.Networking.Packet;
using Common.Networking.Message;

namespace GameServer.Networking
{
    class Server
    {
        public static Dictionary<string, HandlePacket> HandlePackets = typeof(HandlePacket).Assembly.GetTypes().Where(x => typeof(HandlePacket).IsAssignableFrom(x) && !x.IsAbstract).Select(Activator.CreateInstance).Cast<HandlePacket>().ToDictionary(x => x.GetType().Name, x => x);

        public static Host Host;
        public GameTimer positionUpdatePump;

        private const int POSITION_UPDATE_DELAY = 100;

        public static byte ChannelID = 0;

        private ushort port;
        private int maxClients;

        private bool serverRunning;

        public static List<Client> clients;
        public static List<Client> positionPacketQueue;

        public Server(ushort port, int maxClients)
        {
            this.port = port;
            this.maxClients = maxClients;

            clients = new List<Client>();
            positionPacketQueue = new List<Client>();

            positionUpdatePump = new GameTimer(POSITION_UPDATE_DELAY, PositionUpdates);
            positionUpdatePump.Start();
        }

        public void Start()
        {
            Library.Initialize();

            Host = new Host();

            var address = new Address();
            address.Port = port;
            Host.Create(address, maxClients);
            serverRunning = true;

            Logger.Log($"Server listening on {port}");

            //int packetCounter = 0;
            //int i = 0;
            Event netEvent;

            while (serverRunning)
            {
                var polled = false;

                while (!polled)
                {
                    if (!Host.IsSet)
                        return;

                    if (Host.CheckEvents(out netEvent) <= 0)
                    {
                        if (Host.Service(15, out netEvent) <= 0)
                            break;

                        polled = true;
                    }

                    var ip = netEvent.Peer.IP;
                    var id = netEvent.Peer.ID;

                    switch (netEvent.Type)
                    {
                        case EventType.None:
                            break;

                        case EventType.Connect:
                            Logger.Log($"Client connected - ID: {id}, IP: {ip}");
                            clients.Add(new Client(netEvent.Peer));
                            break;

                        case EventType.Disconnect:
                            Logger.Log($"Client disconnected - ID: {id}, IP: {ip}");
                            clients.Remove(clients.Find(x => x.ID.Equals(netEvent.Peer.ID)));
                            break;

                        case EventType.Timeout:
                            Logger.Log($"Client timeout - ID: {id}, IP: {ip}");
                            clients.Remove(clients.Find(x => x.ID.Equals(netEvent.Peer.ID)));
                            break;

                        case EventType.Receive:
                            //Logger.Log($"{packetCounter++} Packet received from - ID: {id}, IP: {ip}, Channel ID: {netEvent.ChannelID}, Data length: {netEvent.Packet.Length}");
                            HandlePacket(netEvent);
                            netEvent.Packet.Dispose();
                            break;
                    }
                }

                Host.Flush();
            }

            CleanUp();
        }

        // Send a position update to all peers in the game every x ms
        private void PositionUpdates(Object source, ElapsedEventArgs e)
        {
            SendPositionUpdate();
        }

        private void SendPositionUpdate()
        {
            // If nothing is being queued there is no reason to check if we need to send position updates
            if (positionPacketQueue.Count == 0)
                return;

            var clientsInGame = clients.FindAll(x => x.Status == ClientStatus.InGame);

            // If there's only one or no client(s) there is no reason to send position updates to no one
            if (clientsInGame.Count <= 1)
                return;

            var data = new List<object>(); // Prepare the data list that will eventually be serialized and sent

            // Send clientQueued data to every other client but clientQueued client
            foreach (var clientQueued in positionPacketQueue)
            {
                // Add the clientQueued data to data list
                data.Add(clientQueued.ID);
                data.Add(clientQueued.x);
                data.Add(clientQueued.y);

                var sendPeers = new List<Peer>();

                // Figure out which clients we need to send this clientQueued data to
                foreach (Client clientInGame in clientsInGame)
                {
                    if (clientInGame.ID == clientQueued.ID) // We do not want to send data back to the queued client
                        continue;

                    sendPeers.Add(clientInGame.Peer);
                }

                // Send the data to the clients
                Logger.Log($"Broadcasting to client {clientQueued.ID}");
                //Network.Broadcast(server, GamePacket.Create(ServerPacketType.PositionUpdate, PacketFlags.None, data.ToArray()), sendPeers.ToArray());

                /*var serverPacket = new ServerPacket(ServerPacketType.PositionUpdates, new MessageHandshake());
                var packet = default(ENet.Packet);
                packet.Create(serverPacket.Data, PacketFlags.None);
                Network.Broadcast(packet, sendPeers.ToArray());*/

                positionPacketQueue.Remove(clientQueued);
            }
        }

        public static Peer[] GetPeersInGame()
        {
            return clients.FindAll(x => x.Status == ClientStatus.InGame).Select(x => x.Peer).ToArray();
        }

        private void HandlePacket(Event netEvent)
        {
            uint id = netEvent.Peer.ID;

            try
            {
                var readBuffer = new byte[1024];
                var readStream = new MemoryStream(readBuffer);
                var reader = new BinaryReader(readStream);

                readStream.Position = 0;
                netEvent.Packet.CopyTo(readBuffer);

                var packetID = (ClientPacketType)reader.ReadByte();

                if (packetID == ClientPacketType.Disconnect) 
                {
                    Logger.Log($"Client {netEvent.Peer.ID} has disconnected");
                }

                /*switch (packetID)
                {
                    case PacketType.A:
                        HandlePackets["ClientRequestNames"].Run(id);
                        break;

                    case PacketType.B:
                        HandlePackets["ClientRequestPositions"].Run(id);
                        break;
                }   */

                readStream.Dispose();
                reader.Dispose();
            }

            catch (ArgumentOutOfRangeException)
            {
                Logger.LogWarning($"Received packet from client '{id}' but buffer was too long. {netEvent.Packet.Length}");
            }
        }

        public void Stop()
        {
            serverRunning = false;
        }

        private void CleanUp()
        {
            positionUpdatePump.Dispose();
            Host.Dispose();
            Library.Deinitialize();
        }
    }
}
