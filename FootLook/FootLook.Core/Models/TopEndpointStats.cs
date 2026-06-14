namespace FootLook.Core.Models
{
    public class TopEndpointStats
    {
    public string Path { get; set; } = string.Empty;

        public int Count { get; set; }

        public double AverageDuration { get; set; }

        public int Failures { get; set; }
    }
}