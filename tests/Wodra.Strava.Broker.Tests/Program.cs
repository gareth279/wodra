using Wodra.Strava.Broker;

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"Failed: {name}");
    Console.WriteLine($"PASS {name}");
}

Check(LoopbackRedirectPolicy.IsAllowed("http://127.0.0.1:43127/strava/callback"), "IPv4 loopback callback");
Check(LoopbackRedirectPolicy.IsAllowed("http://localhost:43127/strava/callback"), "localhost callback");
Check(LoopbackRedirectPolicy.IsAllowed("http://[::1]:43127/strava/callback"), "IPv6 loopback callback");
Check(!LoopbackRedirectPolicy.IsAllowed("https://127.0.0.1:43127/strava/callback"), "redirect must match native loopback HTTP scheme");
Check(!LoopbackRedirectPolicy.IsAllowed("http://example.com:43127/strava/callback"), "remote callback rejected");
Check(!LoopbackRedirectPolicy.IsAllowed("http://127.0.0.1:43127/other"), "unexpected callback path rejected");
Check(!LoopbackRedirectPolicy.IsAllowed("http://127.0.0.1:43127/strava/callback?next=https://example.com"), "query parameters rejected");
Check(!LoopbackRedirectPolicy.IsAllowed("http://user@127.0.0.1:43127/strava/callback"), "userinfo rejected");
