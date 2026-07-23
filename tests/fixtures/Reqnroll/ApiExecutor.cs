namespace Fixture.Reqnroll
{
    // A CUSTOM HTTP wrapper with no known client library — the shape many real suites use. Endpoint
    // extraction must catch calls to it via the generic verb-name fallback (Get/Post/… + a strictly
    // route-like argument), not via any library-specific knowledge. Method count 2.
    public static class ApiExecutor
    {
        public static string Get(string route) => route;

        public static string Post(string route, object body) => route;
    }
}
