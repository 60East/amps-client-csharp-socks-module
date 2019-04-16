# amps-client-csharp-socks-module

C# SOCKS5 Connection Transport

This auxilliary code adds the capability for an AMPS application to
connect to AMPS through a SOCKS5 proxy.

The implementation is very simple, and has the following limitations:

* Authentication is not supported
* The server and proxy address must be provided in the connection string

## Building the Transport

There are two options for using this transport:

To build a standalone assembly:

1. Load the provided solution and project.
2. Update the reference to the AMPS.Client DLL to a 5.3 or later version of
the AMPS C# client.
3. Build the solution

The assembly is now ready to use and distribute.

To include the Transport directly in your project:

1. Add the SOCKSTransport.cs and SOCKSTransportImpl.cs files to
   a project that references the AMPS Client (5.3 or later).

2. Build the project. The SOCKS transport will be included
   and ready to use.

## Using the Transport

To use the SOCKS transport, two things are necessary:

1. Initialize the transport in the application

2. In the connection strings for connections that
   should connect through a SOCKS5 proxy, use 
   `socks` for the transport part of the connection
   string, and provide the address and port of the
   proxy in the `proxy` parameter to the connection string.

### Initializing the Transport

To initialize the transport, your application should call the static
method `initSocks()` on the SOCKS transport, as in the following snippet:

```
SOCKSTransport.initSocks();  // Set up the SOCKS transport with the AMPS Client
```

You must initialize the transport for the AMPS Client to recognize the
`socks` transport in a connection string.

Once the transport is initialized, you can use connection strings that
use the `socks` transport. For example, to connect to an AMPS server on
the host `internal-access-name` on port `9111` by way of the
SOCKS5 proxy running on the server `mysocksproxy.example.org` at port `1080`,
you would use the following connection string:

```
socks://internal-access-name:9111/amps/json?proxy=mysocksproxy.example.org:1080
```

For sample purposes, this connection string uses the AMPS protocol and the JSON
message type. All AMPS protocols are supported.
