namespace Esmbler.Hosting;

/// <summary>Bound from configuration section "Esmbler" (environment variables Esmbler__*).</summary>
public sealed class PlatformOptions
{
    public const string Section = "Esmbler";

    public DatabaseSettings Database { get; set; } = new();

    public OpenFgaSettings OpenFga { get; set; } = new();

    public AuthSettings Auth { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();

    public McpSettings Mcp { get; set; } = new();

    /// <summary>Apply migrations at startup (API host only).</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Provision the demo tenants (acme, globex) when the database is empty.</summary>
    public bool SeedDemoData { get; set; }

    /// <summary>Dashboard, metric and group-count requests allowed per person per minute.</summary>
    public int MetricRequestsPerMinute { get; set; } = 120;

    /// <summary>
    /// Whether signed-in people may create workspaces from a starter (REQ-WS-001). Off by default: every created
    /// workspace is a new tenant, so operators turn self-service on deliberately (threat model T-72).
    /// </summary>
    public bool AllowWorkspaceCreation { get; set; }

    /// <summary>Workspaces one person may create (self-service); operators provision more.</summary>
    public int MaxWorkspacesPerPerson { get; set; } = 5;

    public sealed class DatabaseSettings
    {
        public string App { get; set; } = "";

        public string? Migrator { get; set; }
    }

    /// <summary>Notification delivery (ADR 0021): provider hosts channel instances may use live, and the fan-out cap.</summary>
    public sealed class NotificationSettings
    {
        /// <summary>SMTP hosts allowed for live email (Esmbler__Notifications__AllowedEmailHosts__0=smtp.example.com). Empty: capture only.</summary>
        public List<string> AllowedEmailHosts { get; set; } = [];

        public List<string> AllowedSmsHosts { get; set; } = ["api.twilio.com"];

        /// <summary>IMAP hosts workspace mailboxes may poll (ADR 0023, Esmbler__Notifications__AllowedImapHosts__0=imap.example.com). Empty: webhook mailboxes only.</summary>
        public List<string> AllowedImapHosts { get; set; } = [];

        public int MaxRecipientsPerEvent { get; set; } = 200;
    }

    /// <summary>The MCP server as a remote connector (ADR 0025).</summary>
    public sealed class McpSettings
    {
        /// <summary>
        /// Canonical MCP resource URI (for example https://mcp.example.com/mcp). When set, it is the audience of every MCP
        /// token and OAuth for external clients is on; when empty (development, CI) the audience is esmbler-mcp and OAuth is off.
        /// </summary>
        public string? PublicUrl { get; set; }

        /// <summary>Hosts whose Client ID Metadata Documents are accepted (Esmbler__Mcp__AllowedClientHosts__0=chatgpt.com).</summary>
        public List<string> AllowedClientHosts { get; set; } = ["chatgpt.com", "claude.ai"];
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
