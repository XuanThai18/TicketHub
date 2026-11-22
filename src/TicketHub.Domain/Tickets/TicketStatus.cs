namespace TicketHub.Domain.Tickets;

public enum TicketStatus
{
    /// <summary>Đã phát, chưa dùng.</summary>
    Valid,

    /// <summary>Đã soát vào cửa.</summary>
    Used,

    /// <summary>Bị huỷ (vd sự kiện huỷ, hoàn tiền).</summary>
    Cancelled
}
