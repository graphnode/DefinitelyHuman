namespace IrcBouncer.Config;

public struct BouncerConfig
{
    public string UpstreamHost { get; set; }
    public int UpstreamPort { get; set; }
    public int ListenPort { get; set; }
    public int BufferSize { get; set; }
}