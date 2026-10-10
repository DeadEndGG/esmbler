// Types mirroring the API DTOs (camelCase JSON). Kept deliberately structural: the server is authoritative.

export interface Workspace {
  tenantId: string;
  slug: string;
  name: string;
  environments: { environmentId: string; key: string; name: string; kind: string }[];
}

export interface NavItem {
  entity: string;
  label: string;
  pluralLabel: string;
  listView?: string;
  recordView?: string;
  quickCreateView?: string;
  canCreate: boolean;
  /** The entity receives catalog requests: "New" opens the service catalog. */
  requests?: boolean;
}

export interface AppBootstrap {
  tenant: { id: string; slug: string; name: string };
  environment: { id: string; key: string; name: string; kind: string };
  user: { id: string; displayName: string; email?: string; agent?: string };
  roles: string[];
  actions: string[];
  configRevision: number;
  modules: { id: string; version: string }[];
  navigation: NavItem[];
  workspaces: Workspace[];
  /** The workspace has a service catalog the user may request from. */
  catalog?: boolean;
  /** The workspace runs approvals (ADR 0016); shows the Approvals inbox. */
  approvals?: boolean;
  /** Dashboards the user can see at least one figure on (ADR 0019). */
  dashboards?: DashboardNavItem[];
}

export interface DashboardNavItem {
  view: string;
  label: string;
  entity: string;
}

/** A metric as the view needs it: what it counts, so saved views and tiles can drill into the same query. */
export interface MetricClientView {
  id: string;
  label: string;
  entity: string;
  filter?: string | null;
  sla: string[];
  measure: string;
  format?: string | null;
  groupBy?: string | null;
}

/** The explorer query a figure counts: open it to see exactly those records. */
export interface DrillQuery {
  entity: string;
  filters: string[];
  sla: string[];
  label: string;
}

export interface MetricBucket {
  key: string;
  label: string;
  value?: number | null;
  count: number;
  drill: DrillQuery;
}

/** A metric evaluated for the signed-in person (ADR 0019). */
export interface MetricResult {
  id: string;
  label: string;
  status: 'ok' | 'restricted' | 'timeout';
  measure: string;
  format?: string | null;
  unit?: string | null;
  value?: number | null;
  count: number;
  capped: boolean;
  buckets: MetricBucket[];
  moreGroups: boolean;
  drill?: DrillQuery | null;
  asOf: string;
  configRevision: number;
}

export interface DashboardData {
  view: string;
  configRevision: number;
  metrics: MetricResult[];
}

/** An explorer group header. */
export interface GroupCount {
  key?: string | null;
  label: string;
  count: number;
  filter: string;
}

/** One approver's step in a stage (ADR 0016). */
export interface ApprovalStep {
  id: string;
  approverKind: 'user' | 'group' | 'role';
  approver: string;
  status: 'pending' | 'approved' | 'rejected' | 'expired' | 'cancelled' | 'skipped';
  decidedBy?: string | null;
  onBehalfOf?: string | null;
  comment?: string | null;
  decidedAt?: string | null;
  dueAt?: string | null;
  escalation: boolean;
  canDecide: boolean;
}

export interface ApprovalStage {
  id: string;
  label: string;
  mode: 'all' | 'any';
  status: 'waiting' | 'pending' | 'approved' | 'rejected' | 'skipped' | 'cancelled';
  steps: ApprovalStep[];
}

/** An SLA timer of a record (ADR 0018). Seconds are business or elapsed seconds, as the goal is measured. */
export interface SlaTimer {
  id: string;
  policyId: string;
  policyLabel: string;
  targetId: string;
  label: string;
  cycle: number;
  status: 'running' | 'paused' | 'stopped' | 'cancelled';
  outcome: 'met' | 'breached' | null;
  goalMinutes: number;
  calendar: string | null;
  timeZone: string | null;
  startedAt: string;
  dueAt: string | null;
  breachedAt: string | null;
  stoppedAt: string | null;
  elapsedSeconds: number;
  remainingSeconds: number;
  atRisk: boolean;
}

export interface RecordApproval {
  id: string;
  policyId: string;
  policyLabel: string;
  status: 'pending' | 'approved' | 'rejected' | 'cancelled';
  stageIndex: number;
  outcomeReason?: string | null;
  createdAt: string;
  decidedAt?: string | null;
  stages: ApprovalStage[];
}

export interface ApprovalInboxItem {
  stepId: string;
  approvalId: string;
  entity: string;
  recordId: string;
  policy: string;
  stage: string;
  status: ApprovalStep['status'];
  dueAt?: string | null;
  escalation: boolean;
  requester?: string | null;
  onBehalfOf?: string | null;
  comment?: string | null;
  decidedAt?: string | null;
  record?: RecordView | null;
}

export interface ApprovalInbox {
  pending: ApprovalInboxItem[];
  decided: ApprovalInboxItem[];
}

export interface Delegation {
  id: string;
  delegatorId: string;
  delegator: string;
  delegateId: string;
  delegate: string;
  startsAt: string;
  endsAt: string;
  active: boolean;
}

export interface VariableSpec {
  key: string;
  label: string;
  type: string;
  help?: string;
  required?: boolean;
  choiceList?: string;
  maxLength?: number;
  min?: number;
  max?: number;
  scale?: number;
  default?: unknown;
}

export interface CatalogStep {
  id: string;
  title: string;
  dependsOn: string[];
  conditional: boolean;
}

export interface CatalogItem {
  id: string;
  label: string;
  description?: string;
  category?: string;
  icon?: string;
  requestEntity: string;
  active: boolean;
  order: number;
  variables: VariableSpec[];
  steps: CatalogStep[];
}

export interface CatalogView {
  items: CatalogItem[];
  choiceLists: ChoiceList[];
  /** Sent back on submit so a form opened before a catalog change gets a reload conflict, not stale-question errors. */
  configRevision: number;
}

export interface ChoiceOption {
  id: string;
  label: string;
  tone?: string;
  hidden?: boolean;
}

export interface ChoiceList {
  id: string;
  label: string;
  options: ChoiceOption[];
}

export interface FieldSpec {
  key: string;
  type: string;
  label: string;
  description?: string;
  help?: string;
  required?: boolean;
  readOnly?: boolean;
  hidden?: boolean;
  deprecated?: boolean;
  system?: boolean;
  maxLength?: number;
  minLength?: number;
  min?: number;
  max?: number;
  scale?: number;
  pattern?: string;
  patternMessage?: string;
  choiceList?: string;
  target?: string;
  default?: unknown;
  prefix?: string;
}

export interface EntitySpec {
  id: string;
  label: string;
  pluralLabel: string;
  titleField: string;
  fields: FieldSpec[];
}

export interface RuleAction {
  type: string;
  field?: string;
  value?: string;
  options?: string[];
  message?: string;
  tone?: string;
}

export interface RuleSpec {
  id: string;
  kind: string;
  scope: string;
  entity: string;
  view?: string;
  description?: string;
  when?: string;
  assert?: string;
  message?: string;
  field?: string;
  actions: RuleAction[];
  order: number;
  enabled: boolean;
}

export interface WorkflowSpec {
  id: string;
  stateField: string;
  initialState: string;
  states: { id: string; category: string }[];
  transitions: { id: string; label: string; from: string[]; to: string; tone?: string; requiredFields: string[] }[];
}

export interface RelationshipSpec {
  id: string;
  entity: string;
  field: string;
  target: string;
  label: string;
  inverseLabel: string;
}

export interface EntityClientSpec {
  entity: EntitySpec;
  fieldAccess: Record<string, 'hidden' | 'read' | 'write'>;
  choiceLists: ChoiceList[];
  relationships: RelationshipSpec[];
  workflow?: WorkflowSpec;
  rules: RuleSpec[];
  customized: string[];
  configRevision: number;
  catalogItems?: CatalogItem[];
}

export interface NodeSpec {
  id: string;
  type: string;
  props: Record<string, any>;
  children: NodeSpec[];
  hidden?: boolean;
}

export interface ViewSpec {
  id: string;
  kind: string;
  entity: string;
  label: string;
  layout: NodeSpec;
}

export interface ViewClientSpec {
  view: ViewSpec;
  entity: EntityClientSpec;
  relatedEntities: Record<string, EntityClientSpec>;
  configRevision: number;
  /** Metrics the view's tiles and saved views use. */
  metrics?: Record<string, MetricClientView> | null;
}

export interface FieldStates {
  required: string[];
  readOnly: string[];
  hidden: string[];
  options: Record<string, string[]>;
  messages: { rule: string; tone: string; text: string; field?: string }[];
}

export interface RecordView {
  id: string;
  entity: string;
  number?: string;
  revision: number;
  configRevision: number;
  data: Record<string, any>;
  display: Record<string, string>;
  access?: { canUpdate: boolean; canDelete: boolean; fields: Record<string, string> };
  transitions?: { id: string; label: string; tone?: string; requiredFields: string[] }[];
  fieldStates?: FieldStates;
  messages?: { rule: string; tone: string; text: string; field?: string }[];
  createdAt: string;
  updatedAt: string;
}

export interface RecordPage {
  items: RecordView[];
  total: number;
  totalIsCapped: boolean;
  offset: number;
  pageSize: number;
  configRevision: number;
}

export interface ActivityView {
  id: string;
  kind: 'public_reply' | 'work_note' | 'system';
  body: string;
  author?: string;
  authorName?: string;
  byAgent: boolean;
  createdAt: string;
}

export interface HistoryEntry {
  id: number;
  at: string;
  operation: string;
  actor?: string;
  byAgent: boolean;
  configRevision?: number;
  changes: { field: string; label: string; from: unknown; to: unknown }[];
}

export interface DraftStep {
  seq: number;
  op: string;
  summary: string;
  targets: string[];
  source: string;
  byAgent: boolean;
  actor?: string;
  undone: boolean;
  at: string;
}

export interface Diagnostic {
  severity: 'error' | 'warning';
  code: string;
  message: string;
  target?: string;
  suggestion?: string;
}

export interface ConfigChange {
  change: 'added' | 'removed' | 'changed';
  kind: string;
  id: string;
  label: string;
  properties: string[];
}

export interface DraftState {
  id: string;
  version: number;
  baseRevision: number;
  publishedRevision: number;
  steps: DraftStep[];
  diagnostics: Diagnostic[];
  changes: ConfigChange[];
  artifactHash?: string;
  permissionImpact: boolean;
  workflowImpact?: WorkflowImpact[];
  approvalImpact?: ApprovalImpact[];
  slaImpact?: SlaImpact[];
}

/** What publishing a changed workflow does to running records (REQ-MIG-003). */
/** Approvals in flight under a policy the draft changes; they keep the policy they started with (ADR 0016). */
export interface SlaImpact {
  policy: string;
  policyLabel: string;
  running: number;
}

export interface ApprovalImpact {
  policy: string;
  policyLabel: string;
  pending: number;
}

export interface WorkflowImpact {
  workflow: string;
  entity: string;
  entityLabel: string;
  policy: 'pin' | 'migrate' | 'cancel';
  running: number;
  moving: number;
  stayingPinned: number;
  unmappedStates: string[];
}

export interface DraftViewSpec {
  spec: ViewClientSpec;
  draftId?: string;
  draftVersion: number;
  changedTargets: string[];
  steps: DraftStep[];
  isDraft: boolean;
}

export interface PublishRequestView {
  id: string;
  draftVersion: number;
  artifactHash: string;
  summary: string;
  status: string;
  requestedByAgent: boolean;
  requestedBy?: string;
  createdAt: string;
  readiness: { severity: string; code: string; message: string; target?: string }[];
  changes: ConfigChange[];
  permissionImpact: boolean;
}

export interface Problem {
  status: number;
  title: string;
  code: string;
  target?: string;
  suggestion?: string;
  errors?: { field: string; code: string; message: string; suggestion?: string }[];
  details?: any;
  traceId?: string;
}
