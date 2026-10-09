/**
 * Types of the SMS Dock client.
 *
 * The client returns plain objects exactly as the server sent them. The
 * interfaces describe their keys for editors and type checkers. All
 * timestamps are milliseconds since the Unix epoch. Fields that do not
 * apply are `null`; they are never left out. All timeouts and delays are
 * seconds.
 */

/** Protocol version this client speaks. */
export const API_PROTOCOL_VERSION: 1;
/** Default port of the WebSocket API. */
export const DEFAULT_API_PORT: 9443;
/** Largest frame the server accepts from a client. */
export const MAX_FRAME_BYTES: number;
/** Largest value of `limit`. */
export const MAX_PAGE_SIZE: 200;
/** Environment variable that holds the password. */
export const PASSWORD_ENV: 'SMSDOCK_PASSWORD';
/** Environment variable that holds the certificate fingerprint. */
export const FINGERPRINT_ENV: 'SMSDOCK_FINGERPRINT';
/** All event names of protocol version 1. */
export const EVENT_NAMES: readonly EventName[];
/** Version of this package. */
export const VERSION: string;

export type Direction = 'in' | 'out';

export type MessageStatus =
  | 'received'
  | 'queued'
  | 'sending'
  | 'sent'
  | 'delivered'
  | 'failed';

export type DeleteMode = 'hidden' | 'provider';

export type EventName =
  | 'sms.received'
  | 'sms.queued'
  | 'sms.sent'
  | 'sms.delivered'
  | 'sms.failed'
  | 'sms.deleted'
  | 'conversations.changed';

/**
 * One SMS.
 *
 * `id` is `null` only inside an `sms.received` event whose stored copy was
 * not found in time. `sentTimestamp` is set for incoming messages only.
 * `requestId` is set for messages sent through SMS Dock.
 */
export interface Message {
  id: number | null;
  conversationId: number | null;
  address: string;
  contact: string | null;
  direction: Direction;
  body: string;
  timestamp: number;
  sentTimestamp: number | null;
  read: boolean;
  status: MessageStatus;
  sim: number | null;
  requestId: string | null;
}

/**
 * All messages exchanged with one phone number.
 *
 * `snippet` is the newest message cut to 160 characters; `timestamp`,
 * `direction` and `sim` describe that newest message.
 */
export interface Conversation {
  id: number;
  address: string;
  contact: string | null;
  snippet: string;
  timestamp: number;
  direction: Direction;
  messageCount: number;
  unreadCount: number;
  sim: number | null;
}

/** One phone number of a contact. */
export interface ContactNumber {
  number: string;
  label: string | null;
}

/** One contact of the phone that has at least one number. */
export interface Contact {
  name: string;
  numbers: ContactNumber[];
}

/** One SIM. `subscriptionId` is the value used as `sim` elsewhere. */
export interface Sim {
  subscriptionId: number;
  slot: number;
  name: string;
  number: string | null;
  isDefault: boolean;
}

/** Name and version of the app and the protocol version it speaks. */
export interface AppInfo {
  name: string;
  version: string;
  protocolVersion: number;
}

/** Licence state. `remaining` is `null` when sending is unlimited. */
export interface Licence {
  enforced: boolean;
  licensed: boolean;
  dailyLimit: number;
  sentToday: number;
  remaining: number | null;
}

/** Android permissions of the app. */
export interface Permissions {
  readSms: boolean;
  sendSms: boolean;
  receiveSms: boolean;
  contacts: boolean;
  phoneState: boolean;
  notifications: boolean;
  [name: string]: boolean;
}

/** Result of `status`. */
export interface Status {
  app: AppInfo;
  sims: Sim[];
  permissions: Permissions;
  defaultSmsApp: boolean;
  deleteMode: DeleteMode;
  licence: Licence;
  popup: boolean;
}

/**
 * Result of `messages.send`.
 *
 * `to` is the number as it is sent, without the ignored characters.
 * `status` is `queued` for a new message; a repeated request returns the
 * current status, `failed` included.
 */
export interface SendResult {
  requestId: string;
  to: string;
  parts: number;
  status: MessageStatus;
}

/**
 * State of a message sent through SMS Dock.
 *
 * `status` is `queued` (handed to the phone), `sending` (some parts are
 * left), `sent`, `delivered` or `failed`. `source` tells who sent.
 */
export interface OutboxEntry {
  requestId: string;
  to: string;
  parts: number;
  partsSent: number;
  status: MessageStatus;
  errorCode: number | null;
  messageId: number | null;
  sim: number | null;
  source: 'web' | 'api' | 'app';
  createdAt: number;
}

/** Result of a delete. */
export interface DeleteResult {
  deleted: number;
  mode: DeleteMode;
}

/** Result of `conversations.markRead`: `{ok: true}`. */
export interface MarkReadResult {
  ok: boolean;
}

/** Result of `ping`: the time of the phone. */
export interface PingResult {
  time: number;
}

/**
 * The subscription in force after `subscribe` or `unsubscribe`. In
 * `events`, `*` is replaced by the names it stands for.
 */
export interface Subscription {
  events: EventName[];
  address: string | null;
}

/** One page of a list. `nextCursor` is `null` on the last page. */
export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

interface EventBase<Name extends EventName, Data> {
  event: Name;
  /** Counts all events of the phone; restarts at 1 with the app. */
  seq: number;
  data: Data;
}

/** An SMS arrived. */
export type SmsReceivedEvent = EventBase<'sms.received', { message: Message }>;

/** SMS Dock accepted a message for sending. */
export type SmsQueuedEvent = EventBase<
  'sms.queued',
  {
    requestId: string;
    to: string;
    parts: number;
    sim: number | null;
    source: 'web' | 'api' | 'app';
  }
>;

/** The network accepted a message; `requestId` is `null` if another app sent it. */
export type SmsSentEvent = EventBase<
  'sms.sent',
  {
    requestId: string | null;
    conversationId: number | null;
    to: string;
    message: Message | null;
  }
>;

/** The recipient's phone confirmed receipt. */
export type SmsDeliveredEvent = EventBase<
  'sms.delivered',
  { requestId: string | null; id: number | null; conversationId: number | null; to: string }
>;

/** Sending failed. */
export type SmsFailedEvent = EventBase<
  'sms.failed',
  {
    requestId: string | null;
    id: number | null;
    conversationId: number | null;
    to: string;
    errorCode: number | null;
    reason: string;
  }
>;

/** A message or (`id` null) a conversation was deleted. */
export type SmsDeletedEvent = EventBase<
  'sms.deleted',
  {
    id: number | null;
    conversationId: number | null;
    address: string | null;
    mode: DeleteMode;
  }
>;

/** Messages changed outside SMS Dock. */
export type ConversationsChangedEvent = EventBase<
  'conversations.changed',
  Record<string, never>
>;

/** An event: `{event: name, seq: number, data: {...}}`. */
export type SmsDockEvent =
  | SmsReceivedEvent
  | SmsQueuedEvent
  | SmsSentEvent
  | SmsDeliveredEvent
  | SmsFailedEvent
  | SmsDeletedEvent
  | ConversationsChangedEvent;

/** The event with a given name. */
export type EventOf<Name extends EventName> = Extract<SmsDockEvent, { event: Name }>;

/** Options of `new SmsDockClient(url, options)`. */
export interface SmsDockClientOptions {
  /** Password set in the app; default is `SMSDOCK_PASSWORD`. */
  password?: string;
  /** SHA-256 fingerprint of the certificate; default is `SMSDOCK_FINGERPRINT`. */
  fingerprint?: string;
  /** Skip the check of the certificate and warn on standard error. For tests only. */
  insecure?: boolean;
  /** Seconds to wait for a response. Default 30. */
  timeout?: number;
  /** Seconds to open the connection. Default 10. */
  connectTimeout?: number;
  /** Reconnect attempts after a lost connection; `0` none, `null` no limit. Default 5. */
  reconnectAttempts?: number | null;
  /** Seconds before the first attempt; doubled for each further one. Default 1. */
  reconnectDelay?: number;
  /** Upper limit of that delay. Default 10. */
  maxReconnectDelay?: number;
  /** Events kept while nobody reads them; then the oldest are dropped. Default 1000. */
  eventQueueSize?: number;
  /** How often a request is repeated after `rate_limited`. Default 2. */
  rateLimitRetries?: number;
  /** Called with the client after every reconnect; may be async. */
  onReconnect?: ReconnectHook | null;
}

export type ReconnectHook = (client: SmsDockClient) => unknown;

/** Milliseconds since the epoch, or a `Date`. */
export type Timestamp = number | Date;

export interface ConversationFilter {
  /** Search in text, numbers and contact names. */
  q?: string | null;
  /** Only conversations with unread messages. */
  unread?: boolean;
  /** Only this SIM (subscription id). */
  sim?: number | null;
}

export interface MessageFilter {
  /** Only messages from or to this number. */
  address?: string | null;
  /** Only this conversation; wins if `address` is given too. */
  conversationId?: number | null;
  /** Incoming, outgoing or both. Default `'all'`. */
  direction?: Direction | 'all';
  /** Search in the message text. */
  q?: string | null;
  /** Not older than this; sent as the field `from`. */
  since?: Timestamp | null;
  /** Not newer than this; sent as the field `to`. */
  until?: Timestamp | null;
  /** Only this SIM (subscription id). */
  sim?: number | null;
  /** Only unread messages. */
  unread?: boolean;
}

export interface ContactFilter {
  /** Search in names and numbers. */
  q?: string | null;
}

export interface PageOptions {
  /** Page size, 1 to 200. Default 50. */
  limit?: number;
  /** `nextCursor` of the previous page. */
  cursor?: string | null;
}

export interface IterateOptions {
  /** `limit` of each request, 1 to 200. Default 50. */
  pageSize?: number;
  /** End after that many items; without it all are yielded. */
  maxItems?: number | null;
}

export interface SendOptions {
  /** Subscription id; default is the SMS SIM of the phone. */
  sim?: number | null;
  /** Your own id, 1 to 64 characters of `A-Z a-z 0-9 . _ -`. */
  requestId?: string | null;
}

export interface WaitForResultOptions {
  /** Seconds; default is the timeout of the client. */
  timeout?: number;
  /** Wait for the delivery report; `sent` is not enough then. */
  delivered?: boolean;
  /** Longest pause between two questions, in seconds. Default 1. */
  pollInterval?: number;
}

export interface RequestOptions {
  /** Send again after a lost connection or `rate_limited`. Default `true`. */
  retry?: boolean;
  /** Seconds; default is the timeout of the client. */
  timeout?: number;
}

/** One authenticated connection to SMS Dock. */
export class SmsDockClient {
  constructor(url: string, options?: SmsDockClientOptions);

  /** The URL as given. */
  readonly url: string;
  timeout: number;
  connectTimeout: number;
  reconnectAttempts: number | null;
  reconnectDelay: number;
  maxReconnectDelay: number;
  readonly insecure: boolean;
  rateLimitRetries: number;
  /** Called with the client after every reconnect, or `null`. */
  onReconnect: ReconnectHook | null;
  /** `{name, version, protocolVersion}` of the app, known after the login. */
  serverInfo: AppInfo | null;
  /** Number of events dropped because the event queue was full. */
  droppedEvents: number;
  /** Number of connections opened so far, reconnects included. */
  connectionCount: number;

  /** Whether a connection is open and logged in. */
  readonly connected: boolean;
  /** The subscription in force, or `null` without one. */
  readonly subscription: Subscription | null;
  /** Number of events that wait in the queue. */
  readonly pendingEvents: number;
  /** The `epoch` of the last login; `null` before the first. */
  readonly epoch: number | null;
  /** Sequence number of the newest event the client knows of. */
  readonly latestSeq: number | null;
  /** Whether the app was restarted before the last reconnect. */
  readonly serverRestarted: boolean;
  /** Events of the phone during the last interruption; `null` after a restart. */
  readonly missedEvents: number | null;

  /** Opens the connection, checks the certificate and logs in. One attempt. */
  connect(): Promise<this>;
  /** Closes the connection. Queued events are kept. */
  close(): Promise<void>;
  [Symbol.asyncDispose](): Promise<void>;

  status(): Promise<Status>;
  ping(): Promise<PingResult>;
  conversations(options?: ConversationFilter & PageOptions): Promise<Page<Conversation>>;
  iterConversations(
    options?: ConversationFilter & IterateOptions,
  ): AsyncGenerator<Conversation, void, void>;
  messages(options?: MessageFilter & PageOptions): Promise<Page<Message>>;
  iterMessages(options?: MessageFilter & IterateOptions): AsyncGenerator<Message, void, void>;
  getMessage(messageId: number): Promise<Message>;
  send(to: string, body: string, options?: SendOptions): Promise<SendResult>;
  messageStatus(requestId: string): Promise<OutboxEntry>;
  waitForResult(requestId: string, options?: WaitForResultOptions): Promise<OutboxEntry>;
  deleteMessage(messageId: number): Promise<DeleteResult>;
  deleteConversation(conversationId: number): Promise<DeleteResult>;
  markRead(conversationId: number): Promise<MarkReadResult>;
  contacts(options?: ContactFilter & PageOptions): Promise<Page<Contact>>;
  iterContacts(options?: ContactFilter & IterateOptions): AsyncGenerator<Contact, void, void>;

  subscribe(
    events: EventName | '*' | Iterable<EventName | '*'>,
    options?: { address?: string | null },
  ): Promise<Subscription>;
  unsubscribe(events: EventName | '*' | Iterable<EventName | '*'>): Promise<Subscription>;
  /** Yields events as they arrive; ends after `timeout` seconds without one. */
  events(options?: { timeout?: number | null }): AsyncGenerator<SmsDockEvent, void, void>;
  waitForEvent<Name extends EventName>(
    name: Name,
    options?: { timeout?: number; predicate?: (event: EventOf<Name>) => boolean },
  ): Promise<EventOf<Name>>;
  waitForEvent(
    name: '*' | Iterable<EventName | '*'>,
    options?: { timeout?: number; predicate?: (event: SmsDockEvent) => boolean },
  ): Promise<SmsDockEvent>;

  /** Sends one request and returns the `result` of its response. */
  request<Result = Record<string, unknown>>(
    action: string,
    fields?: Record<string, unknown>,
    options?: RequestOptions,
  ): Promise<Result>;
  /** Sends a request without waiting and returns its id. */
  sendRequest(action: string, fields?: Record<string, unknown>): Promise<string>;
  /** Returns the result of a request sent with `sendRequest()`. */
  waitForResponse<Result = Record<string, unknown>>(
    requestId: string,
    options?: { timeout?: number },
  ): Promise<Result>;
}

/** Base class of all errors thrown by this package. */
export class SmsDockError extends Error {}

/** The server answered a request with an error. */
export class ApiError extends SmsDockError {
  constructor(code: string, message: string, details?: Record<string, unknown>);
  /** Error code of `API.md`, for example `invalid_number`. */
  readonly code: string;
  /** Other fields of the error, for example `{retryAfter: 30}`. */
  readonly details: Record<string, unknown>;
  /** Seconds to wait (`locked_out`, `rate_limited`), else `null`. */
  readonly retryAfter: number | null;
  /** Missing Android permission (`permission_missing`), else `null`. */
  readonly permission: string | null;
}

/** `invalid_password`, `locked_out` or `not_authenticated`. Never retried. */
export class AuthenticationError extends ApiError {}

/** The API needs the full licence of the app (`licence_required`). */
export class LicenceRequiredError extends ApiError {}

/** The server sent something that protocol version 1 does not allow. */
export class ProtocolError extends SmsDockError {}

/** The connection failed and could not be restored. */
export class ConnectionInterrupted extends SmsDockError {
  constructor(
    message: string,
    closeCode?: number | null,
    closeReason?: string | null,
    options?: { cause?: unknown },
  );
  /** Close code of the server, if it sent a close frame. */
  readonly closeCode: number | null;
  readonly closeReason: string | null;
}

/** The server presented a certificate other than the pinned one. */
export class CertificateMismatchError extends SmsDockError {
  constructor(expected: string, actual: string);
  /** The pinned fingerprint as `AB:CD:…`. */
  readonly expected: string;
  /** The fingerprint of the certificate the server presented. */
  readonly actual: string;
}

/** The time to wait is over. Not an `SmsDockError`. */
export class TimeoutError extends Error {
  constructor(message: string);
  readonly code: 'ETIMEDOUT';
}

/** Returns a SHA-256 fingerprint as 64 lower-case hex digits. */
export function normalizeFingerprint(fingerprint: string): string;
/** Returns a fingerprint the way the app shows it: `AB:CD:…`. */
export function formatFingerprint(fingerprint: string): string;
/** Returns the SHA-256 fingerprint of a DER certificate as `AB:CD:…`. */
export function certificateFingerprint(der: Uint8Array): string;
/** Connects to `host:port` and returns the fingerprint of its certificate. */
export function fetchFingerprint(
  host: string,
  port?: number,
  options?: { timeout?: number },
): Promise<string>;
