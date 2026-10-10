namespace Esmbler.Hosting;

/// <summary>Bound from configuration section "Esmbler" (environment variables Esmbler__*).</summary>
public sealed class PlatformOptions
{
    public const string Section = "Esmbler";

    public DatabaseSettings Database { get; set; } = new();

    public OpenFgaSettings OpenFga { get; set; } = new();

    public AuthSettings Auth { get; set; } = new();

    /// <summary>Apply migrations at startup (API host only).</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Provision the demo tenants (acme, globex) when the database is empty.</summary>
    public bool SeedDemoData { get; set; }

    public sealed class DatabaseSettings
    {
        public string App { get; set; } = "";

        public string? Migrator { get; set; }
    }

    public sealed class OpenFgaSettings
    {
        public string ApiUrl { get; set; } = "http://localhost:8081";

        public int TimeoutMs { get; set; } = 2000;
    }

    public sealed class AuthSettings
    {
        /// <summary>HMAC signing key (≥ 32 bytes). Production: from a secret store (human action).</summary>
        public string SigningKey { get; set; } = "";

        public string Issuer { get; set; } = "esmbler";

        public int AccessTokenMinutes { get; set; } = 15;

        public int AgentTokenMinutes { get; set; } = 30;
    }
}
