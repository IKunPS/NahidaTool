using System;
using System.Collections.Generic;

namespace NahidaTool.Eavesdrop;

internal static class ProxyHostPolicy
{
    internal static bool Matches(string host, string domain)
    {
        host = host.TrimEnd('.');
        domain = domain.Trim('.');
        return domain.Length > 0 && (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ShouldIntercept(string host, HashSet<string> intercepted, HashSet<string> passthrough)
    {
        if (passthrough.Contains(host.TrimEnd('.')))
            return false;
        foreach (string domain in intercepted)
            if (Matches(host, domain)) return true;
        return false;
    }
}
