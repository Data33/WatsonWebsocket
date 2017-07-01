﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WatsonWebsocket
{
    /// <summary>
    /// Watson websocket client.
    /// </summary>
    public class WatsonWsClient : IDisposable
    {
        #region Public-Members

        #endregion

        #region Private-Members

        private string ServerIp;
        private int ServerPort;
        private bool Debug;
        private string Url;
        private ClientWebSocket ClientWs;
        private bool Connected;
        private Func<byte[], bool> MessageReceived;
        private Func<bool> ServerConnected;
        private Func<bool> ServerDisconnected;

        private readonly SemaphoreSlim SendLock;
        private CancellationTokenSource TokenSource;
        private CancellationToken Token;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Initializes the Watson websocket client.
        /// </summary>
        public WatsonWsClient(
            string serverIp,
            int serverPort,
            bool ssl,
            bool acceptInvalidCerts,
            Func<bool> serverConnected,
            Func<bool> serverDisconnected,
            Func<byte[], bool> messageReceived,
            bool debug)
        {
            if (String.IsNullOrEmpty(serverIp)) throw new ArgumentNullException(nameof(serverIp));
            if (serverPort < 1) throw new ArgumentOutOfRangeException(nameof(serverPort));
            if (messageReceived == null) throw new ArgumentNullException(nameof(messageReceived));

            if (serverConnected != null) ServerConnected = serverConnected;
            else ServerConnected = null;

            if (serverDisconnected != null) ServerDisconnected = serverDisconnected;
            else ServerDisconnected = null;

            ServerIp = serverIp;
            ServerPort = serverPort;
            Debug = debug;
            MessageReceived = messageReceived;
            SendLock = new SemaphoreSlim(1);

            if (ssl) Url = "wss://" + ServerIp + ":" + ServerPort;
            else Url = "ws://" + ServerIp + ":" + ServerPort;

            if (acceptInvalidCerts) ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => true;
            ClientWs = new ClientWebSocket();
            ClientWs.ConnectAsync(new Uri(Url), CancellationToken.None).Wait();

            Connected = true;
            Task.Run(() => ServerConnected?.Invoke());
            
            TokenSource = new CancellationTokenSource();
            Token = TokenSource.Token;
            Task.Run(async () => await DataReceiver(Token), Token);
        }
        
        #endregion

        #region Public-Methods

        /// <summary>
        /// Tear down the client and dispose of background workers.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }
         
        /// <summary>
        /// Send data to the server asynchronously
        /// </summary>
        /// <param name="data">Byte array containing data.</param>
        /// <returns>Task with Boolean indicating if the message was sent successfully.</returns>
        public async Task<bool> SendAsync(byte[] data)
        {
            return await MessageWriteAsync(data);
        }

        /// <summary>
        /// Determine whether or not the client is connected to the server.
        /// </summary>
        /// <returns>Boolean indicating if the client is connected to the server.</returns>
        public bool IsConnected()
        {
            return Connected;
        }

        #endregion

        #region Private-Methods

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClientWs?.Abort();
                TokenSource.Cancel();
            }
        }

        private void Log(string msg)
        {
            if (Debug)
            {
                Console.WriteLine(msg);
            }
        }

        private void LogException(string method, Exception e)
        {
            Log("================================================================================");
            Log(" = Method: " + method);
            Log(" = Exception Type: " + e.GetType().ToString());
            Log(" = Exception Data: " + e.Data);
            Log(" = Inner Exception: " + e.InnerException);
            Log(" = Exception Message: " + e.Message);
            Log(" = Exception Source: " + e.Source);
            Log(" = Exception StackTrace: " + e.StackTrace);
            Log("================================================================================");
        }

        private string BytesToHex(byte[] data)
        {
            if (data == null || data.Length < 1) return "(null)";
            return BitConverter.ToString(data).Replace("-", "");
        }

        private async Task DataReceiver(CancellationToken? cancelToken = null)
        {
            try
            {
                #region Wait-for-Data

                while (true)
                {
                    cancelToken?.ThrowIfCancellationRequested();
                     
                    byte[] data = await MessageReadAsync();
                    if (data == null)
                    {
                        // no message available
                        await Task.Delay(30, Token);
                        continue;
                    }
                    else
                    {
                        if (data.Length == 1 && data[0] == 0x00)
                        {
                            break;
                        }
                    }

                    if (MessageReceived != null)
                    {
                        Task unawaited = Task.Run(() => MessageReceived(data), Token);
                    } 
                }

                #endregion
            }
            catch (OperationCanceledException oce)
            {
                Log("*** DataReceiver server disconnected (canceled): " + oce.Message);
            }
            catch (WebSocketException wse)
            {
                Log("*** DataReceiver server disconnected (websocket exception): " + wse.Message);
            }
            catch (Exception)
            {
                Log("*** DataReceiver server " + ServerIp + ":" + ServerPort + " disconnected");
            }
            finally
            {
                Connected = false;
                ServerDisconnected?.Invoke();
            }
        }
         
        private async Task<byte[]> MessageReadAsync()
        {
            /*
             *
             * Do not catch exceptions, let them get caught by the data reader
             * to destroy the connection
             *
             */

            #region Check-for-Null-Values

            if (ClientWs == null) return null;

            #endregion

            #region Variables

            byte[] contentBytes;

            #endregion

            #region Read-Data

            using (MemoryStream dataMs = new MemoryStream())
            {
                long bufferSize = 16 * 1024;
                byte[] buffer = new byte[bufferSize];
                ArraySegment<byte> bufferSegment = new ArraySegment<byte>(buffer);

                while (ClientWs.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult receiveResult = await ClientWs.ReceiveAsync(bufferSegment, CancellationToken.None);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    else
                    {
                        dataMs.Write(buffer, 0, receiveResult.Count);
                        if (receiveResult.EndOfMessage) break;
                    }
                }

                contentBytes = dataMs.ToArray();
            }

            #endregion

            #region Check-Content-Bytes

            if (contentBytes == null || contentBytes.Length < 1)
            {
                Log("*** MessageReadAsync no content read");
                return null;
            }
             
            #endregion

            return contentBytes; 
        }
         
        private async Task<bool> MessageWriteAsync(byte[] data)
        {
            bool disconnectDetected = false;

            try
            {
                #region Check-if-Connected

                if (ClientWs == null)
                {
                    Log("MessageWriteAsync client is null");
                    disconnectDetected = true;
                    return false;
                }

                #endregion
                
                #region Send-Message

                await SendLock.WaitAsync(Token);
                try
                {
                    await ClientWs.SendAsync(new ArraySegment<byte>(data, 0, data.Length), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                catch (Exception eInner)
                {
                    LogException("MessageWriteAsync", eInner);
                }
                finally
                {
                    SendLock.Release();
                }

                return true;

                #endregion
            }
            catch (ObjectDisposedException ObjDispInner)
            {
                Log("*** MessageWriteAsync disconnected (obj disposed exception): " + ObjDispInner.Message);
                disconnectDetected = true;
                return false;
            }
            catch (SocketException SockInner)
            {
                Log("*** MessageWriteAsync disconnected (socket exception): " + SockInner.Message);
                disconnectDetected = true;
                return false;
            }
            catch (InvalidOperationException InvOpInner)
            {
                Log("*** MessageWriteAsync disconnected (invalid operation exception): " + InvOpInner.Message);
                disconnectDetected = true;
                return false;
            }
            catch (IOException IOInner)
            {
                Log("*** MessageWriteAsync disconnected (IO exception): " + IOInner.Message);
                disconnectDetected = true;
                return false;
            }
            catch (Exception e)
            {
                LogException("MessageWriteAsync", e);
                disconnectDetected = true;
                return false;
            }
            finally
            {
                if (disconnectDetected)
                {
                    Connected = false;
                    Dispose();
                    if (ServerDisconnected != null)
                    {
                        Task unawaited = Task.Run(() => ServerDisconnected());
                    }
                }
            }
        }

        #endregion
    }
}
