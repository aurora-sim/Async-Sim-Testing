/*
 * Copyright (c) Contributors, http://aurora-sim.org/, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */


using Aurora.Framework.Servers.HttpServer.Implementation;

namespace Aurora.Framework.Servers.HttpServer.Interfaces
{
    /// <summary>
    ///     Interface to OpenSimulator's built in HTTP server.  Use this to register handlers (http, llsd, xmlrpc, etc.)
    ///     for given URLs.
    /// </summary>
    public interface IHttpServer
    {
        uint Port { get; }

        /// <summary>
        ///     Whether this server is running with HTTPS
        /// </summary>
        bool Secure { get; }

        /// <summary>
        ///     A well-formed URI for the host region server (namely "http://ExternalHostName:Port)
        /// </summary>
        string ServerURI { get; }

        /// <summary>
        ///     The hostname (external IP or dns name) that this server is on (without http(s)://)
        /// </summary>
        string HostName { get; set; }

        /// <summary>
        ///     The hostname (external IP or dns name) that this server is on (with http(s)://)
        /// </summary>
        string FullHostName { get; }

        /// <summary>
        ///     Add a handler for an HTTP request.
        ///     This handler can actually be invoked either as
        ///     http://hostname:port/?method=methodName
        ///     or
        ///     http://hostname:portmethodName
        ///     if the method name starts with a slash.  For example, AddHTTPHandler("/object/", ...) on a standalone region
        ///     server will register a handler that can be invoked with either
        ///     http://localhost:9000/?method=/object/
        ///     or
        ///     http://localhost:9000/object/
        ///     In addition, the handler invoked by the HTTP server for any request is the one when best matches the request
        ///     URI.  So if a handler for "/myapp/" is registered and a request for "/myapp/page" is received, then
        ///     the "/myapp/" handler is invoked if no "/myapp/page" handler exists.
        /// </summary>
        /// <param name="handler"></param>
        /// <returns>
        ///     true if the handler was successfully registered, false if a handler with the same name already existed.
        /// </returns>
        bool AddHTTPHandler(IStreamedRequestHandler handler);

        bool AddPollServiceHTTPHandler(string methodName, PollServiceEventArgs args);

        /// <summary>
        ///     Add a stream handler to the http server.  If the handler already exists, then nothing happens.
        /// </summary>
        /// <param name="handler"></param>
        void AddStreamHandler(IStreamedRequestHandler handler);

        bool AddXmlRPCHandler(string method, XmlRpcMethod handler);

        /// <summary>
        ///     Gets the XML RPC handler for given method name
        /// </summary>
        /// <param name="method">Name of the method</param>
        /// <returns>Returns null if not found</returns>
        XmlRpcMethod GetXmlRPCHandler(string method);

        void RemovePollServiceHTTPHandler(string httpMethod, string path);

        void RemoveStreamHandler(string httpMethod, string path);

        void RemoveHttpStreamHandler(string path);

        void RemoveXmlRPCHandler(string method);

        void Start();

        void Stop();
    }
}