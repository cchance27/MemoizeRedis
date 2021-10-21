namespace MemoizeRedis
{
    public record Settings
    {
        public string Server { get; init; }
        public int TTL { get; init; }
    }
}