using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace HSocket.Server
{  
    public class Connection<T> where T :  Connection<T>
    {
        public Connection()
        {
            _retryQueue = new Queue<Message>();
            _state = State.Open;
            binary = new List<byte>();
            respondFrames = new Queue<Message>();
        }
        public event TextReceivedEventHandler<T> MessageReceived;
        public event BinaryReceivedEventHandler<T> BinaryReceived;
        public event ConnectionClosedEventHandler<T> Closed;
        public event StateChangedEventHandler<T> StateChanged;
        
        public string ID { get { return _id; } }
        public int KeepAlive { get; set; }
        public bool RetryOnConnectionBusy { get; set; } = true;
        // Queue of messages that have failed.
        private Queue<Message> _retryQueue;     
        private string _id;
        private State _state;
        private TcpClient _conn;
        private NetworkStream _stream;
        private string message = string.Empty;
        private List<byte> binary;
        private OpCode currentOpCode;
        private bool _sending = false;
        // Queue of messages in need of return ASAP. Close requests, pongs, etc...
        private Queue<Message> respondFrames;

        public State State
        {
            get { return _state; }
            private set
            {
                _state = value;
                StateChanged?.Invoke(this as T, _state);
            }
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="conn">A TCP connection that has been upgraded to a websocket connection.</param>
        internal void Init(TcpClient conn, string id)
        {
            if (!conn.Connected)
                throw new ArgumentException("The connection is closed.");
            _id = id;
            _conn = conn;
            _stream = conn.GetStream();
            Task.Run(() => Read());
        }
           

         
        /// <summary>
        /// Sends a string
        /// </summary>
        /// <param name="text">Message</param>
        /// <exception cref="ConnectionBusyException">Occurs when the socket is busy sending another message.</exception>
        public void Send(string text)
        {
            Send(new Message(text));
        }
        public void Send(Message msg){
            _send(msg);
        }
        /// <summary>
        /// Sends binary
        /// </summary>
        /// <param name="binary">Data</param>
        /// <exception cref="ConnectionBusyException">Occurs when the socket is busy sending another message.</exception>
        public void Send(byte[] binary)
        {
            Send(new Message(binary));
        }
        internal void _send(byte[] data, OpCode code){
            _send(new Message(){Data = data, opCode = code});
        }
        internal void _send(Message msg, bool recursive = false)
        {
            if (!recursive)
            {
                if (State != State.Open)
                    if (State != State.Closing && msg.opCode != OpCode.ConnectionClose)
                        throw new ConnectionClosedException();
                if (_sending)
                {
                    if(RetryOnConnectionBusy){
                        _retryQueue.Enqueue(msg);
                    } else
                        throw new ConnectionBusyException();
                }

                _sending = true;
            }

            var code = msg.opCode;
            var data = msg.Data;

            List<byte> bytes = new List<byte>();
            bytes.Add((byte)(0x80 + code));
            if (data.Length <= 125)
                bytes.Add((byte)(data.Length));
            else if (data.Length <= 65535)
            {
                bytes.Add((byte)126);
                bytes.Add((byte)((0xFF & (data.Length >> 8))));
                bytes.Add((byte)(0xFF & data.Length));
            }
            else
            {
                bytes.Add((byte)127);
                for (int i = 0; i < 8; i++)
                    bytes.Add((byte)(0xFF & ((long)(data.Length) >> (8 * (7 - i)))));
            }
            bytes.AddRange(data);
            try
            {
                _stream.Write(bytes.ToArray(), 0, bytes.Count);
                _stream.Flush();

                if (!recursive)
                {
                    while (respondFrames.Count > 0 || _retryQueue.Count > 0)
                    {
                        if(respondFrames.Count > 0){
                            var frame = respondFrames.Dequeue();
                            _send(frame, true);
                            frame.OnComplete();
                        } else {
                            var frame = _retryQueue.Dequeue();
                            _send(frame, true);
                            frame.OnComplete();
                        }
                    }
                }
            }
            catch (IOException) { throw new ConnectionClosedException(); }
            catch (Exception) { Close(true); }

            if (!recursive)
                _sending = false;
        }
        private async Task<Frame> GetFrameInfo()
        {
            var re = new Frame();

            // Read until first 2 bytes are read
            int read = 0;
            byte[] buff = new byte[2];
            read += await _stream.ReadAsync(buff, 0, buff.Length);
            while (read < buff.Length)
                read += await _stream.ReadAsync(buff, read, buff.Length - read);

            // Check message is masked
            if ((buff[1] >> 7) != 1)
                throw new UnmaskedMessageException();
            
            // Check RSV1,2,3 are set 0
            if ((buff[0] & 0x70) != 0)
                throw new NotImplementedException("RSV1, RSV2, RSV3 must be set to 0.");

            // Extract opcode
            re.OpCode = (OpCode)(buff[0] & 0xF);
            
            // Extract FIN
            re.FIN = (buff[0] >> 7) == 1;

            // Extract length
            int len = buff[1] & 0x7F;
            if (len < 126) re.Length = len;
            else if (len == 126)
                re.Length = (_stream.ReadByte() << 8) + _stream.ReadByte();
            else
                for (int i = 0; i < 8; i++)
                    re.Length += _stream.ReadByte() << (8 * (7 - i));

            // Extract mask
            re.Mask = new byte[4];
            _stream.Read(re.Mask, 0, 4);

            // Extract payload
            re.Payload = new byte[re.Length];
            long count = 0;
            while(count < re.Length)
                count += _stream.Read(re.Payload, (int)count, (int)(re.Length - count));

            return re;
        }
        private async void Read()
        {
            if (State != State.Open)
                return;
            try
            {
                var frame = await GetFrameInfo();

                switch (frame.OpCode)
                {
                    case OpCode.TextFrame:
                        HandleIncomingText(frame);
                        break;
                    case OpCode.BinaryFrame:
                        HandleIncomingBinary(frame);
                        break;
                    case OpCode.ConnectionClose:
                        HandleCloseRequest(frame);
                        break;
                    case OpCode.Ping:
                        Pong(frame);
                        break;
                    case OpCode.ContinuationFrame:
                        if (currentOpCode == OpCode.TextFrame)
                            HandleIncomingText(frame);
                        else
                            HandleIncomingBinary(frame);
                        break;
                    default:
                        Close();
                        break;
                }
            }
            catch  { Close(); }
            
            Task.Run(() => Read());
        }
        private void HandleIncomingBinary(Frame frame)
        {
            binary.AddRange(frame.UnmaskedData);
            if (frame.FIN)
            {
                BinaryReceived?.Invoke(this as T, binary.ToArray());
                binary.Clear();
            }
            else
                currentOpCode = OpCode.BinaryFrame;
        }
        private void HandleIncomingText(Frame frame)
        { 
            message += Encoding.UTF8.GetString(frame.UnmaskedData);
            if (frame.FIN)
            {
                MessageReceived?.Invoke(this as T, message);
                message = string.Empty;
            }
            else
                currentOpCode = OpCode.TextFrame;
        }
        private void HandleCloseRequest(Frame frame)
        {
            if (State != State.Closing)
            {
                State = State.Closing;
                try
                {
                    _send(frame.UnmaskedData, OpCode.ConnectionClose);
                }
                catch (ConnectionBusyException)
                {
                    respondFrames.Clear();
                    respondFrames.Enqueue(new Message(frame.UnmaskedData, OpCode.ConnectionClose, _shutdown));
                    return;
                }
            }
            _shutdown();
        }
        private void Pong(Frame frame)
        {
            try
            {
                _send(frame.UnmaskedData, OpCode.Pong);
            } catch (ConnectionBusyException)
            {
                respondFrames.Enqueue(new Message(frame.UnmaskedData, OpCode.Pong));
            }
        }
        private void _shutdown()
        {
            _stream.Dispose();
            _conn.Dispose();
            State = State.Closed;
            Closed?.Invoke(this as T);
        }
        /// <summary>
        /// Closes the connection. Connection enters closing state until it closes when Closed is invoked.
        /// </summary>
        public void Close()
        {
            try
            {
                _send(new byte[0], OpCode.ConnectionClose);
                State = State.Closing;
            } catch (ConnectionBusyException)
            {
                respondFrames.Enqueue(new Message(new byte[0], OpCode.ConnectionClose, new Action(() => { State = State.Closing; })));
            }
        }
        /// <summary>
        /// Closes the connection.
        /// </summary>
        /// <param name="hardquit">Set true if the connection should be abandoned instead of performing a clean exit.</param>
        public void Close(bool hardquit)
        {
            Close();
            if (hardquit)
                _shutdown();
        }

    }
     
    public class UnmaskedMessageException : Exception { }
    public class ConnectionClosedException : Exception { }
    public class ConnectionBusyException : Exception { }
}
