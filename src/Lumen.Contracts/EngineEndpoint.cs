using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using Grpc.Net.Client;

namespace Lumen.Contracts;

/// <summary>
/// Where the local engine listens and how to reach it: a per-user named pipe on Windows, a Unix domain socket
/// elsewhere (TDD §47). The name carries the contract version so an old engine is never reused by a new client.
/// </summary>
public static class EngineEndpoint
{
    public const string ContractVersion = "v1";

    public static string DefaultName =>
        $"lumen-engine-{ContractVersion}-{Sanitize(Environment.UserName)}";

    public static string UnixSocketPath(string name) => Path.Combine(Path.GetTempPath(), $"{name}.sock");

    public static GrpcChannel CreateChannel(string name)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = OperatingSystem.IsWindows()
                ? async (_, cancellationToken) =>
                {
                    // CurrentUserOnly: refuse to talk to a pipe server owned by another user.
                    var pipe = new NamedPipeClientStream(
                        ".",
                        name,
                        PipeDirection.InOut,
                        PipeOptions.WriteThrough | PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                        TokenImpersonationLevel.Anonymous);
                    try
                    {
                        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        return pipe;
                    }
                    catch
                    {
                        await pipe.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
                : async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(UnixSocketPath(name)), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = 64 * 1024 * 1024,
            DisposeHttpClient = true,
        });
    }

    private static string Sanitize(string value) =>
        new([.. value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);
}
