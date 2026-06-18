namespace FlowableWrapper.Configuration
{
    public sealed class ProcessNotificationOptions
    {
        public const string SectionName = "ProcessNotification";

        public bool Enabled { get; set; }

        public string MessageCenterBaseUrl { get; set; } = string.Empty;

        public string SendPath { get; set; } = "/api/message-center/send";

        public int TimeoutSeconds { get; set; } = 10;

        public string TokenEndpoint { get; set; } = string.Empty;

        public string ClientId { get; set; } = string.Empty;

        public string ClientSecret { get; set; } = string.Empty;

        public string BusinessType { get; set; } = "process_task";

        public string TaskUrlTemplate { get; set; } = "/process/tasks/{taskId}";
    }
}
