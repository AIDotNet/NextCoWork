// Bound full-file reads for plan review and execution without blocking detailed plans.
export const PLAN_FILE_MAX_BYTES = 1024 * 1024

export type PlanApprovalAction =
  | 'approve_current'
  | 'approve_new_session'
  | 'request_revision'
  | 'reject'

export interface PlanFileRef {
  planId: string
  /** Workspace-relative path, always `.plan/<plan-id>.md`. */
  path: string
}

export type PlanExecutionRef = PlanFileRef

export interface PlanToolReceipt extends PlanFileRef {
  type: 'plan_file'
  action: PlanApprovalAction
  feedback?: string
}

export function planFilePath(planId: string): string {
  return `.plan/${planId}.md`
}
