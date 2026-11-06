using System.Net;
using IPNetwork = System.Net.IPNetwork;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Solvelt.Api.Options;

namespace Solvelt.Api.Security;

/// <summary>
/// Which peers may set X-Forwarded-For / X-Forwarded-Proto. Rules: nobody unless configured; CIDR ranges allowed;
/// never a catch-all; and only the single nearest hop is consumed (ForwardLimit 1), so a client cannot inject an earlier hop.
/// </summary>
public static class ForwardedHeadersSetup
{
    /// <summary>
    /// The private and link-local ranges an App Service front end reaches the app container from (RFC 1918, 169.254.0.0/16 link-local, IPv6 ULA and link-local).
    /// They are not routable from the public internet, so a client outside the platform cannot be a peer in them.
    /// </summary>
    public static readonly string[] PlatformRanges = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"];
    private const int MinIPv4Prefix = 8, MinIPv6Prefix = 16;

    public sealed record Trust(bool Enabled, IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks);

    public static Trust Resolve(AppOptions o)
    {
        var proxies = new List<IPAddress>(); var nets = new List<IPNetwork>();
        foreach (var raw in o.TrustedProxies)
        {
            var entry = (raw ?? "").Trim();
            if (entry.Length == 0) throw new InvalidOperationException("App:TrustedProxies contains an empty entry.");
            if (entry.Contains('/'))
            {
                if (!IPNetwork.TryParse(entry, out var net)) throw new InvalidOperationException($"App:TrustedProxies entry '{entry}' is not a valid CIDR range.");
                var min = net.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? MinIPv4Prefix : MinIPv6Prefix;
                if (net.PrefixLength < min)
                    throw new InvalidOperationException($"App:TrustedProxies entry '{entry}' is too broad: it would trust (almost) every client. Use a range of /{min} or narrower.");
                nets.Add(net);
            }
            else
            {
                if (!IPAddress.TryParse(entry, out var ip)) throw new InvalidOperationException($"App:TrustedProxies entry '{entry}' is not a valid IP address or CIDR range.");
                proxies.Add(ip);
            }
        }
        if (o.TrustPlatformProxy) foreach (var r in PlatformRanges) nets.Add(IPNetwork.Parse(r));
        var enabled = proxies.Count + nets.Count > 0;
        if (o.TrustPlatformProxy && !enabled) throw new InvalidOperationException("App:TrustPlatformProxy is on but no proxy range is trusted.");
        return new(enabled, proxies, nets);
    }

    /// <summary>
    /// Removes the forwarded-headers middleware the host adds when ASPNETCORE_FORWARDEDHEADERS_ENABLED=true (an IStartupFilter that would run in front of the app's own
    /// UseForwardedHeaders and consume a second hop). Exactly one forwarded-headers middleware, with the options from <see cref="Apply"/>, then runs.
    /// </summary>
    public static void RemoveHostMiddleware(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(Microsoft.AspNetCore.Hosting.IStartupFilter) && d.ImplementationType?.Name == "ForwardedHeadersStartupFilter").ToList())
            services.Remove(d);
    }

    /// <summary>Fills the options. Called from DI configuration so it also overrides the trust-everyone defaults that ASPNETCORE_FORWARDEDHEADERS_ENABLED=true would install.</summary>
    public static void Apply(ForwardedHeadersOptions fh, Trust t)
    {
        fh.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        fh.ForwardLimit = 1;
        fh.KnownProxies.Clear(); fh.KnownIPNetworks.Clear();   // also drops the default loopback trust
        foreach (var p in t.Proxies) fh.KnownProxies.Add(p);
        foreach (var n in t.Networks) fh.KnownIPNetworks.Add(n);
    }
}
