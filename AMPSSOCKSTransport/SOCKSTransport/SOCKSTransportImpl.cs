////////////////////////////////////////////////////////////////////////////
//
// Copyright (c) 2010-2016 60East Technologies Inc., All Rights Reserved.
//
// This computer software is owned by 60East Technologies Inc. and is
// protected by U.S. copyright laws and other laws and by international
// treaties.  This computer software is furnished by 60East Technologies 
// Inc. pursuant to a written license agreement and may be used, copied,
// transmitted, and stored only in accordance with the terms of such
// license agreement and with the inclusion of the above copyright notice.
// This computer software or any other copies thereof may not be provided
// or otherwise made available to any other person.
//
// U.S. Government Restricted Rights.  This computer software: (a) was
// developed at private expense and is in all respects the proprietary
// information of 60East Technologies Inc.; (b) was not developed with
// government funds; (c) is a trade secret of 60East Technologies Inc.
// for all purposes of the Freedom of Information Act; and (d) is a
// commercial item and thus, pursuant to Section 12.212 of the Federal
// Acquisition Regulations (FAR) and DFAR Supplement Section 227.7202,
// Government’s use, duplication or disclosure of the computer software
// is subject to the restrictions set forth by 60East Technologies Inc..
//
////////////////////////////////////////////////////////////////////////////
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Web;

using System.Diagnostics;
using System.Linq;

using AMPS.Client.Exceptions;
using System.Collections.Generic;

namespace AMPS.Client.Extensions
{
    public class SOCKSTransportImpl
    {
        protected Uri _addr;
        protected Stream _socketStream = null;
        protected Socket _socket = null;
        public object _lock = new object();
        public volatile int _connectionVersion = 0;
        private volatile bool _disconnecting = false;
        private Protocol _messageType = null;
        private MessageHandler _onMessage = DefaultMessageHandler.instance;
        private TransportDisconnectHandler _onDisconnect = DefaultDisconnectHandler.instance;
        private SOCKSReaderThread _readerThread = null;
        private TCPTransportImpl.ExceptionListener _exceptionListener = null;
        private Properties _properties;
        private int _readTimeout = 0;
        private TransportFilter _filter = null;
        private Action _idleAction = null;

        public SOCKSTransportImpl(Protocol messageType_, Properties properties_)
        {
            this._messageType = messageType_;
            this._properties = properties_;
        }

        public void setTransportFilter(TransportFilter filter_)
        {
            _filter = filter_;
        }
        public void setReadTimeout(int readTimeoutMillis_)
        {
            _readTimeout = readTimeoutMillis_;
        }

        public void setMessageHandler(MessageHandler h)
        {
            this._onMessage = h;
        }

        public void setDisconnectHandler(TransportDisconnectHandler h)
        {
            this._onDisconnect = h;
        }

        public void setExceptionListener(AMPS.Client.TCPTransportImpl.ExceptionListener e)
        {
            _exceptionListener = e;
        }

        public void setIdleAction(Action idleAction)
        {
            _idleAction = idleAction;
        }


        public void connect(Uri addr)
        {
            lock (_lock)
            {
                _disconnecting = false;
                try
                {
                    if (this._addr != null)
                    {
                        throw new AlreadyConnectedException("Already connected to AMPS at " +
                           this._addr.Host + ":" + this._addr.Port + "\n");
                    }



                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    string proxyAddr = HttpUtility.ParseQueryString(addr.Query).Get("proxy");

                    if (proxyAddr == String.Empty)
                    {
                        throw new InvalidURIException("A socks connection must specify a proxy in the URI options.");
                    }
                    IPAddress proxyIp = IPAddress.Parse(proxyAddr.Split(':')[0]);
                    int proxyPort = int.Parse(proxyAddr.Split(':')[1]);

                    //applySocketProperties(_properties != null ? p.Union(_properties) : p);
                    _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    _socket.Connect(proxyIp, proxyPort);
                    negotiateSocks(addr);

                    createStream(addr);



                    _readerThread = new SOCKSReaderThread(this, this._messageType);
                    _addr = addr;
                    _connectionVersion++;
                }
                catch (ThreadInterruptedException e)
                {
                    // Clear the interrupt flag!  It is set, and simply catching this exception
                    // doesn't clear it.
                    //Thread.interrupted();
                    throw new ConnectionRefusedException("Interrupted, but please try again.", e);
                }
                catch (SocketException ioex)
                {
                    throw new ConnectionRefusedException("Unable to connect to AMPS at " +
                          addr.Host + ":" + addr.Port, ioex);
                }
            }

        }

        protected virtual void negotiateSocks(Uri uri)
        {

            byte[] handshake = { 0x05, 0x01, 0x00 };          // SOCKS5, 1 auth scheme, only support no authentication
            byte[] connectCommand = {0x05, 0x01, 0x00, 0x01,  // SOCKS5, TCP/IP stream, reserved field, IPv4 address type
                                     0x00, 0x00, 0x00, 0x00,  // host address -- to be filled in
                                     0x00, 0x00               // host port -- to be filled in
                                    };

            byte[] replyBuffer = new byte[10];
            _socket.Send(handshake);
            _socket.Receive(replyBuffer, 0, 2, SocketFlags.None);
            if (replyBuffer[0] != 5)
            {
                throw new ConnectionException("SOCKS proxy does not support SOCKS5.");
            }
            if (replyBuffer[1] != 0)
            {
                throw new ConnectionException("SOCKS proxy does not support an authentication type supported by this client.");
            }
            // Set up the connect command

            IPAddress ampsAddr = Dns.GetHostAddresses(uri.DnsSafeHost)[0];

            Array.Copy(ampsAddr.GetAddressBytes(), 0, connectCommand, 4, 4);
            Array.Copy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(uri.Port)), 2, connectCommand, 8, 2);

            _socket.Send(connectCommand);
            _socket.Receive(replyBuffer, 0, 5, SocketFlags.None);

            if (replyBuffer[0] != 5)
            {
                throw new ConnectionException("SOCKS proxy does not support SOCKS5.");
            }
            if (replyBuffer[1] != 0)
            {
                throw new ConnectionException("SOCKS proxy did not allow the request.");
            }

            // Ignore the rest of the response: we don't really have a way
            // to tell the client where the proxy connected, but we do need
            // to read the rest of the response.

            int leftToDrain = 2;
            switch (replyBuffer[3])  // Switch on type of address
            {
                case 1:  // IPv4, have first octet
                    leftToDrain += 3;
                    break;
                case 3: // Hostname, have first octet
                    leftToDrain += replyBuffer[4] - 1;
                    break;
                case 4: // IPv6, have first octet
                    leftToDrain += 14;
                    break;
                default:
                    throw new ConnectionException("SOCKS proxy returned an unknown address type.");
            }
            if (leftToDrain != 0)
            {
                _socket.Receive(replyBuffer, 0, leftToDrain, SocketFlags.None);
            }
        }


        protected virtual void createStream(Uri uri)
        {
            _socketStream = new NetworkStream(_socket);
        }

        private void applySocketOptionBool(SocketOptionLevel level, SocketOptionName name, string v)
        {
            try
            {
                switch (v)
                {
                    case "true":
                        _socket.SetSocketOption(level, name, true);
                        return;

                    case "false":
                        _socket.SetSocketOption(level, name, false);
                        return;
                }
            }
            catch (Exception e)
            {
                throw new InvalidURIException("Error setting socket properties.", e);
            }
            throw new InvalidURIException("Invalid socket option value `" + v + "'");
        }
        private void applySocketOptionInt(SocketOptionLevel level, SocketOptionName name, string v)
        {
            try
            {
                int val = Convert.ToInt32(v);
                _socket.SetSocketOption(level, name, val);
            }
            catch (Exception e)
            {
                throw new InvalidURIException("Invalid integer value in URI.", e);
            }
        }

        private void applySocketProperties(IEnumerable<KeyValuePair<string, string>> properties_)
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            if (properties_ == null) return;
            foreach (var pair in properties_)
            {
                switch (pair.Key)
                {
                    case "bind":
                        int portStart = pair.Value.IndexOf(':');
                        if (portStart == -1)
                        {
                            _socket.Bind(new IPEndPoint(IPAddress.Parse(pair.Value), 0));
                        }
                        else
                        {
                            string addr = pair.Value.Substring(0, portStart);
                            string portString = pair.Value.Substring(++portStart);
                            int port = Convert.ToInt32(portString);
                            _socket.Bind(new IPEndPoint(IPAddress.Parse(addr), port));
                        }
                        break;
                    case "tcp_keepalive":
                        applySocketOptionBool(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, pair.Value);
                        break;
                    case "tcp_sndbuf":
                        applySocketOptionInt(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, pair.Value);
                        break;
                    case "tcp_rcvbuf":
                        applySocketOptionInt(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, pair.Value);
                        break;
                    case "tcp_linger":
                        applySocketOptionInt(SocketOptionLevel.Socket, SocketOptionName.Linger, pair.Value);
                        break;
                    case "tcp_nodelay":
                        applySocketOptionBool(SocketOptionLevel.Socket, SocketOptionName.NoDelay, pair.Value);
                        break;
                    case "pretty":
                        break; // handled by the Client
                    default:
                        throw new InvalidURIException("Unrecognized URI Parameter `" +
                            pair.Key + "'");
                }
            }
        }

        private void _disconnect()
        {
            try
            {
                if (_addr != null)
                {
                    if (_readerThread != null) _readerThread.stopThread();
                    if (_socket != null)
                    {
                        _socket.Shutdown(SocketShutdown.Send);
                        _socket.Close();
                    }
                    // we'd like the next blocking operation in the
                    // reader thread to throw so it can end.  Unless,
                    // of course, *we* are the reader thread.
                    if (_readerThread != null && !_readerThread._thread.Equals(Thread.CurrentThread))
                    {
                        _readerThread._thread.Interrupt();
                        _readerThread._thread.Join();
                    }
                }
            }
            catch (Exception)
            {
                ;
            }
            this._addr = null;
        }

        public void disconnect()
        {
            lock (_lock)
            {
                _disconnecting = true;
                this._disconnect();
                _disconnecting = false;
            }
        }

        public void send(MemoryStream buf)
        {
            try
            {
                if (_filter != null) _filter.outgoing(buf.GetBuffer(), 4, (int)buf.Length - 4);
                _socketStream.Write(buf.GetBuffer(), 0, (int)buf.Length);
            }
            catch (ObjectDisposedException)
            {
                throw new DisconnectedException("socket was closed while sending message.");
            }
            catch (SocketException)
            {
                throw new DisconnectedException("Socket error while sending message.");
            }
            catch (IOException ioex)
            {
                throw new DisconnectedException("IO error while sending message.", ioex);
            }
        }

        public Socket socket()
        {
            try
            {
                return _socket;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public long writeQueueSize()
        {
            return 0;
        }

        public long readQueueSize()
        {
            return 0;
        }

        public long flush()
        {
            // Having the lock means we've sent all data in this
            //   TCP implementation.
            return 0;
        }

        public long flush(long timeout)
        {
            // Having the lock means we've sent all data in this
            //   TCP implementation.
            return 0;
        }

        internal void handleCloseEvent(int failedVersion_, String message, Exception exception)
        {
            _onDisconnect.preInvoke(failedVersion_);
            if (_readerThread == null || !_readerThread._thread.Equals(Thread.CurrentThread))
            {
                Monitor.Enter(_lock);
            }
            else
            {
                // The reader thread can cause a deadlock if send thread first grabs
                // the lock and then reader thread blocks trying to acquire it before
                // getting interrupted by the send thread.
                try
                {
                    while (!Monitor.TryEnter(_lock, 100))
                    {
                        if (_readerThread == null || _readerThread.stopped)
                            throw new DisconnectedException("Reconnect is in progress in send thread.");
                    }
                }
                catch (ThreadInterruptedException)
                {
                    throw new DisconnectedException("Reconnect already in progress in send thread.");
                }
            }
            try
            {
                if (_disconnecting)
                {
                    throw new DisconnectedException("Disconnect in progress.");
                }
                // If there's a new version of the connection available, you should use that.
                if (failedVersion_ != _connectionVersion)
                {
                    throw new RetryOperationException("A new connection is available.");
                }

                //OK, our thread is in charge of disconnecting and reconnecting. Let's do it.
                try
                {
                    _disconnect();
                }
                catch (Exception)
                {
                }


                // Create a completely new SOCKSTransport
                SOCKSTransport t = SOCKSTransport.createTransport(this._messageType);
                t.impl = this;
                try
                {
                    this._onDisconnect.invoke(t, new DisconnectedException(message, exception));
                }
                catch (Exception aex)
                {
                    throw new DisconnectedException("User's DisconnectHandler threw an exception.", aex);
                }
            }
            finally
            {
                Monitor.Exit(_lock);
            }
            // This doesn't need to be locked, because we only care if the connection version is
            // something other than what we arrived with.
            if (_connectionVersion == failedVersion_)
            {
                throw new DisconnectedException("A disconnect occurred, and no disconnect handler successfully reconnected.");
            }
            else
            {
                throw new RetryOperationException("Reconnect successful; retry the operation.");
            }
        }

        internal class SOCKSReaderThread
        {
            internal SOCKSTransportImpl transport = null;
            internal Protocol messageType = null;
            internal bool stopped = false;
            internal Thread _thread;
            internal int _currentVersion;
            internal SOCKSReaderThread(SOCKSTransportImpl transport, Protocol messageType)
            {
                this.transport = transport;
                this.messageType = messageType;
                this.stopped = false;
                _thread = new Thread(_ => run());
                _thread.Name = "AMPS C# Client Background Reader Thread";
                _thread.IsBackground = true;
                _thread.Start();
            }

            public void stopThread()
            {
                this.stopped = true;
            }

            void handleCloseEvent(Exception e)
            {
                if (!stopped)
                {
                    try
                    {
                        transport.handleCloseEvent(_currentVersion, e.Message, e);
                    }
                    catch (Exception ex)
                    {
                        if (transport._exceptionListener != null)
                        {
                            try
                            {
                                transport._exceptionListener.Invoke(ex);
                            }
                            catch (Exception)
                            {

                            }
                        }
                    }
                }
            }
            void handleTimeoutEvent()
            {
                if (!stopped)
                {
                    try
                    {
                        const string message = "No activity on socket or socket closed.";
                        transport.handleCloseEvent(_currentVersion, message, new DisconnectedException(message));
                    }
                    catch (Exception ex)
                    {
                        if (transport._exceptionListener != null)
                        {
                            try
                            {
                                transport._exceptionListener.Invoke(ex);
                            }
                            catch (Exception)
                            {

                            }
                        }
                    }
                }
            }
            void handleEndOfStream()
            {
                if (!stopped)
                {
                    try
                    {
                        const string message = "No activity on socket or socket closed.";
                        transport.handleCloseEvent(_currentVersion, message, new DisconnectedException(message));
                    }
                    catch (Exception ex)
                    {
                        if (transport._exceptionListener != null)
                        {
                            try
                            {
                                transport._exceptionListener.Invoke(ex);
                            }
                            catch (Exception)
                            {

                            }
                        }
                    }
                }
            }
            public void run()
            {
                byte[] buf = new byte[16 * 1024];
                SimpleReadBuffer readBuffer = new SimpleReadBuffer();
                ProtocolParser messageStream = this.messageType.getMessageStream();
                int readPoint = 0, parsePoint = 0;
                DateTime lastRead = DateTime.Now;
                while (true)
                {
                    if (this.stopped)
                        return;
                    _currentVersion = this.transport._connectionVersion;
                    int bytesRead = 0;
                    try
                    {
                        if (transport._socket.Poll(100, SelectMode.SelectRead))
                        {
                            bytesRead = transport._socketStream.
                                Read(buf, readPoint, buf.Length - readPoint);
                            if (bytesRead == 0)
                            {
                                handleEndOfStream();
                                return;
                            }
                            lastRead = DateTime.Now;
                        }
                        else
                        {
                            if (transport._idleAction != null) transport._idleAction.Invoke();
                            if (!stopped && transport._readTimeout > 0 && (DateTime.Now - lastRead).TotalMilliseconds > transport._readTimeout)
                            {
                                handleTimeoutEvent();
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        handleCloseEvent(ex);
                        return;
                    }

                    readPoint += bytesRead;
                    while (readPoint >= parsePoint + 4)
                    {
                        int availableBytes = readPoint - parsePoint;
                        int messageSize = Utilities.readInt32(buf, parsePoint);
                        int sizeWithFrame = 4 + messageSize;
                        int messageStartPoint = parsePoint + 4;
                        if (sizeWithFrame <= availableBytes)
                        {
                            try
                            {
                                if (transport._filter != null) transport._filter.incoming(buf, messageStartPoint, messageSize);
                                readBuffer.Reset(buf, messageStartPoint, messageSize);
                                messageStream.process(readBuffer, messageSize, this.transport._onMessage);
                            }
                            catch (Exception e)
                            {
                                if (transport._exceptionListener != null)
                                {
                                    transport._exceptionListener(e);
                                }
                            }
                            parsePoint += sizeWithFrame;
                        }
                        else
                        {
                            // enough space remaining to hold the whole message?
                            if (sizeWithFrame > (buf.Length - parsePoint))
                            {
                                // do we need to resize the buffer or just move the data
                                if (sizeWithFrame > buf.Length)
                                {
                                    // Full resize needed
                                    byte[] newBuffer = new byte[2 * buf.Length];
                                    Array.Copy(buf, parsePoint, newBuffer, 0, readPoint - parsePoint);
                                    buf = newBuffer;
                                }
                                else
                                {
                                    // just move the data down to the beginning of the buffer
                                    // MSDN documents Array.Copy as safe for overlapping regions.
                                    Array.Copy(buf, parsePoint, buf, 0, readPoint - parsePoint);
                                }
                                readPoint -= parsePoint;
                                parsePoint = 0;
                            }
                            // now we need to get more.
                            break;
                        }
                    }// end while(readpoint > parsePoint + 4)
                    // if the buffer contains no pending data to parse, start reading at 0
                    if (readPoint == parsePoint)
                    {
                        readPoint = 0;
                        parsePoint = 0;
                    }
                    else if (buf.Length - parsePoint < 32)
                    {
                        Array.Copy(buf, parsePoint, buf, 0, readPoint - parsePoint);
                        readPoint -= parsePoint;
                        parsePoint = 0;
                    }

                }// while(true)
            }//public void run()
        }
    

    
}
}
