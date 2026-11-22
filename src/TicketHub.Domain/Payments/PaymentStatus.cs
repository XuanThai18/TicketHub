namespace TicketHub.Domain.Payments;

public enum PaymentStatus
{
    /// <summary>Đã mở lượt thanh toán, chờ cổng báo kết quả.</summary>
    Pending,

    /// <summary>Cổng báo trả thành công và đơn đã chuyển Paid.</summary>
    Succeeded,

    /// <summary>Cổng báo trả thất bại; đơn vẫn Pending, có thể thanh toán lại trước khi hết hạn.</summary>
    Failed,

    /// <summary>Tiền đã về nhưng đơn không còn Pending (hết hạn/đã huỷ, vé đã trả kho): phải hoàn tiền.</summary>
    RefundRequired
}
