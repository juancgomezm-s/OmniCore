// Wire names are shared with OmniCore.Protocol/ExtensionProtocol.cs and protocol-v1.schema.json.
export const protocolVersion = '1.0' as const;
export const maximumFrameBytes = 262144;
export type ViewKind = 'tree' | 'list' | 'table' | 'form' | 'indicator' | 'details';
export interface ViewItem { id: string; label: string; parentId?: string | null; hasChildren?: boolean; detail?: string | null; data?: unknown; cells?: Record<string, unknown> | null }
export interface ViewColumn { id: string; label: string; valueType?: 'text' | 'number' | 'boolean'; alignment?: 'left' | 'right' | 'center' }
export interface ViewField { id: string; label: string; kind: 'text' | 'number' | 'boolean' | 'choice'; value?: unknown; required?: boolean; readOnly?: boolean; options?: string[] | null }
export interface ViewIndicator { id: string; label: string; state: 'unknown' | 'ready' | 'active' | 'warning' | 'error'; value?: number | null; unit?: string | null }
export interface ViewAction { id: string; label: string; requiresUserAuthority: boolean; itemId?: string | null; configurationKey?: string | null; configurationScope?: 'workspace' | 'user' | 'session'; enabled?: boolean; disabledReason?: string | null }
export interface ViewScope { workspaceId: string; sessionId: string | null; manifestSha256: string; packageSha256: string; snapshotId?: string | null }
export interface FormSubmission { extensionId: string; viewId: string; actionId: string; expectedRevision: number; scope: ViewScope; values: JsonObject; path?: string; search?: string }
export interface View { id: string; extensionId: string; kind: ViewKind; revision: number; state: 'ready' | 'partial' | 'unsupported' | 'error'; items: ViewItem[]; actions: ViewAction[]; diagnostic?: string | null; cursor?: string | null; reset?: boolean; removedIds?: string[] | null; metadata?: unknown; columns?: ViewColumn[] | null; fields?: ViewField[] | null; indicators?: ViewIndicator[] | null; itemOrder?: string[] | null; scope?: ViewScope | null }
export interface ViewRequest { extensionId: string; viewId: string; path?: string; search?: string; afterRevision?: number | null; clientKinds?: ViewKind[] | null }
export interface Initialize { protocolVersion: string; extensionId: string; capabilities: string[]; sessionId: string | null }
export type JsonObject = Record<string, unknown>;
export interface ActivityEvent { sequence: number; kind: string; text: string; runId: string | null; executionId: string | null; agent: string | null; files: string[]; activity: string; checkpointId: string | null; recoverable: boolean; extensionId?: string | null; toolId?: string | null; correlationId?: string | null; causationId?: string | null }
export interface ExtensionNotification { sequence: number; id: string; extensionId: string; sessionId: string | null; level: 'info' | 'warning' | 'error'; text: string; manifestSha256: string; packageSha256: string }
export interface ExtensionNotificationPage { nextSequence: number; gap: boolean; items: ExtensionNotification[] }
/** Read-only client DTO. Approval is a trusted human action, never an extension service. */
export interface ExtensionContextPreview { id: string; extensionId: string; contributor: string; version: string; workspaceId: string; sessionId: string; runId: string; manifestSha256: string; packageSha256: string; contentSha256: string; items: ExtensionContextText[]; approved: boolean; consentSha256?: string | null }
export interface ExtensionContextText { id: string; kind: string; content: string }
export interface ActivityPage { sessionId: string | null; nextSequence: number; highWater: number; gap: boolean; earliestAvailableSequence: number | null; items: ActivityEvent[] }
export interface CurrentSession { sessionId: string; runId: string | null; basedOnJournalSequence: number; title: string; mode: string | null; runState: string | null; recoveryBlocked: boolean; projectionUnavailable: boolean; activity: string | null }
export interface InvocationOrigin { origin: string; sessionId?: string | null; correlationId?: string | null }
export interface ToolInvocation extends InvocationOrigin { toolCallId?: string }
export interface ObserverEvent { readonly point: string; readonly hookId: string; readonly eventType?: string; readonly toolId?: string; readonly _context?: InvocationOrigin }
export interface PreHookInput extends ObserverEvent { readonly redactedArgumentsJson?: string }
export interface PostHookEvent extends ObserverEvent { readonly redactedArgumentsJson?: string }
export type PreHookDecision = { kind: 'continue' } | { kind: 'restrict'; deniedToolIds: string[] }
  | { kind: 'requireValidation'; validatorId: string; reason: string } | { kind: 'fail' };
