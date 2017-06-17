# HSocket
Socket server library for .NET Core.
All simple. Just create socket hub and and set event.

         SocketHub server = new SocketHub(8000);
            server.Start();
            Console.WriteLine("Server start on 8000");

            server.onHandshake += Server_onHandshake;
            server.OnConnected += Server_OnConnected;
            server.OnBinaryReceived += Server_OnBinaryReceived;
            server.OnTextReceived += Server_OnTextReceived;    
            server.OnStateChange += Server_OnStateChange;
            server.OnClose += Server_OnClose;
						
            server.Close(false);
						
And thats all you create you socket hub.
OR if u need more setting  or something more, you can create you own hub.
To create you own connection just Inheritance like this

    class CustomConnection : Connection<CustomConnection>
		
then create own custom server and set our CustomConnection

 	class CustomServer : SocketServer<CustomConnection>

more detail in example

