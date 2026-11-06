/**
 * 需求：文档安全预检必须先于关窗，重复退出只做一次预检，窗口否决后恢复文档调用。
 * 本模块只拥有预检的在途状态和释放函数，不关闭窗口、不保存文档、不改变 QuitFlow 的两段式退出。
 * 丢弃只能来自确认回调的明确 true；异常或取消都不能被当成用户同意。
 */
export class DocumentQuitGuard {
  private pending = false
  private generation = 0
  private release: (() => void) | null = null

  constructor(private readonly deps: {
    isQuitting: () => boolean
    acquire: (discardChanges: boolean) => Promise<(() => void) | null>
    confirmDiscard: (error: unknown) => Promise<boolean>
    begin: () => void
  }) {}

  get acquired(): boolean { return this.release !== null }

  async request(): Promise<void> {
    if (this.pending || this.deps.isQuitting()) return
    this.pending = true
    const generation = this.generation
    try {
      let release: (() => void) | null
      try {
        release = await this.deps.acquire(false)
      } catch (error) {
        if (generation !== this.generation || !(await this.deps.confirmDiscard(error))) return
        if (generation !== this.generation) return
        release = await this.deps.acquire(true)
      }
      // 需求：预检等待期间也可能收到窗口否决，迟到的成功不能重新发起已经取消的退出。
      if (generation !== this.generation) {
        release?.()
        return
      }
      this.release = release
      try {
        this.deps.begin()
      } catch (error) {
        this.veto()
        throw error
      }
    } finally {
      this.pending = false
    }
  }

  /** 窗口拒绝 unload 后应用必须可继续编辑，而不是把文档闸门一直留着。 */
  veto(): void {
    this.generation += 1
    const release = this.release
    this.release = null
    release?.()
  }
}
