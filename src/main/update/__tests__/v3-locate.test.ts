import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { findV3App } from '../v3-locate'

const deps = (files: string[], over: Partial<Parameters<typeof findV3App>[0]> = {}) => ({
  platform: 'darwin', home: '/Users/u', localAppData: undefined, execPath: '/Volumes/Old/NextCoWork.app/Contents/MacOS/NextCoWork',
  exists: (p: string) => files.includes(p), ...over
})

describe('findV3App', () => {
  it('finds the Velopack-installed 3.x bundle in /Applications by its sq.version', () => {
    expect(findV3App(deps(['/Applications/NextCoWork.app/Contents/MacOS/sq.version']))).toEqual({ path: '/Applications/NextCoWork.app' })
    expect(findV3App(deps([join('/Users/u', 'Applications', 'NextCoWork.app', 'Contents', 'MacOS', 'sq.version')])))
      .toEqual({ path: join('/Users/u', 'Applications', 'NextCoWork.app') })
  })

  it('does not mistake a 2.x bundle (no sq.version) or the running app itself for 3.x', () => {
    expect(findV3App(deps(['/Applications/NextCoWork.app/Contents/MacOS/NextCoWork']))).toBeNull()
    expect(findV3App(deps(['/Applications/NextCoWork.app/Contents/MacOS/sq.version'], { execPath: '/Applications/NextCoWork.app/Contents/MacOS/NextCoWork' }))).toBeNull()
  })

  it('finds the Windows install under %LocalAppData%\\NextCoWork\\current', () => {
    const current = join('C:/Users/u/AppData/Local', 'NextCoWork', 'current')
    const found = findV3App(deps([join(current, 'sq.version'), join(current, 'NextCoWork.exe')], { platform: 'win32', localAppData: 'C:/Users/u/AppData/Local', execPath: 'C:/Users/u/AppData/Local/Programs/nextcowork/NextCoWork.exe' }))
    expect(found).toEqual({ path: join(current, 'NextCoWork.exe') })
    expect(findV3App(deps([join(current, 'NextCoWork.exe')], { platform: 'win32', localAppData: 'C:/Users/u/AppData/Local' }))).toBeNull()
  })

  it('cannot know where a Linux AppImage lives', () => {
    expect(findV3App(deps(['/opt/NextCoWork.AppImage'], { platform: 'linux' }))).toBeNull()
  })
})
