// Ticket 7 — 5.000 người dùng ảo cùng mua 1.000 vé (mỗi đơn 1 vé).
// Chứng minh: đúng 1.000 đơn thành công (201), phần còn lại bị từ chối hết vé (409), 0 lỗi khác, không bán vượt.
import http from 'k6/http';
import { Counter, Trend } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://host.docker.internal:5315';
const TOTAL_TICKETS = 1000;
const BUYERS = 5000;
const ADMIN_SUBJECT = '11111111-1111-1111-1111-111111111111';
const JSON_HEADERS = { 'Content-Type': 'application/json' };

const created = new Counter('orders_created');
const soldOut = new Counter('orders_sold_out');
const unexpected = new Counter('orders_unexpected');
const placeOrderMs = new Trend('place_order_ms', true);

export const options = {
  scenarios: {
    // 5.000 lượt mua trong 10 giây (500/s): gần như đồng thời, nhưng không mở 5.000 kết nối TCP trong cùng một mili-giây.
    buy: {
      executor: 'constant-arrival-rate',
      rate: BUYERS / 10,
      timeUnit: '1s',
      duration: '10s',
      preAllocatedVUs: 500,
      maxVUs: BUYERS,
    },
  },
};

// Mỗi VU tự cấp một token (một lần, trước khi đo) để setup() không phải giữ 5.000 token trong bộ nhớ.
let myToken = null;
function tokenForThisVu() {
  if (myToken === null) {
    const subject = `00000000-0000-4000-8000-${String(__VU).padStart(12, '0')}`;
    myToken = http.post(`${BASE}/dev/token`, JSON.stringify({ subject, roles: ['User'] }), { headers: JSON_HEADERS })
      .json('accessToken');
  }
  return myToken;
}

export function setup() {
  const adminToken = http.post(`${BASE}/dev/token`,
    JSON.stringify({ subject: ADMIN_SUBJECT, roles: ['Admin'] }), { headers: JSON_HEADERS })
    .json('accessToken');
  const auth = { Authorization: `Bearer ${adminToken}`, ...JSON_HEADERS };

  const now = Date.now();
  const iso = (ms) => new Date(ms).toISOString();
  const day = 24 * 60 * 60 * 1000;
  const eventId = http.post(`${BASE}/api/v1/events`, JSON.stringify({
    name: `Load test ${now}`, description: null, venue: 'Nhà hát',
    startsAt: iso(now + 11 * day), endsAt: iso(now + 11 * day + 3 * 60 * 60 * 1000),
    saleStartsAt: iso(now - 60 * 60 * 1000), saleEndsAt: iso(now + 10 * day),
  }), { headers: auth }).json('id');

  http.post(`${BASE}/api/v1/events/${eventId}/ticket-types`, JSON.stringify({
    name: 'VIP', description: null, price: 500000, totalQuantity: TOTAL_TICKETS, maxQuantityPerOrder: 1,
  }), { headers: auth });
  http.post(`${BASE}/api/v1/events/${eventId}/publish`, null, { headers: auth });

  const ticketTypeId = http.get(`${BASE}/api/v1/events/${eventId}`).json('ticketTypes.0.id');
  return { eventId, ticketTypeId };
}

export default function (data) {
  const body = JSON.stringify({ lines: [{ ticketTypeId: data.ticketTypeId, quantity: 1 }] });
  const res = http.post(`${BASE}/api/v1/orders`, body, {
    headers: { Authorization: `Bearer ${tokenForThisVu()}`, ...JSON_HEADERS },
    tags: { name: 'place_order' },
  });
  placeOrderMs.add(res.timings.duration);

  if (res.status === 201) created.add(1);
  else if (res.status === 409) soldOut.add(1);
  else unexpected.add(1);
}

export function handleSummary(data) {
  const count = (name) => (data.metrics[name] ? data.metrics[name].values.count : 0);
  const summary = {
    total_tickets: TOTAL_TICKETS,
    buyers: BUYERS,
    event_id: data.setup_data ? data.setup_data.eventId : null,
    ticket_type_id: data.setup_data ? data.setup_data.ticketTypeId : null,
    iterations: count('iterations'),
    dropped_iterations: count('dropped_iterations'),
    orders_created: count('orders_created'),
    orders_sold_out: count('orders_sold_out'),
    orders_unexpected: count('orders_unexpected'),
    place_order_ms_p95: data.metrics.place_order_ms ? data.metrics.place_order_ms.values['p(95)'] : null,
    place_order_ms_avg: data.metrics.place_order_ms ? data.metrics.place_order_ms.values.avg : null,
  };
  const text = JSON.stringify(summary, null, 2) + '\n';
  return { '/results/summary.json': text, stdout: text };
}
