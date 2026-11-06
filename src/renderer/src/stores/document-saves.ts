/**
 * 文档保存的**在途注册表**。
 *
 * 为什么单独一个模块:保存这件事有两个读者。
 *
 * 1. `stores/documents.ts` 自己 —— 同一次保存被连点两下要复用同一个 promise
 *    (而不是发两次写盘),而且**判重的键必须是「这一次保存」的身份,不是路径**:
 *    文件在保存途中被改名/移动后,旧路径上的那次保存仍然在飞,它和「新路径上的
 *    新一次保存」是两件事,不能互相顶替。
 * 2. `actions/workspace-files.ts` 的破坏性操作闸门 —— 删/改名前要等这些保存落定,
 *    否则「磁盘已经删了,而渲染层刚把旧内容写回去」。
 *
 * ★ 两份索引各有各的用途,缺一不可:
 * - **按身份**(`requestId`)是保存回执的归属判据,改名搬家后依然找得到;
 * - **按 (工作区, 路径)** 是闸门的判据 —— 它必须在**条目已经被删掉之后**也查得到
 *   那次还在飞的写盘。只按身份查的话,`applyMutation` 一删条目,闸门就再也看不到
 *   它,于是「删除时等保存落定」成了空等。
 *
 * ★ 放这里而不是写回 `documents`:`actions` 也要读它,而 `documents` 反过来依赖
 * `actions` 就是这次要拆的那个环。这里谁都不依赖。
 *
 * ★ 这里**只登记,不参与判定**:保存成败由 `documents` 决定;注册表在 promise
 * 落定后自己清掉对应项。
 */
export type DocumentSavePromise = Promise<boolean>

const inFlight = new Map<string, DocumentSavePromise>()
const inFlightByPath = new Map<string, Set<DocumentSavePromise>>()

const pathKey = (workspaceId: string, path: string): string => JSON.stringify([workspaceId, path])

/**
 * 登记一次保存。`identity` 是文档身份(`requestId`),`path` 是**请求发出那一刻**的路径。
 * 返回的 promise 落定时两份索引一起清 —— 比对引用,防止更晚的一次写覆盖掉已完成
 * 的那条留下的空位。
 */
export function trackDocumentSave(
  identity: string,
  request: { workspaceId: string; path: string },
  promise: DocumentSavePromise
): void {
  const key = pathKey(request.workspaceId, request.path)
  inFlight.set(identity, promise)
  const atPath = inFlightByPath.get(key) ?? new Set<DocumentSavePromise>()
  atPath.add(promise)
  inFlightByPath.set(key, atPath)
  const cleanup = (): void => {
    if (inFlight.get(identity) === promise) inFlight.delete(identity)
    atPath.delete(promise)
    if (atPath.size === 0 && inFlightByPath.get(key) === atPath) inFlightByPath.delete(key)
  }
  void promise.then(cleanup, cleanup)
}

export function pendingDocumentSave(identity: string): DocumentSavePromise | undefined {
  return inFlight.get(identity)
}

/** 某个路径上还在飞的保存 —— 供破坏性操作闸门用,与条目是否还在无关。 */
export function pendingDocumentSaveAtPath(workspaceId: string, path: string): DocumentSavePromise | undefined {
  const pending = inFlightByPath.get(pathKey(workspaceId, path))
  if (pending === undefined) return undefined
  if (pending.size === 1) return pending.values().next().value
  return Promise.all(pending).then((saved) => saved.every(Boolean))
}

/** 目录变更也必须等待子文件；条目已被远端删除后仍能按请求原路径找到它。 */
export function pendingDocumentSavesWithin(workspaceId: string, parent: string): DocumentSavePromise[] {
  const pending = new Set<DocumentSavePromise>()
  for (const [key, saves] of inFlightByPath) {
    const [owner, path] = JSON.parse(key) as [string, string]
    if (owner === workspaceId && (path === parent || path.startsWith(`${parent}/`))) {
      for (const save of saves) pending.add(save)
    }
  }
  return [...pending]
}
