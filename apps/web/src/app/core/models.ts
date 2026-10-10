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
