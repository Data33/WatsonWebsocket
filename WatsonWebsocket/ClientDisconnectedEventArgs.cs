﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net;

namespace WatsonWebsocket
{
    /// <summary>
    /// Event arguments for when a client disconnects from the server.
    /// </summary>
    public class ClientDisconnectedEventArgs : EventArgs
    {
        internal ClientDisconnectedEventArgs(string ipPort)
        {
            IpPort = ipPort;
        }

        /// <summary>
        /// The IP:port of the client.
        /// </summary>
        public string IpPort { get; } 
    }
}