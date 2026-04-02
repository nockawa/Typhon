using System;
using System.Net;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Typhon.Client.Tests")]

namespace Typhon.Client;

/// <summary>
/// Factory for creating connections to a Typhon server.
/// </summary>
public static class TyphonClient
{
    /// <summary>
    /// Connect to a Typhon server at the specified host and port.
    /// </summary>
    /// <param name="host">Server hostname or IP address.</param>
    /// <param name="port">TCP port. Default: 9000.</param>
    /// <param name="options">Connection options. Null uses defaults.</param>
    /// <returns>A connected <see cref="TyphonConnection"/>. Dispose when done.</returns>
    /// <exception cref="System.Net.Sockets.SocketException">TCP connection failed.</exception>
    public static TyphonConnection Connect(string host, int port = 9000, TyphonConnectionOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        var addresses = Dns.GetHostAddresses(host);
        if (addresses.Length == 0)
        {
            throw new ArgumentException($"Could not resolve host: {host}", nameof(host));
        }

        return Connect(new IPEndPoint(addresses[0], port), options);
    }

    /// <summary>
    /// Connect to a Typhon server at the specified endpoint.
    /// </summary>
    /// <param name="endpoint">Server IP endpoint.</param>
    /// <param name="options">Connection options. Null uses defaults.</param>
    /// <returns>A connected <see cref="TyphonConnection"/>. Dispose when done.</returns>
    /// <exception cref="System.Net.Sockets.SocketException">TCP connection failed.</exception>
    public static TyphonConnection Connect(IPEndPoint endpoint, TyphonConnectionOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var connection = new TyphonConnection(endpoint, options);
        try
        {
            connection.Connect();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
