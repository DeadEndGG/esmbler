namespace Esmbler.Hosting;

/// <summary>
/// Container health probe without extra tools in the runtime image: `dotnet Host.dll healthcheck [path]`
/// requests http://localhost:8080{path} and exits 0 on success.
/// </summary>
public static class HealthCheckCommand
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "healthcheck")
        {
            return null;
        }

        var path = args.Length > 1 ? args[1] : "/health/live";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync(new Uri("http://localhost:8080" + path));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
