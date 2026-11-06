// 需求：文档资源由 Vite 原样内嵌，主进程类型检查也需认识这些文本导入。
declare module '*?raw' {
  const source: string
  export default source
}
