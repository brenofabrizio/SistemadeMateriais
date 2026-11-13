using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SistemaMateriais.Web.Infrastructure;

public static class ForwardedHeaderConfiguration
{
    public static IPAddress TrustedProxyAddress => IPAddress.Loopback;

    public static void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(TrustedProxyAddress);
    }
}
