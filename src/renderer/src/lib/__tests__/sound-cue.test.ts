import { describe, expect, it } from 'vitest'
import type { AgentEvent } from '../../../../shared/agent/event'
import type { PendingInteraction } from '../../../../shared/agent/interaction'
import { createSoundCueTracker, type SoundPreferences } from '../sound-cue'

const ALL_ON: SoundPreferences = { taskComplete: true, permissionApproval: true, planApproval: true }

function env(runId: string, events: AgentEvent[]): { runId: string; seq: number; events: AgentEvent[] } {
  return { runId, seq: events.length, events }
}

const permission: PendingInteraction = {
  kind: 'tool_permission', id: 'i1', runId: 'run-1', callId: 'c1', toolName: 'Bash',
  input: {}, readOnly: false, destructive: false, createdAt: 0
}
const plan: PendingInteraction = {
  kind: 'plan_approval', id: 'i2', runId: 'run-1', plan: '# plan', planId: 'p1', path: '/p.md', createdAt: 0
}
const ask: PendingInteraction = { kind: 'ask_user', id: 'i3', runId: 'run-1', questions: [], createdAt: 0 }

describe('createSoundCueTracker', () => {
  it('plays the completion cue only when a registered top-level run ends done', () => {
    const tracker = createSoundCueTracker()
    tracker.noteTopLevelRun('run-1')
    expect(tracker.cueOf(env('run-1', [{ type: 'run_end', status: 'done' }]), ALL_ON)).toBe('taskComplete')
  })

  // 子代理的 run_end 走同一条事件流;不区分的话一轮回复会响好几声。
  it('stays silent when an unregistered (subagent) run ends', () => {
    const tracker = createSoundCueTracker()
    tracker.noteTopLevelRun('run-1')
    expect(tracker.cueOf(env('run-1:sub:1', [{ type: 'run_end', status: 'done' }]), ALL_ON)).toBeNull()
  })

  it('does not treat aborted or failed runs as completed', () => {
    const tracker = createSoundCueTracker()
    tracker.noteTopLevelRun('a')
    tracker.noteTopLevelRun('b')
    expect(tracker.cueOf(env('a', [{ type: 'run_end', status: 'aborted' }]), ALL_ON)).toBeNull()
    expect(tracker.cueOf(env('b', [{ type: 'run_end', status: 'error' }]), ALL_ON)).toBeNull()
  })

  it('forgets a run after its run_end so a replayed end does not chime twice', () => {
    const tracker = createSoundCueTracker()
    tracker.noteTopLevelRun('run-1')
    tracker.cueOf(env('run-1', [{ type: 'run_end', status: 'done' }]), ALL_ON)
    expect(tracker.cueOf(env('run-1', [{ type: 'run_end', status: 'done' }]), ALL_ON)).toBeNull()
  })

  it('maps permission and plan requests to their own cues and leaves ask_user silent', () => {
    const tracker = createSoundCueTracker()
    expect(tracker.cueOf(env('run-1', [{ type: 'interaction_request', interaction: permission }]), ALL_ON)).toBe('permission')
    expect(tracker.cueOf(env('run-1', [{ type: 'interaction_request', interaction: plan }]), ALL_ON)).toBe('plan')
    expect(tracker.cueOf(env('run-1', [{ type: 'interaction_request', interaction: ask }]), ALL_ON)).toBeNull()
  })

  it('respects each toggle independently', () => {
    const tracker = createSoundCueTracker()
    tracker.noteTopLevelRun('run-1')
    const off: SoundPreferences = { taskComplete: false, permissionApproval: false, planApproval: true }
    expect(tracker.cueOf(env('run-1', [{ type: 'interaction_request', interaction: permission }]), off)).toBeNull()
    expect(tracker.cueOf(env('run-1', [{ type: 'interaction_request', interaction: plan }]), off)).toBe('plan')
    expect(tracker.cueOf(env('run-1', [{ type: 'run_end', status: 'done' }]), off)).toBeNull()
  })

  it('picks the most urgent cue when one batch carries several', () => {
    const tracker = createSoundCueTracker()
    tracker.noteTopLevelRun('run-1')
    const events: AgentEvent[] = [
      { type: 'interaction_request', interaction: plan },
      { type: 'interaction_request', interaction: permission },
      { type: 'run_end', status: 'done' }
    ]
    expect(tracker.cueOf(env('run-1', events), ALL_ON)).toBe('permission')
  })
})
