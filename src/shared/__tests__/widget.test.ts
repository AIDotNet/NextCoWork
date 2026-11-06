import { describe, expect, it } from 'vitest'
import { bindWidgetAction, inferWidgetFormat, resolveWorkspacePath, type WidgetAction } from '../domain/widget'

const action: WidgetAction = {
  id: 'run_tests',
  label: 'Run tests',
  tool: 'Bash',
  input: { command: 'npm test -- {{suite}} --shard={{shard}}', nested: ['{{suite}}'] },
  params: {
    suite: { type: 'enum', values: ['api', 'ui'] },
    shard: { type: 'number', min: 1, max: 4, integer: true }
  }
}

describe('bindWidgetAction', () => {
  it('把合法参数代进所有字符串叶子', () => {
    expect(bindWidgetAction(action, { suite: 'api', shard: 2 })).toEqual({
      command: 'npm test -- api --shard=2',
      nested: ['api']
    })
  })

  /** ★ 参数会进命令行 —— 枚举之外的值、越界数字、多余参数一律拒绝,不做"尽量代" */
  it('拒绝枚举之外的值(包括注入)', () => {
    expect(() => bindWidgetAction(action, { suite: 'api; rm -rf ~', shard: 1 })).toThrow(/one of/)
  })

  it('拒绝越界与非整数', () => {
    expect(() => bindWidgetAction(action, { suite: 'ui', shard: 5 })).toThrow()
    expect(() => bindWidgetAction(action, { suite: 'ui', shard: 1.5 })).toThrow()
    expect(() => bindWidgetAction(action, { suite: 'ui', shard: '1' })).toThrow()
  })

  it('拒绝未声明的参数', () => {
    expect(() => bindWidgetAction(action, { suite: 'ui', shard: 1, extra: 'x' })).toThrow(/Unknown parameter/)
  })

  it('没有参数的动作原样返回入参', () => {
    const fixed: WidgetAction = { id: 'r', label: 'R', tool: 'Read', input: { file_path: '/a/b' } }
    expect(bindWidgetAction(fixed, {})).toEqual({ file_path: '/a/b' })
  })
})

describe('inferWidgetFormat', () => {
  it('按开头认出三种格式', () => {
    expect(inferWidgetFormat('<svg viewBox="0 0 1 1"></svg>')).toBe('svg')
    expect(inferWidgetFormat('<div>hi</div>')).toBe('html')
    expect(inferWidgetFormat("import { Stack } from 'nextcowork/widget'\nexport default function W() {}")).toBe('tsx')
    expect(inferWidgetFormat('export default function W() { return null }')).toBe('tsx')
  })
})

describe('resolveWorkspacePath', () => {
  it('相对路径落进工作区', () => {
    expect(resolveWorkspacePath('/repo', 'src/a.ts')).toBe('/repo/src/a.ts')
    expect(resolveWorkspacePath('/repo/', './src/../b.ts')).toBe('/repo/b.ts')
  })

  it('跳出工作区的一律拒绝', () => {
    expect(resolveWorkspacePath('/repo', '../etc/passwd')).toBeNull()
    expect(resolveWorkspacePath('/repo', '/etc/passwd')).toBeNull()
    expect(resolveWorkspacePath('/repo', '/repo-evil/x')).toBeNull()
  })

  it('Windows 路径', () => {
    expect(resolveWorkspacePath('C:\\work\\repo', 'src\\a.ts')).toBe('C:\\work\\repo\\src\\a.ts')
    expect(resolveWorkspacePath('C:\\work\\repo', '..\\other\\a.ts')).toBeNull()
  })
})
