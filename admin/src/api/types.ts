// Shapes returned by the .NET admin API (src/JobMatch.Api/Admin/AdminEndpoints.cs).

export interface Metric {
  value: number | null
  target: number
  higherIsBetter: boolean
  n: number
  note: string
  meetsTarget: boolean | null
}

export interface Funnel {
  consented: number
  profilesConfirmed: number
  matched: number
  rated: number
  interested: number
  connected: number
}

export interface Analytics {
  generatedUtc: string
  funnel: Funnel
  taskCompletionRate: Metric
  conversationEfficiency: Metric
  intentAccuracy: Metric
  entityExtractionPrecision: Metric
  matchingRelevance: Metric
  responseLatencyP95Seconds: Metric
  repairRate: number | null
  openHandoffs: number
  handoffCases: number
  nluFallbackRate: number | null
  nluLatencyP95Ms: number | null
  nluIntents: Record<string, number>
  syntheticMatchesRated: number
}

export interface Trade {
  id: string
  name: string
  skills: string[]
}

export interface Reference {
  trades: Trade[]
  areas: { label: string; district: string }[]
  payPeriods: string[]
  availabilities: string[]
  intents: string[]
}

export type JobStatus = 'Draft' | 'Open' | 'Closed'

export interface JobSummary {
  id: number
  title: string
  trade: string | null
  area: string | null
  district: string | null
  status: JobStatus
  isSynthetic: boolean
  createdUtc: string
  matches: number
  interested: number
  averageRating: number | null
}

export interface JobInput {
  title: string
  description: string | null
  tradeId: string
  skills: string[]
  area: string
  payMin: number | null
  payMax: number | null
  payPeriod: string | null
  availability: string | null
  employerName: string | null
}

export interface JobDetail extends JobInput {
  id: number
  district: string | null
  status: JobStatus
  isSynthetic: boolean
  createdUtc: string
}

export interface InterestedItem {
  matchId: number
  workerPhone: string
  workerTrade: string | null
  workerArea: string | null
  jobId: number
  jobTitle: string
  jobArea: string | null
  employer: string | null
  jobIsSynthetic: boolean
  interestedUtc: string
}

export interface HandoffItem {
  id: number
  workerPhone: string
  state: string
  createdUtc: string
  recentMessages: string[]
}

export interface AnnotationProgress {
  annotated: number
  eligible: number
  target: number
}

export interface AnnotationTask {
  messageId: number
  text: string
  state: string
  extractedSkills: string[]
  progress: AnnotationProgress
}

export interface AnnotationSummary {
  progress: AnnotationProgress
  intentAccuracy: Metric
  entityExtractionPrecision: Metric
  otherCount: number
  perIntent: { intent: string; annotated: number; correct: number }[]
  confusions: { annotated: string; predicted: string; count: number }[]
}

export interface Supply {
  minimumCount: number
  totalWorkers: number | null
  byTradeAndDistrict: { trade: string; district: string; workers: number | null }[]
  bySkill: { trade: string; skill: string; workers: number | null }[]
}
