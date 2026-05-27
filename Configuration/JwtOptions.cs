namespace FlowableWrapper.Configuration
{
    public sealed class JwtOptions
    {
        public const string SectionName = "Jwt";

        public string Mode { get; set; } = "Oidc";

        public string Authority { get; set; } = string.Empty;

        public bool RequireHttpsMetadata { get; set; } = true;

        public string UseridClaim { get; set; } = "userid";

        public IList<string> FallbackUseridClaims { get; set; } =
            new List<string> { "sub", "employee_id", "uid" };
    }
}
