export interface Page<T> {
  items: T[]
  offset: number
  limit: number
  total: number
}

export interface AgentDescriptor {
  agentTypeId: string
  displayName: string
  description: string
  requiredFeatureCapabilities: string[]
  supportedDeploymentModes: number[]
  defaultIntervals: number[]
  automaticDemoCertified: boolean
  automaticLiveCertified: boolean
}

export interface PolicySummary {
  policyId: string
  strategyId: string
  strategyVersion: string
  createdAt: string
  description: string | null
}

export interface PolicyRevisionSummary {
  policyRevisionId: string
  policyId: string
  revision: number
  status: number
  configurationHash: string
  createdAt: string
}

export interface BrokerAccountOption {
  brokerAccountId: string
  broker: string
  displayName: string
  environment: string
  isLive: boolean
  accountCurrency: string
}

export interface InstrumentOption {
  instrumentId: number
  canonicalKey: string
  displayName: string
  brokerSymbol: string
  baseCurrency: string | null
  quoteCurrency: string | null
}

export interface DeploymentPreflightRequest {
  brokerAccountId: string
  brokerEnvironment: string
  instrumentId: number
  policyRevisionId: string
  mode: number
  expectedConfigurationHash?: string
  expectedPackageHash?: string
  requireParityCertification: boolean
  deploymentAgentId?: string
}

export interface DeploymentPreflightGate {
  code: string
  passed: boolean
  message: string
}

export interface DeploymentPreflightResult {
  passed: boolean
  gates: DeploymentPreflightGate[]
  policyId: string | null
  strategyId: string | null
  configurationHash: string | null
  packageHash: string | null
  requiredIntervals: string[]
}

export interface DeploymentSummary {
  deploymentId: string
  brokerAccountId: string
  environment: string
  hostInstanceId: string
  status: number
  version: number
  requestedAt: string
  requestedBy: string
}

export interface DeploymentAgentDetail {
  deploymentAgentId: string
  deploymentId: string
  brokerAccountId: string
  policyRevisionId: string
  instrumentId: number
  strategyId: string
  agentMode: number
  status: number
  enabled: boolean
  packageHash: string
  createdAt: string
  startedAt: string | null
  stoppedAt: string | null
  faultCode: string | null
  faultMessage: string | null
}

export interface DeploymentEventDetail {
  eventId: number
  eventType: string
  occurredAt: string
  actorIdentity: string
  reasonCode: string | null
  detailJson: string | null
}

export interface DeploymentDetail {
  deployment: DeploymentSummary
  agents: DeploymentAgentDetail[]
  recentEvents: DeploymentEventDetail[]
}

export interface LifecycleMutationContext {
  actor: string
  reason: string
  controlToken: string
}

export const policyStatusNames = [
  'Research', 'Reviewed', 'Approved for Demo', 'Retired', 'Backtested', 'Validated',
  'Shadow certified', 'Demo certified', 'Live certified', 'Suspended',
]

export const deploymentStatusNames = [
  'Running', 'Stopped', 'Requested', 'Validating', 'Preparing', 'Warming up',
  'Reconciling', 'Paused', 'Draining', 'Faulted',
]

export const agentStatusNames = [
  'Requested', 'Validating', 'Preparing', 'Warming up', 'Running', 'Paused',
  'Draining', 'Stopped', 'Faulted', 'Validation failed',
]

export const agentModeNames: Record<number, string> = {
  0: 'Record only',
  1: 'Shadow',
  2: 'Manual approval',
  3: 'Automatic',
}
