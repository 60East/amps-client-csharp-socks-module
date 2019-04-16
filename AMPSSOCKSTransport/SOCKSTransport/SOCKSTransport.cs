////////////////////////////////////////////////////////////////////////////
//
// Copyright (c) 2012-2019 60East Technologies Inc., All Rights Reserved.
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
// Government�s use, duplication or disclosure of the computer software
// is subject to the restrictions set forth by 60East Technologies Inc..
//
////////////////////////////////////////////////////////////////////////////
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

using AMPS.Client.Exceptions;
using System.Reflection;

namespace AMPS.Client.Extensions
{
    public class SOCKSTransport : AMPS.Client.Transport
    {
        internal SOCKSTransportImpl impl = null;
        protected Protocol protocol = null;
        protected Uri uri = null;
        protected object sendLock = new object();
        protected MemoryStream sendBuffer = new MemoryStream(4096);
        protected Properties properties = null;

        public static void initSocks()
        {
            try
            {
                AMPS.Client.TransportFactory.register("socks", "AMPS.Client.Extensions.SOCKSTransport", Assembly.GetExecutingAssembly());

            }
            catch (AMPS.Client.Exceptions.TransportTypeException)
            {
                // This is thrown if the type is already registered.
                // In this case, multiple registration should be a no-op, so we just suppress the
                // exception
            }
        }

        public SOCKSTransport(Protocol msgType, Properties properties)   
        {
            this.properties = properties;
            this.protocol = msgType;
            createImpl();
        }

        public SOCKSTransport(Protocol msgType)
            : this(msgType, null)
        {
        }

        protected virtual void createImpl()
        {
            this.impl = new SOCKSTransportImpl(this.protocol, this.properties);
        }
        public static SOCKSTransport createTransport(Protocol messageType)
        {
            return new SOCKSTransport(messageType);
        }

        public void setIdleAction(Action idleAction)
        {
            impl.setIdleAction(idleAction);
        }
        public void setMessageHandler(MessageHandler ml)
        {
            this.impl.setMessageHandler(ml);
        }

        public void setDisconnectHandler(TransportDisconnectHandler dh)
        {
            this.impl.setDisconnectHandler(dh);
        }

        public void setExceptionListener(AMPS.Client.TCPTransportImpl.ExceptionListener exceptionListener)
        {
            this.impl.setExceptionListener(exceptionListener);
        }

        public void setTransportFilter(TransportFilter filter_)
        {
            impl.setTransportFilter(filter_);
        }
        public void setReadTimeout(int readTimeoutMillis_)
        {
            this.impl.setReadTimeout(readTimeoutMillis_);
        }
        public void connect(Uri uri)
        {
            this.uri = uri;
            this.impl.connect(uri);
        }

        public void close()
        {
            this.impl.disconnect();
        }

        public void disconnect()
        {
            this.impl.disconnect();
        }

        public void handleCloseEvent(int failedVersion, String message, Exception exception)
        {
            this.impl.handleCloseEvent(failedVersion, message, exception);
        }

        public void sendWithoutRetry(Message message)
        {
            lock (sendLock)
            {
                // Reserve space for a 4-byte int at the
                // beginning for the size of the message
                sendBuffer.SetLength(4);
                sendBuffer.Position = 4;

                Message.SerializationResult sr = message.serialize(sendBuffer);
                if (sr == Message.SerializationResult.BufferTooSmall)
                {
                    // Buffer was too small, let's resize and retry
                    sendBuffer = new MemoryStream(2 * sendBuffer.Capacity);
                    sendWithoutRetry(message);
                    return;
                }
                else if (sr == Message.SerializationResult.OK)
                {
                    int length = (int)sendBuffer.Position - 4;
                    Utilities.writeInt32(length, sendBuffer.GetBuffer(), 0);
                    sendBuffer.Position = 0;
                    // Write buffer to socket
                    this.impl.send(sendBuffer);
                }
            }
        }

        public void send(Message message)
        {
            lock (sendLock)
            {
                while (true)
                {
                    try
                    {
                        sendWithoutRetry(message);
                        return;
                    }
                    catch (DisconnectedException exception)
                    {
                        try
                        {
                            handleCloseEvent(getVersion(), "Disconnected", exception);
                        }
                        catch (RetryOperationException)
                        {
                            ;
                        }
                    }
                }
            }
        }

        public Message allocateMessage()
        {
            return this.protocol.allocateMessage();
        }

        public long writeQueueSize()
        {
            try
            {
                return this.impl.writeQueueSize();
            }
            catch (NullReferenceException)
            {
                throw new DisconnectedException("not connected");
            }
        }

        public long readQueueSize()
        {
            try
            {
                return this.impl.writeQueueSize();
            }
            catch (NullReferenceException)
            {
                throw new DisconnectedException("not connected");
            }
        }

        public long flush()
        {
            try
            {
                return this.impl.flush();
            }
            catch (NullReferenceException)
            {
                throw new DisconnectedException("not connected");
            }
        }

        public long flush(long timeout)
        {
            try
            {
                return this.impl.flush(timeout);
            }
            catch (NullReferenceException)
            {
                throw new DisconnectedException("not connected");
            }
        }

        public Socket socket()
        {
            try
            {
                return this.impl.socket();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public int getVersion()
        {
            return this.impl._connectionVersion;
        }

    }
}
