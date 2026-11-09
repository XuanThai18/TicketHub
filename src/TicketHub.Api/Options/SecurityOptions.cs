namespace TicketHub.Api.Options;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Tắt mặc định vì TLS thường kết thúc ở ingress/reverse proxy.</summary>
    public bool UseHttpsRedirection { get; init; }

    /// <summary>CIDR của proxy tin cậy (vd 10.0.0.0/8). Rỗng = tin mọi proxy, chỉ an toàn khi app không lộ trực tiếp ra Internet.</summary>
    public string[] KnownNetworks { get; init; } = [];
}
