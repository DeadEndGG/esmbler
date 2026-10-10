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

        /// <summary>Accept the public development signing key (docker-compose.yml). Never set outside local development.</summary>
        public bool AllowDevelopmentKey { get; set; }

        /// <summary>Login attempts allowed per client address per minute.</summary>
        public int LoginAttemptsPerMinute { get; set; } = 30;

        /// <summary>
        /// Use X-Forwarded-For/-Proto from the reverse proxy in front of the API (the web container's nginx in Compose),
        /// so rate limits and cookies see the real client. Enable only when the API is reachable through that proxy alone.
        /// </summary>
        public bool TrustForwardedHeaders { get; set; }

        /// <summary>Failed sign-ins per account within 15 minutes before the account is temporarily locked.</summary>
        public int FailedLoginsPerAccount { get; set; } = 8;

        /// <summary>Mark the refresh cookie Secure even on plain HTTP (browsers accept Secure cookies on localhost).</summary>
        public bool SecureCookies { get; set; } = true;
    }
}
