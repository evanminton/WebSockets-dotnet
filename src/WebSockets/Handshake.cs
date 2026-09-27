using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace WebSockets;

/// <summary>The outcome of a successful opening handshake.</summary>
internal sealed record HandshakeResult(
    System.Net.WebSockets.WebSocket Socket,
    ConnectionStream Stream,
    string Protocol,
    string Extensions);

/// <summary>
/// The opening handshake: "establish a WebSocket connection" from the standard, with the response checks of
/// RFC 6455 section 4.1 and the permessage-deflate negotiation of RFC 7692.
/// </summary>
internal static class Handshake
{
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const string DeflateOffer = "permessage-deflate; client_max_window_bits";

    public static async Task<HandshakeResult> ConnectAsync(
        Uri url, string[] protocols, WebSocketOptions options, HttpMessageInvoker invoker, CancellationToken cancellationToken)
    {
        var httpUrl = new UriBuilder(url) { Scheme = url.Scheme == "wss" ? "https" : "http", Port = url.IsDefaultPort ? -1 : url.Port }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, httpUrl)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        string key = Convert.ToBase64String(nonce);
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", key);
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        if (protocols.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Protocol", string.Join(", ", protocols));
        }

        if (options.PerMessageDeflate)
        {
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Extensions", DeflateOffer);
        }

        if (options.Origin is { } origin)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        foreach ((string name, string value) in options.RequestHeaders)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.SwitchingProtocols)
            {
                throw new WebSocketException($"The server responded with status {(int)response.StatusCode} instead of 101.");
            }

            if (!HeaderEquals(response, "Upgrade", "websocket") || !HeaderContainsToken(response, "Connection", "upgrade"))
            {
                throw new WebSocketException("The response is missing the Upgrade or Connection header.");
            }

            string expectedAccept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));
            if (Header(response, "Sec-WebSocket-Accept") != expectedAccept)
            {
                throw new WebSocketException("The Sec-WebSocket-Accept header is missing or wrong.");
            }

            string? protocol = Header(response, "Sec-WebSocket-Protocol");
            if (protocols.Length > 0 && string.IsNullOrEmpty(protocol))
            {
                throw new WebSocketException("The server did not select any of the requested subprotocols.");
            }

            if (!string.IsNullOrEmpty(protocol) && !protocols.Contains(protocol, StringComparer.Ordinal))
            {
                throw new WebSocketException($"The server selected the subprotocol '{protocol}', which was not requested.");
            }

            string extensions = Header(response, "Sec-WebSocket-Extensions") ?? "";
            WebSocketDeflateOptions? deflate = ParseExtensions(extensions, options.PerMessageDeflate);

            Stream raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var stream = new ConnectionStream(raw);
            var socket = System.Net.WebSockets.WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = false,
                SubProtocol = string.IsNullOrEmpty(protocol) ? null : protocol,
                KeepAliveInterval = options.KeepAliveInterval,
                DangerousDeflateOptions = deflate,
            });
            return new HandshakeResult(socket, stream, protocol ?? "", extensions);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Validates the server's extension list and turns an accepted permessage-deflate into deflate options.</summary>
    internal static WebSocketDeflateOptions? ParseExtensions(string header, bool offered)
    {
        WebSocketDeflateOptions? deflate = null;
        foreach (string extension in SplitList(header, ','))
        {
            string[] parts = SplitList(extension, ';').ToArray();
            if (!offered || !parts[0].Equals("permessage-deflate", StringComparison.OrdinalIgnoreCase) || deflate is not null)
            {
                throw new WebSocketException($"The server selected the extension '{parts[0]}', which was not offered.");
            }

            deflate = new WebSocketDeflateOptions();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string parameter in parts.Skip(1))
            {
                int equals = parameter.IndexOf('=');
                string name = (equals < 0 ? parameter : parameter[..equals]).Trim();
                string? value = equals < 0 ? null : parameter[(equals + 1)..].Trim().Trim('"');
                if (!seen.Add(name))
                {
                    throw new WebSocketException($"The permessage-deflate parameter '{name}' is repeated.");
                }

                switch (name.ToLowerInvariant())
                {
                    case "client_no_context_takeover" when value is null:
                        deflate.ClientContextTakeover = false;
                        break;
                    case "server_no_context_takeover" when value is null:
                        deflate.ServerContextTakeover = false;
                        break;
                    case "client_max_window_bits":
                        deflate.ClientMaxWindowBits = WindowBits(value);
                        break;
                    case "server_max_window_bits":
                        deflate.ServerMaxWindowBits = WindowBits(value);
                        break;
                    default:
                        throw new WebSocketException($"The permessage-deflate parameter '{parameter}' is not valid.");
                }
            }
        }

        return deflate;
    }

    // RFC 7692 allows 8 to 15; zlib (and so .NET) cannot produce 8-bit windows, so 8 is widened to 9 as zlib itself does.
    private static int WindowBits(string? value)
    {
        if (value is null || !int.TryParse(value, out int bits) || bits < 8 || bits > 15)
        {
            throw new WebSocketException($"The permessage-deflate window size '{value}' is not valid.");
        }

        return Math.Max(bits, 9);
    }

    private static IEnumerable<string> SplitList(string value, char separator) =>
        value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(", ", values) : null;

    private static bool HeaderEquals(HttpResponseMessage response, string name, string expected) =>
        string.Equals(Header(response, name)?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool HeaderContainsToken(HttpResponseMessage response, string name, string token) =>
        Header(response, name) is { } value && SplitList(value, ',').Contains(token, StringComparer.OrdinalIgnoreCase);
}
