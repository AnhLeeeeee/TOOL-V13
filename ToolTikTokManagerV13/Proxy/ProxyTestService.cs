using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace ToolTikTokManagerV13.Proxy;

public sealed class ProxyTestService
{
    static readonly Uri[] IpEndpoints =
    [
        new("https://api.ipify.org"),
        new("https://icanhazip.com"),
        new("https://www.cloudflare.com/cdn-cgi/trace")
    ];

    public async Task<ProxyTestResult> TestAsync(ProxyEndpoint proxy, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = BuildWebProxy(proxy),
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(9)
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));

            string lastError = "";
            foreach (var endpoint in IpEndpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                    {
                        watch.Stop();
                        return new ProxyTestResult(ProxyHealthState.AuthError, "", watch.ElapsedMilliseconds, "Proxy yêu cầu/xác thực sai tài khoản mật khẩu (HTTP 407)." );
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = $"{endpoint.Host}: HTTP {(int)response.StatusCode}";
                        continue;
                    }

                    var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                    var ip = ParseExitIp(body);
                    if (string.IsNullOrWhiteSpace(ip))
                    {
                        lastError = $"{endpoint.Host}: không đọc được IP đầu ra.";
                        continue;
                    }

                    watch.Stop();
                    var health = watch.ElapsedMilliseconds >= 3000 ? ProxyHealthState.Slow : ProxyHealthState.Good;
                    return new ProxyTestResult(health, ip, watch.ElapsedMilliseconds, "");
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex.Message;
                    if (LooksLikeAuthError(ex.Message))
                    {
                        watch.Stop();
                        return new ProxyTestResult(ProxyHealthState.AuthError, "", watch.ElapsedMilliseconds, Short(ex.Message));
                    }
                }
            }

            watch.Stop();
            return new ProxyTestResult(ProxyHealthState.Dead, "", watch.ElapsedMilliseconds, Short(lastError.Length == 0 ? "Không kết nối được qua proxy." : lastError));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new ProxyTestResult(ProxyHealthState.Timeout, "", watch.ElapsedMilliseconds, "Timeout khi kiểm tra proxy.");
        }
        catch (Exception ex)
        {
            watch.Stop();
            var state = LooksLikeAuthError(ex.Message) ? ProxyHealthState.AuthError : ProxyHealthState.Error;
            return new ProxyTestResult(state, "", watch.ElapsedMilliseconds, Short(ex.Message));
        }
    }

    static IWebProxy BuildWebProxy(ProxyEndpoint endpoint)
    {
        var scheme = endpoint.Protocol switch
        {
            ProxyProtocol.Https => "https",
            ProxyProtocol.Socks5 => "socks5",
            _ => "http"
        };
        var proxy = new WebProxy(new Uri($"{scheme}://{endpoint.Host}:{endpoint.Port}"));
        if (endpoint.HasCredentials)
            proxy.Credentials = new NetworkCredential(endpoint.Username, endpoint.Password);
        return proxy;
    }

    static string ParseExitIp(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        foreach (var rawLine in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))
                line = line[3..].Trim();
            if (IPAddress.TryParse(line, out var ip)) return ip.ToString();
        }
        if (IPAddress.TryParse(body.Trim(), out var direct)) return direct.ToString();
        return "";
    }

    static bool LooksLikeAuthError(string? message)
    {
        var text = (message ?? "").ToLowerInvariant();
        return text.Contains("407")
               || text.Contains("proxy authentication")
               || text.Contains("authentication required")
               || text.Contains("credentials");
    }

    static string Short(string? message)
    {
        var text = (message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 220 ? text : text[..220];
    }
}
