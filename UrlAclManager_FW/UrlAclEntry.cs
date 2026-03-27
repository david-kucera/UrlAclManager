using System;

namespace UrlAclManager_FW
{
    public class UrlAclEntry
    {
        public string Url { get; set; } = string.Empty;
        public string User { get; set; } = "Everyone";
        public DateTime RegisteredAt { get; set; } = DateTime.Now;
    }
}
