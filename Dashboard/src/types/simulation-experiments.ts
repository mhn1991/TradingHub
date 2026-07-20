export type ExperimentCalibrationMode =
  | 'Disabled'
  | 'ReuseSpecifiedArtifacts'
  | 'TrainFreshAndUseForHeldOutEvaluation'
  | 'TrainFreshPendingReviewOnly'

export type SimulationExperimentState =
  | 'Queued'
  | 'Running'
  | 'Paused'
  | 'Interrupted'
  | 'Completed'
  | 'Failed'
  | 'Cancelled'

export type SimulationExperimentStage =
  | 'Queued'
  | 'ResolvingProfiles'
  | 'PreparingDatasets'
  | 'TrainingWarmup'
  | 'Learning'
  | 'FreezingArtifacts'
  | 'EmbargoReady'
  | 'EvaluationWarmup'
  | 'Evaluating'
  | 'Aggregating'
  | 'Completed'
  | 'Failed'
  | 'Cancelled'

export interface CalibrationExperimentPolicy {
  mode: ExperimentCalibrationMode
  internalFolds: number
  internalEmbargoHours: number
  artifactIds: string[]
}

export interface StructuralPlaybookOptions {
  enabled?: boolean
  cciMode?: 'Disabled' | 'Soft' | 'Required'
  supplyDemandConfluence?: 'Disabled' | 'Preferred' | 'Required'
}

export interface StructuralConfluenceOptions {
  liquiditySweepReversal?: StructuralPlaybookOptions
  supplyDemandPullback?: StructuralPlaybookOptions
  liquidityBreakRetest?: StructuralPlaybookOptions
  [key: string]: unknown
}

export interface SimulationStrategyProfile {
  profileId: string
  revision: number
  name: string
  agent: {
    kind: 'LegacyProgressive' | 'ImprovedProgressive' | 'StructuralConfluence'
    progressive?: Record<string, unknown> | null
    structuralConfluence?: StructuralConfluenceOptions | null
  }
  instrument: { value: string }
  analysis: Record<string, unknown>
  runtime: { options: Record<string, unknown> }
  management: Record<string, unknown>
  calibration: CalibrationExperimentPolicy
  tags: string[]
  contentHash: string
}

export interface SimulationProfileReference {
  profileId: string
  revision: number
  isBaseline: boolean
}

export interface SimulationExperimentTimeline {
  learningFrom: string
  learningTo: string
  evaluationFrom: string
  evaluationTo: string
  trainingWarmupDays: number
  evaluationWarmupDays: number
  embargoDays: number
}

export interface SimulationExperimentParallelism {
  maxProfileGroups: number
  maxTotalStrategyWorkers: number
  maxHistoricalDownloadsPerBroker: number
  estimatedMemoryBudgetBytes?: number
}

export interface SimulationExperimentRequest {
  name: string
  description?: string
  timeline: SimulationExperimentTimeline
  profiles: SimulationProfileReference[]
  parallelism: SimulationExperimentParallelism
  allowInsufficientWarmup: boolean
  availableDataFrom?: string
}

export interface SimulationArtifactReference {
  artifactId: string
  kind: string
  contentHash: string
}

export interface SimulationExperimentComparisonRow {
  profileRunId: string
  profileName: string
  netProfit?: number | null
  netR?: number | null
  expectancy?: number | null
  maximumDrawdown?: number | null
  profitFactor?: number | null
  tradeCount: number
  differenceFromBaseline?: number | null
  warnings: string[]
}

export interface SimulationExperimentComparisonSummary {
  baselineProfileRunId?: string | null
  profiles: SimulationExperimentComparisonRow[]
  objective: string
}

export interface SimulationProfileRunProgress {
  profileRunId: string
  profileName: string
  stage: SimulationExperimentStage
  progressPercent: number
  learningJobId?: string | null
  evaluationJobId?: string | null
  artifacts: SimulationArtifactReference[]
  completedStages: SimulationExperimentStage[]
  result?: SimulationExperimentComparisonRow | null
  statusDetail?: string | null
  failureReason?: string | null
}

export interface SimulationResourceUsageSnapshot {
  parentExperiments: number
  profileGroups: number
  strategyWorkers: number
  historicalDownloadsByBroker: Record<string, number>
  estimatedMemoryBytes: number
}

export interface SimulationExperimentSnapshot {
  id: string
  revision: number
  name: string
  state: SimulationExperimentState
  stage: SimulationExperimentStage
  createdAt: string
  startedAt?: string | null
  updatedAt?: string | null
  completedAt?: string | null
  manifest: {
    timeline: SimulationExperimentTimeline
    datasetStatus: string
    datasetId?: string | null
    datasetHash?: string | null
  }
  profiles: SimulationProfileRunProgress[]
  resourceUsage: SimulationResourceUsageSnapshot
  warnings: string[]
  failureReason?: string | null
  comparison?: SimulationExperimentComparisonSummary | null
}

export interface ExperimentTimelineDraft {
  mode: 'relative' | 'explicit'
  learningFrom: string
  learningTo: string
  evaluationFrom: string
  evaluationTo: string
  learningMonths: number
  learningDays: number
  trainingWarmupDays: number
  evaluationWarmupDays: number
  embargoDays: number
}

export interface ExperimentDraft {
  name: string
  description: string
  timeline: ExperimentTimelineDraft
  profileReferences: SimulationProfileReference[]
  parallelism: SimulationExperimentParallelism
  allowInsufficientWarmup: boolean
  availableDataFrom: string
}
