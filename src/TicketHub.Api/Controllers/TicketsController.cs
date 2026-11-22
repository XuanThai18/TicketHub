using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.Application.Common.Paging;
using TicketHub.Application.Tickets;

namespace TicketHub.Api.Controllers;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/tickets")]
public sealed class TicketsController(ISender sender) : BaseApiController
{
    /// <summary>Vé của người đang đăng nhập (từ mọi đơn đã thanh toán), mới phát trước.</summary>
    [HttpGet("my")]
    [Authorize]
    [ProducesResponseType<PagedResult<MyTicketResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListMyTickets(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        HandleResult(await sender.Send(new ListMyTicketsQuery(page, pageSize), cancellationToken));
}
