using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace ToolTikTokV11.Services;

/// <summary>
/// Proxy HTTP cục bộ chỉ lắng nghe 127.0.0.1. Chrome kết nối vào bridge này mà
/// không cần username/password; bridge chịu trách nhiệm xác thực với upstream.
/// Nhờ vậy Chrome mới không còn bật popup Proxy Authentication khi --load-extension
/// không được nạp nữa.
/// </summary>
internal sealed class LocalProxyBridge : IDisposable
{
    const int MaxHeaderBytes = 64 * 1024;
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(20);

    readonly ProxyLaunchProfile _config;
    readonly Action<string>? _log;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly Task _acceptLoop;
    bool _disposed;

    LocalProxyBridge(ProxyLaunchProfile config, Action<string>? log)
    {
        _config = config;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(128);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }
    public string LocalProxyUrl => $"http://127.0.0.1:{Port}";

    public static LocalProxyBridge Start(ProxyLaunchProfile config, Action<string>? log = null)
    {
        var bridge = new LocalProxyBridge(config, log);
        log?.Invoke($"bridge_started local=127.0.0.1:{bridge.Port} upstream={config.Scheme}://{config.Host}:{config.Port} auth={(config.HasCredentials ? "yes" : "no")}");
        return bridge;
    }

    async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientSafeAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                Log("accept_error " + Short(ex.Message));
                try { await Task.Delay(200, cancellationToken); } catch { }
            }
        }
    }

    async Task HandleClientSafeAsync(TcpClient client, CancellationToken bridgeToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, bridgeToken);
            }
            catch (OperationCanceledException) when (bridgeToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log("client_error " + Short(ex.Message));
                try { await SendLocalErrorAsync(client.GetStream(), "502 Bad Gateway", bridgeToken); } catch { }
            }
        }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken bridgeToken)
    {
        await using var clientStream = client.GetStream();
        var request = await ReadHeaderAsync(clientStream, HeaderTimeout, bridgeToken);
        if (request.Header.Length == 0) return;

        var headerText = Encoding.Latin1.GetString(request.Header);
        var firstLineEnd = headerText.IndexOf("\r\n", StringComparison.Ordinal);
        var firstLine = (firstLineEnd >= 0 ? headerText[..firstLineEnd] : headerText).Trim();
        var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await SendLocalErrorAsync(clientStream, "400 Bad Request", bridgeToken);
            return;
        }

        var method = parts[0];
        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseAuthority(parts[1], 443, out var targetHost, out var targetPort))
            {
                await SendLocalErrorAsync(clientStream, "400 Bad Request", bridgeToken);
                return;
            }
            await HandleConnectAsync(clientStream, targetHost, targetPort, bridgeToken);
            return;
        }

        if (!TryResolveHttpTarget(headerText, parts[1], out var host, out var port, out var pathAndQuery))
        {
            await SendLocalErrorAsync(clientStream, "400 Bad Request", bridgeToken);
            return;
        }
        await HandleHttpAsync(clientStream, request.Remainder, headerText, parts, host, port, pathAndQuery, bridgeToken);
    }

    async Task HandleConnectAsync(Stream clientStream, string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        try
        {
            if (_config.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
            {
                await using var upstream = await ConnectSocks5Async(targetHost, targetPort, cancellationToken);
                await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\nProxy-Agent: ToolTikTokLocalBridge\r\n\r\n", cancellationToken);
                await PumpBothWaysAsync(clientStream, upstream.Stream, cancellationToken);
                return;
            }

            await using var httpUpstream = await ConnectHttpProxyAsync(cancellationToken);
            var auth = BuildProxyAuthorizationHeader();
            var connectRequest = $"CONNECT {FormatAuthority(targetHost, targetPort)} HTTP/1.1\r\nHost: {FormatAuthority(targetHost, targetPort)}\r\nProxy-Connection: Keep-Alive\r\nConnection: Keep-Alive\r\n{auth}\r\n";
            await WriteAsciiAsync(httpUpstream.Stream, connectRequest, cancellationToken);
            var response = await ReadHeaderAsync(httpUpstream.Stream, HeaderTimeout, cancellationToken);
            var status = ParseStatusCode(response.Header);
            if (status is < 200 or >= 300)
            {
                Log($"upstream_connect_rejected target={targetHost}:{targetPort} status={status}");
                await SendLocalErrorAsync(clientStream, "502 Bad Gateway", cancellationToken);
                return;
            }

            await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\nProxy-Agent: ToolTikTokLocalBridge\r\n\r\n", cancellationToken);
            if (response.Remainder.Length > 0)
                await clientStream.WriteAsync(response.Remainder, cancellationToken);
            await PumpBothWaysAsync(clientStream, httpUpstream.Stream, cancellationToken);
        }
        catch (ProxyAuthenticationException ex)
        {
            Log($"auth_failed target={targetHost}:{targetPort} detail={Short(ex.Message)}");
            await SendLocalErrorAsync(clientStream, "502 Bad Gateway", cancellationToken);
        }
        catch (Exception ex)
        {
            Log($"connect_failed target={targetHost}:{targetPort} detail={Short(ex.Message)}");
            await SendLocalErrorAsync(clientStream, "502 Bad Gateway", cancellationToken);
        }
    }

    async Task HandleHttpAsync(
        Stream clientStream,
        byte[] remainder,
        string originalHeader,
        string[] firstLineParts,
        string targetHost,
        int targetPort,
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_config.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
            {
                await using var upstream = await ConnectSocks5Async(targetHost, targetPort, cancellationToken);
                var firstLine = $"{firstLineParts[0]} {pathAndQuery} {firstLineParts[2]}";
                var rewritten = RewriteRequestHeader(originalHeader, firstLine, includeProxyAuthorization: false);
                await WriteAsciiAsync(upstream.Stream, rewritten, cancellationToken);
                if (remainder.Length > 0) await upstream.Stream.WriteAsync(remainder, cancellationToken);
                await PumpBothWaysAsync(clientStream, upstream.Stream, cancellationToken);
                return;
            }

            await using var httpUpstream = await ConnectHttpProxyAsync(cancellationToken);
            var rebuilt = RewriteRequestHeader(originalHeader, firstLineOverride: null, includeProxyAuthorization: true);
            await WriteAsciiAsync(httpUpstream.Stream, rebuilt, cancellationToken);
            if (remainder.Length > 0) await httpUpstream.Stream.WriteAsync(remainder, cancellationToken);
            await PumpBothWaysAsync(clientStream, httpUpstream.Stream, cancellationToken);
        }
        catch (ProxyAuthenticationException ex)
        {
            Log($"auth_failed http_target={targetHost}:{targetPort} detail={Short(ex.Message)}");
            await SendLocalErrorAsync(clientStream, "502 Bad Gateway", cancellationToken);
        }
        catch (Exception ex)
        {
            Log($"http_failed target={targetHost}:{targetPort} detail={Short(ex.Message)}");
            await SendLocalErrorAsync(clientStream, "502 Bad Gateway", cancellationToken);
        }
    }

    async Task<OwnedStream> ConnectHttpProxyAsync(CancellationToken cancellationToken)
    {
        var tcp = await ConnectTcpAsync(_config.Host, _config.Port, cancellationToken);
        Stream stream = tcp.GetStream();
        if (_config.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectTimeout);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _config.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, timeout.Token);
                stream = ssl;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
        return new OwnedStream(tcp, stream);
    }

    async Task<OwnedStream> ConnectSocks5Async(string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        var tcp = await ConnectTcpAsync(_config.Host, _config.Port, cancellationToken);
        var stream = tcp.GetStream();
        try
        {
            var methods = _config.HasCredentials ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 };
            await stream.WriteAsync(methods, cancellationToken);
            var hello = new byte[2];
            await ReadExactlyAsync(stream, hello, cancellationToken);
            if (hello[0] != 0x05 || hello[1] == 0xFF)
                throw new ProxyAuthenticationException("SOCKS5 không chấp nhận phương thức xác thực.");

            if (hello[1] == 0x02)
            {
                var user = Encoding.UTF8.GetBytes(_config.Username ?? "");
                var pass = Encoding.UTF8.GetBytes(_config.Password ?? "");
                if (user.Length is 0 or > 255 || pass.Length > 255)
                    throw new ProxyAuthenticationException("Username/password SOCKS5 không hợp lệ.");
                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 0x01;
                auth[1] = (byte)user.Length;
                Buffer.BlockCopy(user, 0, auth, 2, user.Length);
                auth[2 + user.Length] = (byte)pass.Length;
                Buffer.BlockCopy(pass, 0, auth, 3 + user.Length, pass.Length);
                await stream.WriteAsync(auth, cancellationToken);
                var authReply = new byte[2];
                await ReadExactlyAsync(stream, authReply, cancellationToken);
                if (authReply[1] != 0x00)
                    throw new ProxyAuthenticationException("SOCKS5 từ chối username/password.");
            }
            else if (hello[1] != 0x00)
            {
                throw new ProxyAuthenticationException($"SOCKS5 trả phương thức xác thực không hỗ trợ: {hello[1]}.");
            }

            var request = BuildSocksConnectRequest(targetHost, targetPort);
            await stream.WriteAsync(request, cancellationToken);
            var replyHead = new byte[4];
            await ReadExactlyAsync(stream, replyHead, cancellationToken);
            if (replyHead[0] != 0x05 || replyHead[1] != 0x00)
                throw new IOException($"SOCKS5 CONNECT thất bại, mã={replyHead[1]}.");
            await ConsumeSocksAddressAsync(stream, replyHead[3], cancellationToken);
            var portBytes = new byte[2];
            await ReadExactlyAsync(stream, portBytes, cancellationToken);
            return new OwnedStream(tcp, stream);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    async Task<TcpClient> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(host, port, timeout.Token);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    string RewriteRequestHeader(string originalHeader, string? firstLineOverride, bool includeProxyAuthorization)
    {
        var lines = originalHeader.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var sb = new StringBuilder(originalHeader.Length + 128);
        sb.Append(firstLineOverride ?? lines[0]).Append("\r\n");
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            var name = colon > 0 ? line[..colon].Trim() : "";
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(line).Append("\r\n");
        }
        if (includeProxyAuthorization && _config.HasCredentials)
            sb.Append(BuildProxyAuthorizationHeader());
        sb.Append("Proxy-Connection: close\r\nConnection: close\r\n\r\n");
        return sb.ToString();
    }

    string BuildProxyAuthorizationHeader()
    {
        if (!_config.HasCredentials) return "";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes((_config.Username ?? "") + ":" + (_config.Password ?? "")));
        return "Proxy-Authorization: Basic " + token + "\r\n";
    }

    static byte[] BuildSocksConnectRequest(string host, int port)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x05);
        ms.WriteByte(0x01);
        ms.WriteByte(0x00);
        if (IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                ms.WriteByte(0x01);
            else
                ms.WriteByte(0x04);
            ms.Write(bytes, 0, bytes.Length);
        }
        else
        {
            var bytes = Encoding.ASCII.GetBytes(host);
            if (bytes.Length is 0 or > 255) throw new IOException("Tên host SOCKS5 không hợp lệ.");
            ms.WriteByte(0x03);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte((byte)((port >> 8) & 0xFF));
        ms.WriteByte((byte)(port & 0xFF));
        return ms.ToArray();
    }

    static async Task ConsumeSocksAddressAsync(Stream stream, byte atyp, CancellationToken cancellationToken)
    {
        int length = atyp switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => -1,
            _ => throw new IOException("SOCKS5 trả kiểu địa chỉ không hợp lệ.")
        };
        if (length == -1)
        {
            var len = new byte[1];
            await ReadExactlyAsync(stream, len, cancellationToken);
            length = len[0];
        }
        if (length > 0)
        {
            var buffer = new byte[length];
            await ReadExactlyAsync(stream, buffer, cancellationToken);
        }
    }

    static bool TryResolveHttpTarget(string headerText, string requestTarget, out string host, out int port, out string pathAndQuery)
    {
        host = "";
        port = 80;
        pathAndQuery = "/";
        if (Uri.TryCreate(requestTarget, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            port = uri.IsDefaultPort ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : uri.Port;
            pathAndQuery = string.IsNullOrWhiteSpace(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            return true;
        }

        var hostHeader = GetHeaderValue(headerText, "Host");
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;
        if (!TryParseAuthority(hostHeader, 80, out host, out port)) return false;
        pathAndQuery = requestTarget.StartsWith('/') ? requestTarget : "/" + requestTarget;
        return true;
    }

    static string GetHeaderValue(string headerText, string name)
    {
        foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.None).Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return "";
    }

    static bool TryParseAuthority(string authority, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        authority = (authority ?? "").Trim();
        if (authority.Length == 0) return false;

        // IPv6 dạng [::1]:443. Không dùng Uri.IsDefaultPort ở đây vì CONNECT
        // example.com:80 vẫn phải giữ đúng 80 dù scheme tạm dùng để parse là http.
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close <= 1) return false;
            host = authority[1..close];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':') || !int.TryParse(rest[1..], out port)) return false;
            }
            return port is > 0 and <= 65535;
        }

        var lastColon = authority.LastIndexOf(':');
        if (lastColon > 0 && authority.IndexOf(':') == lastColon)
        {
            host = authority[..lastColon].Trim();
            if (!int.TryParse(authority[(lastColon + 1)..], out port)) return false;
        }
        else
        {
            host = authority;
        }
        return host.Length > 0 && port is > 0 and <= 65535;
    }

    static string FormatAuthority(string host, int port)
        => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";

    static int ParseStatusCode(byte[] header)
    {
        try
        {
            var text = Encoding.Latin1.GetString(header);
            var first = text.Split(new[] { "\r\n" }, 2, StringSplitOptions.None)[0];
            var parts = first.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && int.TryParse(parts[1], out var status) ? status : 0;
        }
        catch { return 0; }
    }

    static async Task<HeaderBlock> ReadHeaderAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        var searchFrom = 0;
        while (ms.Length < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token);
            if (read <= 0) break;
            ms.Write(buffer, 0, read);
            var data = ms.GetBuffer();
            var length = checked((int)ms.Length);
            var idx = IndexOfHeaderTerminator(data, Math.Max(0, searchFrom - 3), length);
            if (idx >= 0)
            {
                var headerLength = idx + 4;
                var header = new byte[headerLength];
                Buffer.BlockCopy(data, 0, header, 0, headerLength);
                var remainderLength = length - headerLength;
                var remainder = new byte[remainderLength];
                if (remainderLength > 0) Buffer.BlockCopy(data, headerLength, remainder, 0, remainderLength);
                return new HeaderBlock(header, remainder);
            }
            searchFrom = length;
        }
        if (ms.Length == 0) return new HeaderBlock([], []);
        throw new IOException("HTTP header vượt giới hạn hoặc chưa hoàn chỉnh.");
    }

    static int IndexOfHeaderTerminator(byte[] data, int start, int length)
    {
        for (var i = start; i <= length - 4; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        }
        return -1;
    }

    static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read <= 0) throw new IOException("Kết nối upstream đóng sớm.");
            offset += read;
        }
    }

    static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    static async Task SendLocalErrorAsync(Stream stream, string status, CancellationToken cancellationToken)
    {
        var body = "Proxy upstream không khả dụng.";
        var response = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    static async Task PumpBothWaysAsync(Stream client, Stream upstream, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var a = PumpAsync(client, upstream, linked.Token);
        var b = PumpAsync(upstream, client, linked.Token);
        await Task.WhenAny(a, b);
        linked.Cancel();
        try { await Task.WhenAll(a, b); } catch (OperationCanceledException) { } catch (IOException) { }
    }

    static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
    }

    void Log(string detail)
    {
        _log?.Invoke(detail);
    }

    static string Short(string? value)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 220 ? text : text[..220];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
        Log("bridge_stopped");
    }

    sealed class OwnedStream : IAsyncDisposable
    {
        readonly TcpClient _client;
        public OwnedStream(TcpClient client, Stream stream)
        {
            _client = client;
            Stream = stream;
        }
        public Stream Stream { get; }
        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync(); } catch { }
            try { _client.Dispose(); } catch { }
        }
    }

    sealed record HeaderBlock(byte[] Header, byte[] Remainder);
    sealed class ProxyAuthenticationException(string message) : IOException(message);
}
