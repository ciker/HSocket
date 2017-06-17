using HSocket.Hub;
using System;
using System.Collections.Generic;
using HSocket.Server;
using System.Text;

namespace HubExample
{
    class Program
    {
        static void Main(string[] args)
        {
            SocketHub server = new SocketHub(8000);
            server.Start();
            Console.WriteLine("Server start on 8000");

            server.onHandshake += Server_onHandshake;
            server.OnConnected += Server_OnConnected;

            server.OnBinaryReceived += Server_OnBinaryReceived;
            server.OnTextReceived += Server_OnTextReceived;
            

            server.OnStateChange += Server_OnStateChange;
            server.OnClose += Server_OnClose;

            Console.ReadLine();
            server.Close(false);
        }

        private static void Server_OnClose(Connection client)
        {
            
        }

        private static void Server_OnStateChange(Connection client, State state)
        {
             
        }

        private static void Server_OnTextReceived(Connection client, string data)
        {
            client.Send(data);
            //client.Send(Encoding.UTF8.GetBytes(data));
        }

        private static void Server_OnBinaryReceived(Connection client, byte[] data)
        {
           
        }

        private static void Server_OnConnected(Connection client)
        {
            client.Send("hello");
        }

        private static bool Server_onHandshake(Dictionary<string, string> headers)
        {
            return true;
        }
    }
}
